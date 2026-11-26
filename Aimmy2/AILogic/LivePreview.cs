using System.Drawing;

namespace Whales2.AILogic
{
    /// <summary>
    /// Hands the model's view to the Home page's live view: the captured square (BGRA) and the detections found in it.
    /// Capture writes pixels into a back buffer; when the AI loop publishes that frame's detections the buffers swap,
    /// so the picture and its boxes always belong to the same frame. Nothing is copied unless <see cref="Active"/>.
    /// </summary>
    internal static class LivePreview
    {
        public readonly record struct Box(RectangleF Rect, float Confidence, string Label, bool IsTarget);

        /// <summary>Set by the Home page while the live view is on screen. While true and the user isn't aiming,
        /// the AI loop still runs the model, but only to show what it sees: it never aims, clicks or saves images.</summary>
        public static volatile bool Active;

        private static readonly object Lock = new();
        private static byte[] _back = Array.Empty<byte>();
        private static byte[] _front = Array.Empty<byte>();
        private static int _backSize, _frontSize;
        private static Box[] _boxes = Array.Empty<Box>();
        private static float _fovSize;
        private static long _frameId;

        /// <summary>Copies one captured frame (size x size BGRA, any row stride). Called on the AI thread.</summary>
        public static unsafe void WritePixels(byte* src, int srcStride, int size, bool maskThirdPerson)
        {
            int rowBytes = size * 4;
            lock (Lock)
            {
                if (_back.Length != rowBytes * size) _back = new byte[rowBytes * size];
                fixed (byte* dst = _back)
                {
                    for (int y = 0; y < size; y++)
                        Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * rowBytes, rowBytes, rowBytes);

                    if (maskThirdPerson)
                    {
                        // The model sees the bottom-left quarter blacked out, so show it that way too
                        for (int y = size - size / 2; y < size; y++)
                            new Span<byte>(dst + (long)y * rowBytes, size / 2 * 4).Clear();
                    }
                }
                _backSize = size;
            }
        }

        /// <summary>Publishes the frame just captured together with what the model found in it.</summary>
        public static void Publish(IReadOnlyList<Prediction> detections, Prediction? target, float fovSize)
        {
            // The model returns several overlapping boxes per target (aiming just takes the closest), so for display
            // keep only the most confident box of each overlapping group. The group containing the target stays the target.
            var kept = new List<(Prediction P, bool Target)>();
            foreach (var p in detections.OrderByDescending(d => d.Confidence))
            {
                int match = kept.FindIndex(k => IoU(k.P.Rectangle, p.Rectangle) > 0.45f);
                bool isTarget = ReferenceEquals(p, target);
                if (match < 0) kept.Add((p, isTarget));
                else if (isTarget) kept[match] = (kept[match].P, true);
            }

            var boxes = new List<Box>(kept.Count + 1);
            foreach (var (p, isTarget) in kept)
                boxes.Add(new Box(p.Rectangle, p.Confidence, p.ClassName, isTarget));
            if (target != null && target.IsPredicted)
                boxes.Add(new Box(target.Rectangle, 0, "held", true)); // sticky aim holding a briefly lost target

            lock (Lock)
            {
                if (_backSize == 0) return;
                (_front, _back) = (_back, _front);
                _frontSize = _backSize;
                _backSize = 0;
                _boxes = boxes.ToArray();
                _fovSize = fovSize;
                _frameId++;
            }
        }

        private static float IoU(RectangleF a, RectangleF b)
        {
            var inter = RectangleF.Intersect(a, b);
            if (inter.IsEmpty) return 0;
            float i = inter.Width * inter.Height;
            return i / (a.Width * a.Height + b.Width * b.Height - i);
        }

        /// <summary>
        /// Copies the latest published frame into <paramref name="pixels"/> (resized if needed) when it is newer than
        /// <paramref name="lastFrameId"/>. Called on the UI thread.
        /// </summary>
        public static bool TryCopy(ref long lastFrameId, ref byte[] pixels, out int size, out Box[] boxes, out float fovSize)
        {
            lock (Lock)
            {
                size = _frontSize;
                boxes = _boxes;
                fovSize = _fovSize;
                if (_frameId == lastFrameId || size == 0) return false;
                if (pixels.Length != _front.Length) pixels = new byte[_front.Length];
                Buffer.BlockCopy(_front, 0, pixels, 0, _front.Length);
                lastFrameId = _frameId;
                return true;
            }
        }

        /// <summary>Forgets the last frame (e.g. when a model is unloaded), so the view doesn't show a stale picture.</summary>
        public static void Clear()
        {
            lock (Lock)
            {
                _frontSize = _backSize = 0;
                _boxes = Array.Empty<Box>();
                _frameId++;
            }
        }
    }
}
