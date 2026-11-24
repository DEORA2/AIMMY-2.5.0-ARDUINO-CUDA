namespace InputLogic
{
    /// <summary>
    /// The movement paths. Each one describes the route the crosshair takes from where it is when a
    /// target is picked up (progress p = 0) to the target (p = 1). Routes are given in two parts relative
    /// to the starting distance: "along" (towards the target, 1 = arrived) and "lateral" (sideways,
    /// as a fraction of the distance). Every route starts at 0,0 and ends at 1,0, so it always lands
    /// on the target. "Classic" is the old behaviour and has no route.
    /// </summary>
    internal static class PathShapes
    {
        public const string Classic = "Classic";

        public static readonly string[] Names =
        {
            "Classic", "Linear", "Cubic Bezier", "Catmull-Rom", "Sine Wave", "Zigzag", "Perlin Noise",
            "Spiral", "Ease In-Out", "Smooth Step", "Exponential", "Overshoot", "Elastic", "Bounce",
            "Reaction Delay", "Horizontal First", "Adaptive", "Human"
        };

        public static readonly Dictionary<string, string> Descriptions = new()
        {
            ["Classic"] = "The old behaviour: each frame covers the same share of the remaining distance. Fast start, slow finish, straight line.",
            ["Linear"] = "Straight line at a constant speed.",
            ["Cubic Bezier"] = "One smooth arc that bows out to the side and curves in.",
            ["Catmull-Rom"] = "A smooth S-curve that bends one way, then the other.",
            ["Sine Wave"] = "Waves side to side twice on the way in.",
            ["Zigzag"] = "Sharp side-to-side zigzags on the way in.",
            ["Perlin Noise"] = "A natural, slightly wandering line, like an unsteady hand.",
            ["Spiral"] = "Swings past and spirals in around the target.",
            ["Ease In-Out"] = "Straight line that starts gently, speeds up, then slows into the target.",
            ["Smooth Step"] = "Moves in three smooth steps with small pauses: a big jump, a correction, then a final nudge.",
            ["Exponential"] = "Straight line that starts very slowly and accelerates into the target.",
            ["Overshoot"] = "Flies a little past the target, then pulls back onto it.",
            ["Elastic"] = "Springs past the target and wobbles back and forth before settling.",
            ["Bounce"] = "Hits the target, bounces back off it a few times, then settles.",
            ["Reaction Delay"] = "Waits a moment like a human reacting, then moves quickly.",
            ["Horizontal First"] = "Moves sideways first, then up or down (an L-shape).",
            ["Adaptive"] = "Quick and straight for short moves, slower with a gentle curve for long ones.",
            ["Human"] = "A fast, slightly curved flick that lands just short, then a slow correction. The curve changes each time."
        };

        public static bool IsRoutePath(string path) => path != Classic && Descriptions.ContainsKey(path);

        /// <summary>How much longer than the base time this path takes (some need room to show their shape).</summary>
        public static double DurationScale(string path, double distance) => path switch
        {
            "Elastic" or "Bounce" => 1.8,
            "Spiral" => 1.6,
            "Smooth Step" => 1.5,
            "Horizontal First" => 1.4,
            "Reaction Delay" or "Sine Wave" or "Zigzag" or "Perlin Noise" => 1.3,
            "Catmull-Rom" or "Overshoot" or "Human" => 1.2,
            "Adaptive" => Math.Clamp(0.45 + distance / 350.0, 0.45, 1.6),
            _ => 1.0
        };

        /// <summary>Offset from the starting crosshair position at progress <paramref name="p"/>, in screen pixels.</summary>
        public static (double X, double Y) Offset(string path, double p, double startX, double startY, int side, int seed)
        {
            p = Math.Clamp(p, 0, 1);

            if (path == "Horizontal First")
            {
                // World axes rather than along/sideways: X first, then Y, overlapping a little
                return (startX * EaseInOutSine(Math.Clamp(p / 0.55, 0, 1)),
                        startY * EaseInOutSine(Math.Clamp((p - 0.45) / 0.55, 0, 1)));
            }

            var (along, lateral) = Route(path, p, side, seed, Math.Sqrt(startX * startX + startY * startY));

            // Along = direction of the target; lateral = 90 degrees to the left of it
            return (startX * along - startY * lateral,
                    startY * along + startX * lateral);
        }

        private static (double Along, double Lateral) Route(string path, double p, int side, int seed, double distance)
        {
            switch (path)
            {
                case "Cubic Bezier":
                    {
                        double u = 1 - p;
                        double along = 3 * u * u * p * 0.2 + 3 * u * p * p * 0.75 + p * p * p;
                        double lateral = 3 * u * u * p * 0.4 + 3 * u * p * p * 0.2;
                        return (along, side * lateral);
                    }
                case "Catmull-Rom":
                    return CatmullRomS(p, side);
                case "Sine Wave":
                    return (p, side * 0.09 * Math.Sin(4 * Math.PI * p));
                case "Zigzag":
                    return (p, side * 0.08 * ZigzagWave(p));
                case "Perlin Noise":
                    {
                        double envelope = Math.Sin(Math.PI * p);
                        return (p + 0.04 * Noise1D(p * 4 + 50, seed) * envelope,
                                0.12 * Noise1D(p * 4, seed) * envelope);
                    }
                case "Spiral":
                    {
                        // Curls in around the target: swings a little past it and loops back, tightening as it goes
                        double angle = side * 1.75 * Math.PI * p;
                        double radius = (1 - p) * (1 - p);
                        return (1 - radius * Math.Cos(angle), -0.45 * radius * Math.Sin(angle));
                    }
                case "Ease In-Out":
                    return (EaseInOutSine(p), 0);
                case "Smooth Step":
                    return (ThreeSteps(p), 0);
                case "Exponential":
                    return ((Math.Exp(4 * p) - 1) / (Math.Exp(4) - 1), 0);
                case "Overshoot":
                    {
                        const double c1 = 1.70158, c3 = c1 + 1;
                        double q = p - 1;
                        return (1 + c3 * q * q * q + c1 * q * q, 0);
                    }
                case "Elastic":
                    return (1 - Math.Pow(2, -8 * p) * Math.Cos(4 * Math.PI * p), 0);
                case "Bounce":
                    return (EaseOutBounce(p), 0);
                case "Reaction Delay":
                    return (p < 0.35 ? 0 : EaseOutCubic((p - 0.35) / 0.65), 0);
                case "Adaptive":
                    {
                        // Long moves get a gentle curve; short ones go straight
                        double curve = 0.12 * Math.Clamp((distance - 80) / 320, 0, 1);
                        return (EaseOutCubic(p), side * curve * Math.Sin(Math.PI * p));
                    }
                case "Human":
                    {
                        var random = new Random(seed);
                        double curve = 0.04 + random.NextDouble() * 0.07;
                        double reach = 0.9 + random.NextDouble() * 0.05;
                        double along = p < 0.6
                            ? reach * EaseOutQuad(p / 0.6)
                            : reach + (1 - reach) * SmoothStep((p - 0.6) / 0.4);
                        double lateral = side * curve * Math.Sin(Math.PI * Math.Min(p / 0.6, 1))
                                         + 0.015 * Noise1D(p * 5, seed) * Math.Sin(Math.PI * p);
                        return (along, lateral);
                    }
                case "Linear":
                default:
                    return (p, 0);
            }
        }

        #region Easing helpers

        private static double EaseInOutSine(double p) => -(Math.Cos(Math.PI * p) - 1) / 2;
        private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);
        private static double EaseOutQuad(double p) => 1 - (1 - p) * (1 - p);
        private static double SmoothStep(double p) => p * p * (3 - 2 * p);

        private static double ThreeSteps(double p)
        {
            // Each step eases in and out, so the crosshair briefly stops between steps
            if (p < 0.4) return 0.72 * SmoothStep(p / 0.4);
            if (p < 0.7) return 0.72 + 0.22 * SmoothStep((p - 0.4) / 0.3);
            return 0.94 + 0.06 * SmoothStep((p - 0.7) / 0.3);
        }

        private static double EaseOutBounce(double p)
        {
            const double n1 = 7.5625, d1 = 2.75;
            if (p < 1 / d1) return n1 * p * p;
            if (p < 2 / d1) { p -= 1.5 / d1; return n1 * p * p + 0.75; }
            if (p < 2.5 / d1) { p -= 2.25 / d1; return n1 * p * p + 0.9375; }
            p -= 2.625 / d1;
            return n1 * p * p + 0.984375;
        }

        private static double ZigzagWave(double p)
        {
            // Corners at 0.2, 0.4, 0.6 and 0.8: up, down, up, down, back to the line
            double[] corners = { 0, 1, -1, 1, -1, 0 };
            double position = p * 5;
            int index = Math.Min((int)position, 4);
            double frac = position - index;
            return corners[index] + (corners[index + 1] - corners[index]) * frac;
        }

        private static (double Along, double Lateral) CatmullRomS(double p, int side)
        {
            // Spline through start, a point bowed one way, a point bowed the other way, and the target
            (double A, double L)[] points =
            {
                (-0.3, -0.18 * side), (0, 0), (0.3, 0.18 * side), (0.7, -0.15 * side), (1, 0), (1.3, 0.15 * side)
            };
            double position = p * 3;
            int segment = Math.Min((int)position, 2);
            double t = position - segment;
            var (p0, p1, p2, p3) = (points[segment], points[segment + 1], points[segment + 2], points[segment + 3]);

            double Spline(double a, double b, double c, double d) =>
                0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (-a + 3 * b - 3 * c + d) * t * t * t);

            return (Spline(p0.A, p1.A, p2.A, p3.A), Spline(p0.L, p1.L, p2.L, p3.L));
        }

        /// <summary>Smooth 1D gradient noise in roughly -1..1.</summary>
        private static double Noise1D(double x, int seed)
        {
            int i = (int)Math.Floor(x);
            double f = x - i;
            double g0 = Gradient(i, seed), g1 = Gradient(i + 1, seed);
            double u = f * f * f * (f * (f * 6 - 15) + 10);
            return 2 * ((1 - u) * g0 * f + u * g1 * (f - 1));
        }

        private static double Gradient(int i, int seed)
        {
            unchecked
            {
                uint h = (uint)(i * 374761393 + seed * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFF) / 32767.5 - 1.0;
            }
        }

        #endregion
    }

    /// <summary>
    /// Turns a path into per-frame mouse moves. When a target is picked up (or the target changes),
    /// an aim point travels along the path's route over a time set by the sensitivity, and the crosshair is
    /// pulled towards it with the same step as Classic. The aim point is always placed relative to where the
    /// target really is, so a moving target is followed. Once the route finishes, it tracks exactly like the
    /// old behaviour (a fixed share of the remaining distance per frame).
    /// </summary>
    internal sealed class AimPathController
    {
        // Shorter moves than this just track; a route would be invisible anyway
        private const double MinRouteDistance = 12;
        // The aim point may run at most this share of the route ahead of schedule
        private const double LeadCap = 0.3;
        // A new route only starts on a jump if the aim had been steady for this long beforehand.
        // In a game, moves show up a few frames late, so a fast swing can overshoot and make the error
        // flip sides. That looks like a target jump, but it is our own swing: starting a fresh route
        // from it would swing again from further out, and the error grows each time (the aim "spins
        // away"). A real target switch happens from steady tracking; a self-made swing never does.
        private const double SettleSeconds = 0.12;
        // A route is abandoned (plain tracking takes over) if the error grows this far past where it started
        private const double DivergeRatio = 1.5, DivergeSlack = 20;

        private readonly Random _random;
        private bool _routing;
        private bool _hasLast;
        private string _path = "";
        private double _progress, _duration;
        private double _startX, _startY;   // error when the route started
        private int _side = 1, _seed;
        private double _lastErrorX, _lastErrorY;
        private double _steadySeconds;     // how long tracking has been calm (no big frame-to-frame changes)

        // Sensitivity is defined at this AI frame rate; other rates get the same speed per second.
        // Without this, a fast PC (200-300 AI FPS) sends far more moves before the first one shows
        // up on screen (games + capture add ~40-100 ms), overshoots, and swings further each time.
        private const double ReferenceFps = 144;

        // Overshoot brake: when the crosshair shoots well past the target while tracking, the aim
        // speed is cut; it creeps back while the aim holds on target, up to a ceiling that each
        // overshoot lowers (so a setup that is too fast settles at a speed that works).
        private const double MinSpeedScale = 0.2;
        private const double OvershootCut = 0.6;
        private const double RecoverPerSecond = 0.25;      // speed regained per second on target
        private const double CeilingRecoverPerSecond = 0.02;
        private double _speedScale = 1, _speedCeiling = 1;
        private bool _hasRef;
        private double _refX, _refY;       // largest recent error on the current side of the target
        private double _nearSeconds;

        /// <summary>Current overshoot-brake factor (1 = full speed).</summary>
        public double SpeedScale => _speedScale;

        public AimPathController(int seed)
        {
            _random = new Random(seed);
        }

        public void Reset()
        {
            _routing = false;
            _hasLast = false;
            _steadySeconds = 0;
            _hasRef = false;
            // The brake is kept: it describes this game/PC setup, not one engagement.
        }

        /// <summary>
        /// Share of the remaining distance to move this frame. Equal to 1 - sensitivity at 144 FPS,
        /// the same speed per second at other frame rates, times the overshoot brake.
        /// </summary>
        private double FrameGain(double sensitivity, double dt)
        {
            double s = Math.Clamp(sensitivity, 0, 0.999);
            double frames = Math.Clamp(dt, 0.001, 0.05) * ReferenceFps;
            return (1 - Math.Pow(s, frames)) * _speedScale;
        }

        private void UpdateOvershootBrake(double ex, double ey, double distance, double dt)
        {
            double step = Math.Clamp(dt, 0.001, 0.05);
            if (!_hasRef)
            {
                if (distance > 25) { _refX = ex; _refY = ey; _hasRef = true; }
            }
            else
            {
                double refDistance = Hypot(_refX, _refY);
                bool crossed = ex * _refX + ey * _refY < 0;
                if (crossed && distance > Math.Max(15, 0.35 * refDistance))
                {
                    // Went well past the target: too fast for this game's delay
                    _speedCeiling = Math.Max(MinSpeedScale, Math.Min(_speedCeiling, _speedScale * 0.85));
                    _speedScale = Math.Max(MinSpeedScale, _speedScale * OvershootCut);
                    _refX = ex; _refY = ey;
                }
                else if (!crossed && distance > refDistance)
                {
                    _refX = ex; _refY = ey;
                }
            }

            // Arrived = stayed near the target for a moment (a fast swing passes through the middle too)
            _nearSeconds = distance < 8 ? _nearSeconds + step : 0;
            if (_nearSeconds > 0.06) _hasRef = false;

            if (distance < 15)
            {
                _speedScale = Math.Min(_speedCeiling, _speedScale + RecoverPerSecond * step);
                _speedCeiling = Math.Min(1, _speedCeiling + CeilingRecoverPerSecond * step);
            }
        }

        /// <summary>The move for this frame (screen pixels, before smoothing/clamping) towards the target at error ex, ey.</summary>
        public (double X, double Y) NextMove(double ex, double ey, double dt, double sensitivity, string path)
        {
            double distance = Math.Sqrt(ex * ex + ey * ey);
            double moveX, moveY;

            // Paths that pass the target on purpose (Overshoot, Elastic, Spiral...) are only judged once
            // the route has finished and normal tracking has taken over.
            if (!_routing)
                UpdateOvershootBrake(ex, ey, distance, dt);
            double trackGain = FrameGain(sensitivity, dt);

            if (!PathShapes.IsRoutePath(path))
            {
                _routing = false;
                moveX = ex * trackGain;
                moveY = ey * trackGain;
                Remember(ex, ey);
                return (moveX, moveY);
            }

            double step = Math.Clamp(dt, 0.001, 0.05);
            double change = _hasLast ? Hypot(ex - _lastErrorX, ey - _lastErrorY) : 0;
            double lastDistance = Hypot(_lastErrorX, _lastErrorY);

            // The target jumped (switched target, or it reappeared somewhere else)
            bool jumped = _hasLast && change > Math.Max(80, 0.75 * lastDistance);
            bool wasSteady = _steadySeconds >= SettleSeconds;

            if (!_routing)
                _steadySeconds = _hasLast && change < Math.Max(30, 0.3 * lastDistance) ? _steadySeconds + step : 0;

            if (_routing)
            {
                // Mid-route jump, path changed, or the error is growing instead of shrinking: stop the
                // route and just track. Never start another route from here (see SettleSeconds).
                if (jumped || path != _path || distance > DivergeRatio * Hypot(_startX, _startY) + DivergeSlack)
                {
                    _routing = false;
                    _steadySeconds = 0;
                }
            }
            else if (distance >= MinRouteDistance && (!_hasLast || (jumped && wasSteady)))
            {
                StartRoute(ex, ey, distance, sensitivity, path);
                _steadySeconds = 0;
            }
            _path = _routing ? _path : path;

            if (_routing && trackGain > 0)
            {
                // The route is a moving aim point, and the crosshair is pulled towards it with exactly the
                // same proportional step as Classic. Mouse counts are never assumed to equal screen pixels
                // (they don't: the game's sensitivity and the capture scaling both change it). The aim point
                // runs slightly ahead of schedule to make up for the lag of the proportional step.
                double routeGain = trackGain;
                _progress = Math.Min(1, _progress + step / _duration);
                double lagSeconds = Math.Min((1 - routeGain) / routeGain * step, LeadCap * _duration);
                double aimProgress = Math.Min(1, _progress + lagSeconds / _duration);

                var (offsetX, offsetY) = PathShapes.Offset(path, aimProgress, _startX, _startY, _side, _seed);
                double aimErrorX = _startX - offsetX, aimErrorY = _startY - offsetY; // where the target should be, relative to the crosshair
                moveX = (ex - aimErrorX) * routeGain;
                moveY = (ey - aimErrorY) * routeGain;

                if (aimProgress >= 1) _routing = false; // aim point is on the target: from here it is Classic
            }
            else
            {
                moveX = ex * trackGain;
                moveY = ey * trackGain;
            }

            Remember(ex, ey);
            return (moveX, moveY);
        }

        private void StartRoute(double ex, double ey, double distance, double sensitivity, string path)
        {
            _routing = true;
            _path = path;
            _progress = 0;
            _startX = ex;
            _startY = ey;
            _seed = _random.Next();
            _side = path == "Human" ? (_random.Next(2) == 0 ? -1 : 1) : 1;
            // A braked (slowed) aim gets a proportionally longer route so it can still follow the shape
            _duration = BaseDuration(sensitivity) * PathShapes.DurationScale(path, distance) / _speedScale;
        }

        /// <summary>
        /// Time for a route. Matches how long the old behaviour took to cover 95% of the distance at
        /// 144 FPS, so a sensitivity keeps roughly the same speed as before.
        /// </summary>
        public static double BaseDuration(double sensitivity)
        {
            double s = Math.Clamp(sensitivity, 0.05, 0.995);
            double frames = Math.Log(0.05) / Math.Log(s);
            return Math.Clamp(frames / 144.0, 0.035, 1.2);
        }

        private void Remember(double ex, double ey)
        {
            _hasLast = true;
            _lastErrorX = ex;
            _lastErrorY = ey;
        }

        private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
    }
}
