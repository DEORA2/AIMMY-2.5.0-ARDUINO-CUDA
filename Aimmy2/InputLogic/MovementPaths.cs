using System.Drawing;

namespace InputLogic
{
    class MovementPaths
    {
        private static readonly int[] permutation = new int[512];

        internal static Point CubicBezier(Point start, Point end, Point control1, Point control2, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;

            double x = uu * u * start.X + 3 * uu * t * control1.X + 3 * u * tt * control2.X + tt * t * end.X;
            double y = uu * u * start.Y + 3 * uu * t * control1.Y + 3 * u * tt * control2.Y + tt * t * end.Y;

            return new Point((int)x, (int)y);
        }

        internal static Point Lerp(Point start, Point end, double t)
        {
            int x = (int)(start.X + (end.X - start.X) * t);
            int y = (int)(start.Y + (end.Y - start.Y) * t);
            return new Point(x, y);
        }

        internal static Point Exponential(Point start, Point end, double t, double exponent = 2.0)
        {
            double x = start.X + (end.X - start.X) * Math.Pow(t, exponent);
            double y = start.Y + (end.Y - start.Y) * Math.Pow(t, exponent);
            return new Point((int)x, (int)y);
        }

        internal static Point Adaptive(Point start, Point end, double t, double threshold = 100.0)
        {
            double distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            if (distance < threshold)
            {
                return Lerp(start, end, t);
            }
            else
            {
                Point control1 = new Point(start.X + (end.X - start.X) / 3, start.Y + (end.Y - start.Y) / 3);
                Point control2 = new Point(start.X + 2 * (end.X - start.X) / 3, start.Y + 2 * (end.Y - start.Y) / 3);
                return CubicBezier(start, end, control1, control2, t);
            }
        }

        internal static Point SineWave(Point start, Point end, double t, double amplitude = 15.0, double frequency = 2.0)
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

            return new Point((int)(baseX + perpX * wave), (int)(baseY + perpY * wave));
        }

        internal static Point EaseInOut(Point start, Point end, double t)
        {
            double eased = t < 0.5
                ? 2 * t * t
                : 1 - Math.Pow(-2 * t + 2, 2) / 2;

            double x = start.X + (end.X - start.X) * eased;
            double y = start.Y + (end.Y - start.Y) * eased;
            return new Point((int)x, (int)y);
        }

        internal static Point Overshoot(Point start, Point end, double t, double strength = 1.15)
        {
            double eased = 1 + (strength - 1) * Math.Sin(t * Math.PI);
            double x = start.X + (end.X - start.X) * t * eased;
            double y = start.Y + (end.Y - start.Y) * t * eased;
            return new Point((int)x, (int)y);
        }

        internal static Point CatmullRom(Point start, Point end, double t, double tension = 0.5)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;

            Point p0 = new Point(start.X - (int)(dx * tension * 0.5), start.Y - (int)(dy * tension * 0.5));
            Point p3 = new Point(end.X + (int)(dx * tension * 0.5), end.Y + (int)(dy * tension * 0.5));

            double t2 = t * t;
            double t3 = t2 * t;

            double x = 0.5 * ((2 * start.X) +
                       (-p0.X + end.X) * t +
                       (2 * p0.X - 5 * start.X + 4 * end.X - p3.X) * t2 +
                       (-p0.X + 3 * start.X - 3 * end.X + p3.X) * t3);

            double y = 0.5 * ((2 * start.Y) +
                       (-p0.Y + end.Y) * t +
                       (2 * p0.Y - 5 * start.Y + 4 * end.Y - p3.Y) * t2 +
                       (-p0.Y + 3 * start.Y - 3 * end.Y + p3.Y) * t3);

            return new Point((int)x, (int)y);
        }

        internal static Point SmoothStep(Point start, Point end, double t)
        {
            double eased = t * t * (3 - 2 * t);
            double x = start.X + (end.X - start.X) * eased;
            double y = start.Y + (end.Y - start.Y) * eased;
            return new Point((int)x, (int)y);
        }

        internal static Point Spiral(Point start, Point end, double t, double radius = 8.0)
        {
            double baseX = start.X + (end.X - start.X) * t;
            double baseY = start.Y + (end.Y - start.Y) * t;

            double decay = 1 - t;
            double angle = t * Math.PI * 4;
            double offsetX = Math.Cos(angle) * radius * decay;
            double offsetY = Math.Sin(angle) * radius * decay;

            return new Point((int)(baseX + offsetX), (int)(baseY + offsetY));
        }

        internal static Point PerlinNoise(Point start, Point end, double t, double amplitude = 10.0, double frequency = 0.1)
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

            return new Point((int)finalX, (int)finalY);
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