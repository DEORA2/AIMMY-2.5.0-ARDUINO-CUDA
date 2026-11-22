using static AILogic.MathUtil;

namespace Whales2.AILogic
{
    internal enum OutputFormat
    {
        // YOLOv5u/v8/v9/v11/v12: x, y, w, h, class scores...
        AnchorFree,
        // YOLOv5/v7 anchor-based: x, y, w, h, objectness, class scores...
        Objectness,
        // YOLOv10 and NMS-baked exports: x1, y1, x2, y2, score, class
        EndToEnd
    }

    internal sealed class ModelOutputLayout
    {
        private const int EndToEndValues = 6;
        // End-to-end exports keep a fixed top-K (300 by default); dense heads emit thousands.
        private const int MaxEndToEndBoxes = 1000;

        public OutputFormat Format { get; private init; }
        public bool ChannelsFirst { get; private init; }
        public int Channels { get; private init; }
        public int NumClasses { get; private init; }
        public int Rank { get; private init; }

        public int ClassStart => Format == OutputFormat.Objectness ? 5 : 4;

        public int BoxCount(ReadOnlySpan<int> dims)
        {
            var (a, b) = Axes(dims);
            return ChannelsFirst ? b : a;
        }

        public bool Matches(ReadOnlySpan<int> dims)
        {
            if (dims.Length != Rank) return false;
            var (a, b) = Axes(dims);
            return (ChannelsFirst ? a : b) == Channels;
        }

        public string Describe()
        {
            string format = Format switch
            {
                OutputFormat.Objectness => "YOLOv5/v7 (objectness)",
                OutputFormat.EndToEnd => "end-to-end / NMS",
                _ => "YOLOv8-style (anchor-free)"
            };
            return $"{format}, {NumClasses} class(es), {(ChannelsFirst ? "channels-first" : "channels-last")}";
        }

        // Rank-2 outputs are the same layouts with the batch axis dropped.
        private static (int a, int b) Axes(ReadOnlySpan<int> dims) =>
            dims.Length == 3 ? (dims[1], dims[2]) : (dims[0], dims[1]);

        /// <summary>
        /// Works out how to read a detection head's output. Returns false with a null
        /// error when the shape is too dynamic to decide yet (decide again from the
        /// first real output tensor), or false with an error when it can't be a detector.
        /// </summary>
        public static bool TryResolve(
            ReadOnlySpan<int> dims,
            int imageSize,
            int metadataClassCount,
            int extraChannels,
            out ModelOutputLayout? layout,
            out string? error)
        {
            layout = null;
            error = null;

            if (dims.Length != 2 && dims.Length != 3)
            {
                error = $"expected a 2D or 3D detection output but got {dims.Length}D";
                return false;
            }

            var (a, b) = Axes(dims);
            int cells = imageSize > 0 ? CalculateNumDetections(imageSize) : 0;

            if (b == EndToEndValues)
            {
                // [1, K, 6] can also be a transposed 2-class dense head; dense heads have
                // exactly one box (or three, anchor-based) per grid cell.
                if (a <= 0) return false;
                if (a <= MaxEndToEndBoxes && a != cells && a != 3 * cells)
                {
                    layout = new ModelOutputLayout
                    {
                        Format = OutputFormat.EndToEnd,
                        ChannelsFirst = false,
                        Channels = EndToEndValues,
                        NumClasses = Math.Max(metadataClassCount, 1),
                        Rank = dims.Length
                    };
                    return true;
                }
            }

            bool channelsFirst;
            int channels;
            int boxes;
            if (a > 0 && b > 0)
            {
                channelsFirst = a <= b;
                channels = channelsFirst ? a : b;
                boxes = channelsFirst ? b : a;
            }
            else if (a > 0)
            {
                channelsFirst = true;
                channels = a;
                boxes = -1;
            }
            else if (b > 0)
            {
                channelsFirst = false;
                channels = b;
                boxes = -1;
            }
            else
            {
                return false;
            }

            int payload = channels - extraChannels;
            bool objectness;
            if (boxes > 0 && cells > 0 && boxes == 3 * cells)
                objectness = true;
            else if (boxes > 0 && cells > 0 && boxes == cells)
                objectness = false;
            else if (metadataClassCount > 0 && payload == 5 + metadataClassCount)
                objectness = true;
            else
                objectness = false;

            int numClasses = payload - (objectness ? 5 : 4);
            if (numClasses < 1)
            {
                error = $"{channels} values per box is too few for a box plus class scores";
                return false;
            }

            layout = new ModelOutputLayout
            {
                Format = objectness ? OutputFormat.Objectness : OutputFormat.AnchorFree,
                ChannelsFirst = channelsFirst,
                Channels = channels,
                NumClasses = numClasses,
                Rank = dims.Length
            };
            return true;
        }
    }
}
