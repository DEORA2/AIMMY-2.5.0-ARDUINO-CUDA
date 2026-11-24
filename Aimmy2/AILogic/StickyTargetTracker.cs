using System.Diagnostics;
using System.Drawing;

namespace Whales2.AILogic
{
    /// <summary>
    /// Sticky aim: once a target is picked, keep following that same target until it is gone,
    /// even when another detection is closer to the crosshair.
    ///
    /// Each frame the locked target is looked for among all detections near where it is expected
    /// to be (last position + velocity), with a similar size. It switches to a different target only when
    ///  - the locked target has not been seen for longer than the grace period, or
    ///  - another target sits inside the switch radius of the crosshair (while the locked one does not)
    ///    for a short confirm time, i.e. the player deliberately moved onto someone else.
    /// While the locked target is briefly missing (flicker, partial cover) a stand-in at its estimated
    /// position is returned, marked IsPredicted, so the lock and overlay hold; the aim does not steer to it.
    ///
    /// Positions are compared in absolute screen pixels (Prediction.ScreenCenterX/Y), so it works when
    /// the capture box follows the mouse too. Returned boxes are in the current capture's coordinates.
    /// </summary>
    internal sealed class StickyTargetTracker
    {
        /// <summary>How long a locked target may be missing before it is dropped.</summary>
        public const double GraceSeconds = 0.15;

        /// <summary>How long another target must stay in the switch radius before switching to it.</summary>
        public const double SwitchConfirmSeconds = 0.08;

        private const float MaxSpeed = 4000f;        // px/s, caps velocity from bad matches
        private const float VelocitySmoothing = 0.35f;
        private const float MinSizeRatio = 0.4f;     // locked target may shrink/grow, but not by 2.5x in one frame
        private const double MaxPredictSeconds = 0.1;

        private Prediction? _locked;      // last real detection of the locked target (absolute screen coords)
        private long _lastSeen;           // timestamp of _locked
        private float _velX, _velY;       // px/s, absolute screen coords
        private bool _hasVelocity;
        private Prediction? _switchCandidate;
        private long _switchCandidateSince;

        public bool HasTarget => _locked != null;

        public void Reset()
        {
            _locked = null;
            _velX = _velY = 0f;
            _hasVelocity = false;
            _switchCandidate = null;
        }

        /// <param name="detections">All detections this frame (inside the FOV).</param>
        /// <param name="captureLeft">Screen X of the capture box's left edge.</param>
        /// <param name="captureTop">Screen Y of the capture box's top edge.</param>
        /// <param name="captureSize">Capture box size (the crosshair is its centre).</param>
        /// <param name="switchRadius">Another target must be this close to the crosshair to take over; 0 disables switching while the target is visible.</param>
        /// <param name="now">Stopwatch timestamp of this frame.</param>
        public Prediction? Update(IReadOnlyList<Prediction> detections, float captureLeft, float captureTop, int captureSize,
            float switchRadius, long now)
        {
            float crossX = captureLeft + captureSize / 2f;
            float crossY = captureTop + captureSize / 2f;

            // A target that has been gone too long (or since the aim key was last released) is forgotten.
            if (_locked != null && Seconds(now - _lastSeen) > GraceSeconds)
                Reset();

            Prediction? nearest = NearestTo(detections, crossX, crossY);

            if (_locked == null)
                return nearest == null ? null : Acquire(nearest, now);

            // Screen velocity is partly our own camera turning, which stops while the target is unseen (aim
            // pauses then), so only half of it is extrapolated and the gate below covers both cases.
            double dt = Math.Clamp(Seconds(now - _lastSeen), 0, MaxPredictSeconds);
            float predX = _locked.ScreenCenterX + _velX * (float)dt * 0.5f;
            float predY = _locked.ScreenCenterY + _velY * (float)dt * 0.5f;

            Prediction? match = FindMatch(detections, predX, predY, dt);

            if (match == null)
            {
                // Briefly missing: keep aiming where it should be.
                _switchCandidate = null;
                return Ghost(predX, predY, captureLeft, captureTop, captureSize, now);
            }

            UpdateVelocity(match, now);
            _locked = match;
            _lastSeen = now;

            if (nearest != null && !ReferenceEquals(nearest, match) && WantsSwitch(nearest, match, crossX, crossY, switchRadius))
            {
                if (_switchCandidate == null || !IsSameSpot(_switchCandidate, nearest))
                    _switchCandidateSince = now;
                _switchCandidate = nearest;

                if (Seconds(now - _switchCandidateSince) >= SwitchConfirmSeconds)
                    return Acquire(nearest, now);
            }
            else
            {
                _switchCandidate = null;
            }

            return match;
        }

        private static bool WantsSwitch(Prediction other, Prediction locked, float crossX, float crossY, float switchRadius)
        {
            if (switchRadius <= 0) return false;
            float r2 = switchRadius * switchRadius;
            return DistSq(other.ScreenCenterX, other.ScreenCenterY, crossX, crossY) < r2
                && DistSq(locked.ScreenCenterX, locked.ScreenCenterY, crossX, crossY) > r2;
        }

        /// <summary>The switch candidate is "the same" between frames if it stayed roughly in place.</summary>
        private static bool IsSameSpot(Prediction a, Prediction b)
        {
            float size = MathF.Max(8f, MathF.Sqrt(a.Rectangle.Width * a.Rectangle.Height));
            return DistSq(a.ScreenCenterX, a.ScreenCenterY, b.ScreenCenterX, b.ScreenCenterY) < size * size;
        }

        private Prediction? FindMatch(IReadOnlyList<Prediction> detections, float predX, float predY, double dt)
        {
            var last = _locked!;
            float lastArea = MathF.Max(1f, last.Rectangle.Width * last.Rectangle.Height);
            float lastSize = MathF.Sqrt(lastArea);

            // Gate: about two box-sizes, wider when the target was moving fast or we are extrapolating further.
            float speed = MathF.Sqrt(_velX * _velX + _velY * _velY);
            float gate = MathF.Max(lastSize * 2f, 30f) + speed * (float)dt * 0.75f;
            float gateSq = gate * gate;

            Prediction? best = null;
            float bestCost = float.MaxValue;
            foreach (var d in detections)
            {
                float distSq = DistSq(d.ScreenCenterX, d.ScreenCenterY, predX, predY);
                if (distSq > gateSq) continue;

                float area = MathF.Max(1f, d.Rectangle.Width * d.Rectangle.Height);
                float sizeRatio = MathF.Min(area, lastArea) / MathF.Max(area, lastArea);
                if (sizeRatio < MinSizeRatio) continue;

                float cost = MathF.Sqrt(distSq) / gate + (1f - sizeRatio) * 0.5f + (d.ClassId == last.ClassId ? 0f : 0.25f);
                if (cost < bestCost) { bestCost = cost; best = d; }
            }
            return best;
        }

        private void UpdateVelocity(Prediction match, long now)
        {
            double dt = Seconds(now - _lastSeen);
            if (dt < 0.001) return;

            float vx = (float)((match.ScreenCenterX - _locked!.ScreenCenterX) / dt);
            float vy = (float)((match.ScreenCenterY - _locked.ScreenCenterY) / dt);
            float speed = MathF.Sqrt(vx * vx + vy * vy);
            if (speed > MaxSpeed) { vx *= MaxSpeed / speed; vy *= MaxSpeed / speed; }

            if (!_hasVelocity)
            {
                _velX = vx; _velY = vy; _hasVelocity = true;
            }
            else
            {
                _velX += (vx - _velX) * VelocitySmoothing;
                _velY += (vy - _velY) * VelocitySmoothing;
            }
        }

        private Prediction Acquire(Prediction target, long now)
        {
            _locked = target;
            _lastSeen = now;
            _velX = _velY = 0f;
            _hasVelocity = false;
            _switchCandidate = null;
            return target;
        }

        /// <summary>A stand-in for the missing locked target at its predicted spot, in this frame's capture coordinates.</summary>
        private Prediction Ghost(float predX, float predY, float captureLeft, float captureTop, int captureSize, long now)
        {
            var last = _locked!;
            float w = last.Rectangle.Width, h = last.Rectangle.Height;
            float cx = predX - captureLeft, cy = predY - captureTop;
            float fade = (float)Math.Clamp(1.0 - Seconds(now - _lastSeen) / GraceSeconds, 0.0, 1.0);

            return new Prediction
            {
                Rectangle = new RectangleF(cx - w / 2f, cy - h / 2f, w, h),
                Confidence = last.Confidence * (0.5f + 0.5f * fade),
                ClassId = last.ClassId,
                ClassName = last.ClassName,
                CenterXTranslated = cx / captureSize,
                CenterYTranslated = cy / captureSize,
                ScreenCenterX = predX,
                ScreenCenterY = predY,
                IsPredicted = true
            };
        }

        private static Prediction? NearestTo(IReadOnlyList<Prediction> detections, float x, float y)
        {
            Prediction? best = null;
            float bestSq = float.MaxValue;
            foreach (var d in detections)
            {
                float sq = DistSq(d.ScreenCenterX, d.ScreenCenterY, x, y);
                if (sq < bestSq) { bestSq = sq; best = d; }
            }
            return best;
        }

        private static float DistSq(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2, dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    }
}
