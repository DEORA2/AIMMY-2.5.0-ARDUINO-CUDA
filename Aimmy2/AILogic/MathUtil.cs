using Whales2.AILogic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace AILogic
{
    public static class MathUtil
    {
        public static Func<double[], double[], double> L2Norm_Squared_Double = (x, y) =>
        {
            double dist = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                dist += (x[i] - y[i]) * (x[i] - y[i]);
            }

            return dist;
        };
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(Prediction a, Prediction b)
        {
            float dx = a.ScreenCenterX - b.ScreenCenterX;
            float dy = a.ScreenCenterY - b.ScreenCenterY;
            return dx * dx + dy * dy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CalculateTargetScore(
            Prediction candidate,
            Prediction? currentTarget,
            float predictedX,
            float predictedY,
            float currentLockScore,
            float maxLockScore,
            float threshold)
        {
            // Base score from distance to predicted position (where we expect current target to be)
            float dx = candidate.ScreenCenterX - predictedX;
            float dy = candidate.ScreenCenterY - predictedY;
            float distSq = dx * dx + dy * dy;

            // Normalize distance score (0 = far, 1 = close)
            float thresholdSq = threshold * threshold;
            float distanceScore = Math.Max(0f, 1f - (distSq / thresholdSq));

            // Confidence bonus (0-0.3 range)
            float confidenceBonus = candidate.Confidence * 0.3f;

            // Size bonus - larger targets are more stable (0-0.2 range)
            float area = candidate.Rectangle.Width * candidate.Rectangle.Height;
            float sizeBonus = Math.Min(0.2f, area / 50000f);

            // Lock bonus for current target (0-0.5 range based on accumulated score)
            float lockBonus = (currentTarget != null && distanceScore > 0.3f)
                ? (currentLockScore / maxLockScore) * 0.5f
                : 0f;

            return distanceScore + confidenceBonus + sizeBonus + lockBonus;
        }
        public static int CalculateNumDetections(int imageSize)
        {
            // YOLOv8 detection calculation: (size/8)² + (size/16)² + (size/32)²
            int stride8 = imageSize / 8;
            int stride16 = imageSize / 16;
            int stride32 = imageSize / 32;

            return (stride8 * stride8) + (stride16 * stride16) + (stride32 * stride32);
        }
        // LUT = look up table
        // REFERENCE: https://stackoverflow.com/questions/1089235/where-can-i-find-a-byte-to-float-lookup-table
        // "In this case, the lookup table should be faster than using direct calculation. The more complex the math (trigonometry, etc.), the bigger the performance gain."
        // although we used small calculations, something is better than nothing.
        private static readonly float[] _byteToFloatLut = CreateByteToFloatLut();
        private static float[] CreateByteToFloatLut()
        {
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
                lut[i] = i / 255f;
            return lut;
        }

        /// <summary>Converts a locked 32bpp BGRA bitmap into planar RGB floats (0-1).</summary>
        public static unsafe void BitmapToFloatArrayInPlace(Bitmap image, Span<float> result, int IMAGE_SIZE)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            var bmpData = image.LockBits(new Rectangle(0, 0, IMAGE_SIZE, IMAGE_SIZE), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                BgraToPlanarRgb((byte*)bmpData.Scan0, Math.Abs(bmpData.Stride), IMAGE_SIZE, result, maskThirdPerson: false);
            }
            finally
            {
                image.UnlockBits(bmpData);
            }
        }

        /// <summary>
        /// Converts size x size BGRA pixels (any row stride, e.g. a mapped GPU texture) into the model's planar
        /// RGB float input (0-1), written straight into <paramref name="result"/>: no intermediate bitmap or copy.
        /// </summary>
        /// <param name="maskThirdPerson">Blacks out the bottom-left quarter, where a third-person character stands.</param>
        public static unsafe void BgraToPlanarRgb(byte* src, int srcStride, int size, Span<float> result, bool maskThirdPerson)
        {
            int totalPixels = size * size;
            if (result.Length != 3 * totalPixels)
                throw new ArgumentException($"result must be length {3 * totalPixels}", nameof(result));

            int maskStartY = size - size / 2;
            int maskWidth = size / 2;

            // prevent gc from moving the array while we are using it
            fixed (float* dest = result)
            {
                float* rPtr = dest;                    // planar layout: all R, then all G, then all B
                float* gPtr = dest + totalPixels;
                float* bPtr = dest + totalPixels * 2;
                float[] lut = _byteToFloatLut;

                // process rows in parallel (avoid creating one task per row)
                Parallel.For(0, size, new ParallelOptions { MaxDegreeOfParallelism = 4 }, y =>
                {
                    byte* p = src + (long)y * srcStride;
                    int rowStart = y * size;

                    // pixels are B, G, R, A; alpha is ignored
                    for (int x = 0; x < size; x++, p += 4)
                    {
                        int idx = rowStart + x;
                        bPtr[idx] = lut[p[0]];
                        gPtr[idx] = lut[p[1]];
                        rPtr[idx] = lut[p[2]];
                    }

                    if (maskThirdPerson && y >= maskStartY)
                    {
                        new Span<float>(rPtr + rowStart, maskWidth).Clear();
                        new Span<float>(gPtr + rowStart, maskWidth).Clear();
                        new Span<float>(bPtr + rowStart, maskWidth).Clear();
                    }
                });
            }
        }
    }
}
