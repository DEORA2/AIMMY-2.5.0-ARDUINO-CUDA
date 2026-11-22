using System.Drawing;

namespace InputLogic
{
    // All paths return sub-pixel PointF; MouseManager carries the fractional remainder
    // between frames so small corrections aren't truncated away.
    class MovementPaths
    {
        private static readonly int[] permutation = new int[512];

        internal static PointF CubicBezier(PointF start, PointF end, PointF control1, PointF control2, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;

            double x = uu * u * start.X + 3 * uu * t * control1.X + 3 * u * tt * control2.X + tt * t * end.X;
            double y = uu * u * start.Y + 3 * uu * t * control1.Y + 3 * u * tt * control2.Y + tt * t * end.Y;

            return new PointF((float)x, (float)y);
        }

        internal static PointF Lerp(PointF start, PointF end, double t)
        {
            double x = start.X + (end.X - start.X) * t;
            double y = start.Y + (end.Y - start.Y) * t;
            return new PointF((float)x, (float)y);
        }

        internal static PointF Exponential(PointF start, PointF end, double t, double exponent = 2.0)
        {
            double x = start.X + (end.X - start.X) * Math.Pow(t, exponent);
            double y = start.Y + (end.Y - start.Y) * Math.Pow(t, exponent);
            return new PointF((float)x, (float)y);
        }

        internal static PointF Adaptive(PointF start, PointF end, double t, double threshold = 100.0)
        {
            double distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            if (distance < threshold)
            {
                return Lerp(start, end, t);
            }

            PointF control1 = new PointF(start.X + (end.X - start.X) / 3f, start.Y + (end.Y - start.Y) / 3f);
            PointF control2 = new PointF(start.X + 2f * (end.X - start.X) / 3f, start.Y + 2f * (end.Y - start.Y) / 3f);
            return CubicBezier(start, end, control1, control2, t);
        }

        internal static PointF SineWave(PointF start, PointF end, double t, double amplitude = 15.0, double frequency = 2.0)
        {
            double baseX = start.X + (end.X - start.X) * t;
            double baseY = start.Y + (end.Y - start.Y) * t;

            double perpX = -(end.Y - start.Y);
            double perpY = end.X - start.X;
            double perpLength = Math.Sqrt(perpX * perpX + perpY * perpY);

            if (perpLength > 0)
            {
                perpX /= perpLength;
                perpY /= perpLength;
            }

            double wave = Math.Sin(t * Math.PI * frequency) * amplitude * Math.Sin(t * Math.PI);

            return new PointF((float)(baseX + perpX * wave), (float)(baseY + perpY * wave));
        }

        internal static PointF EaseInOut(PointF start, PointF end, double t)
        {
            double eased = t < 0.5
                ? 2 * t * t
                : 1 - Math.Pow(-2 * t + 2, 2) / 2;

            double x = start.X + (end.X - start.X) * eased;
            double y = start.Y + (end.Y - start.Y) * eased;
            return new PointF((float)x, (float)y);
        }

        internal static PointF Overshoot(PointF start, PointF end, double t, double strength = 1.15)
        {
            double eased = 1 + (strength - 1) * Math.Sin(t * Math.PI);
            double x = start.X + (end.X - start.X) * t * eased;
            double y = start.Y + (end.Y - start.Y) * t * eased;
            return new PointF((float)x, (float)y);
        }

        internal static PointF CatmullRom(PointF start, PointF end, double t, double tension = 0.5)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;

            double p0X = start.X - dx * tension * 0.5;
            double p0Y = start.Y - dy * tension * 0.5;
            double p3X = end.X + dx * tension * 0.5;
            double p3Y = end.Y + dy * tension * 0.5;

            double t2 = t * t;
            double t3 = t2 * t;

            double x = 0.5 * ((2 * start.X) +
                       (-p0X + end.X) * t +
                       (2 * p0X - 5 * start.X + 4 * end.X - p3X) * t2 +
                       (-p0X + 3 * start.X - 3 * end.X + p3X) * t3);

            double y = 0.5 * ((2 * start.Y) +
                       (-p0Y + end.Y) * t +
                       (2 * p0Y - 5 * start.Y + 4 * end.Y - p3Y) * t2 +
                       (-p0Y + 3 * start.Y - 3 * end.Y + p3Y) * t3);

            return new PointF((float)x, (float)y);
        }

        internal static PointF SmoothStep(PointF start, PointF end, double t)
        {
            double eased = t * t * (3 - 2 * t);
            double x = start.X + (end.X - start.X) * eased;
            double y = start.Y + (end.Y - start.Y) * eased;
            return new PointF((float)x, (float)y);
        }

        internal static PointF Spiral(PointF start, PointF end, double t, double radius = 8.0)
        {
            double baseX = start.X + (end.X - start.X) * t;
            double baseY = start.Y + (end.Y - start.Y) * t;

            double decay = 1 - t;
            double angle = t * Math.PI * 4;
            double offsetX = Math.Cos(angle) * radius * decay;
            double offsetY = Math.Sin(angle) * radius * decay;

            return new PointF((float)(baseX + offsetX), (float)(baseY + offsetY));
        }

        internal static PointF PerlinNoise(PointF start, PointF end, double t, double amplitude = 10.0, double frequency = 0.1)
        {
            double baseX = start.X + (end.X - start.X) * t;
            double baseY = start.Y + (end.Y - start.Y) * t;

            double noiseX = Noise(t * frequency, 0) * amplitude;
            double noiseY = Noise(t * frequency, 100) * amplitude;

            double perpX = -(end.Y - start.Y);
            double perpY = end.X - start.X;
            double perpLength = Math.Sqrt(perpX * perpX + perpY * perpY);

            if (perpLength > 0)
            {
                perpX /= perpLength;
                perpY /= perpLength;
            }

            double finalX = baseX + perpX * noiseX + noiseY * 0.3;
            double finalY = baseY + perpY * noiseX + noiseY * 0.3;

            return new PointF((float)finalX, (float)finalY);
        }

        private static double Fade(double t)
        {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + t * (b - a);
        }

        private static double Grad(int hash, double x, double y)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : h == 12 || h == 14 ? x : 0;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        private static double Noise(double x, double y)
        {
            int X = (int)Math.Floor(x) & 255;
            int Y = (int)Math.Floor(y) & 255;

            x -= Math.Floor(x);
            y -= Math.Floor(y);

            double u = Fade(x);
            double v = Fade(y);

            int A = permutation[X] + Y;
            int AA = permutation[A];
            int AB = permutation[A + 1];
            int B = permutation[X + 1] + Y;
            int BA = permutation[B];
            int BB = permutation[B + 1];

            return Lerp(Lerp(Grad(permutation[AA], x, y),
                           Grad(permutation[BA], x - 1, y), u),
                      Lerp(Grad(permutation[AB], x, y - 1),
                           Grad(permutation[BB], x - 1, y - 1), u), v);
        }
    }
}
