using System;
using System.Collections.Generic;
using System.Linq;

namespace Whales2.MouseMovementLibraries.SketchBuilder
{
    /// <summary>How the X / Y movement is stored in the mouse's HID report.</summary>
    internal enum XyFormat
    {
        /// <summary>One signed byte each: X at XOffset, Y at XOffset + 1.</summary>
        Bits8,
        /// <summary>Signed 16-bit little-endian each.</summary>
        Bits16,
        /// <summary>Two signed 12-bit values packed into 3 bytes (X low 8 bits, X high 4 / Y low 4, Y high 8).</summary>
        Packed12
    }

    /// <summary>The actions recorded on the sketch builder page, one at a time.</summary>
    internal enum CaptureStep
    {
        Neutral, LeftClick, RightClick, WheelUp, WheelDown, MoveRight, MoveLeft, MoveUp, MoveDown
    }

    /// <summary>Where each field is in the mouse's HID report, as the generated sketch reads it.</summary>
    internal sealed record MouseReportLayout(
        int ReportLength,
        int ReportId,          // -1 = the report has no ID byte
        int ButtonsOffset,
        byte LeftMask,
        byte RightMask,
        byte MiddleMask,
        int XOffset,
        int YOffset,
        XyFormat Format,
        int WheelOffset)       // -1 = no wheel found
    {
        /// <summary>Shortest report the sketch accepts: everything it reads must be inside it.</summary>
        public int MinLength
        {
            get
            {
                int end = Math.Max(ButtonsOffset, WheelOffset) + 1;
                int xyEnd = Format switch
                {
                    XyFormat.Bits8 => XOffset + 2,
                    XyFormat.Bits16 => YOffset + 2,
                    _ => XOffset + 3
                };
                return Math.Max(end, xyEnd);
            }
        }

        public string Describe()
        {
            string xy = Format switch
            {
                XyFormat.Bits8 => $"X byte {XOffset}, Y byte {YOffset} (8-bit)",
                XyFormat.Bits16 => $"X bytes {XOffset}-{XOffset + 1}, Y bytes {YOffset}-{YOffset + 1} (16-bit)",
                _ => $"X and Y packed in bytes {XOffset}-{XOffset + 2} (12-bit)"
            };
            string id = ReportId >= 0 ? $"report ID {ReportId}, " : "";
            string wheel = WheelOffset >= 0 ? $"wheel byte {WheelOffset}" : "no wheel found";
            return $"{ReportLength}-byte reports, {id}buttons byte {ButtonsOffset} (left 0x{LeftMask:X2}, right 0x{RightMask:X2}), {xy}, {wheel}";
        }

        /// <summary>Decodes movement from one report with this layout (used to check the detection).</summary>
        public (int dx, int dy) ReadXy(byte[] r)
        {
            switch (Format)
            {
                case XyFormat.Bits8:
                    return ((sbyte)r[XOffset], (sbyte)r[YOffset]);
                case XyFormat.Bits16:
                    return ((short)(r[XOffset] | (r[XOffset + 1] << 8)), (short)(r[YOffset] | (r[YOffset + 1] << 8)));
                default:
                    int x = r[XOffset] | ((r[XOffset + 1] & 0x0F) << 8);
                    int y = (r[XOffset + 1] >> 4) | (r[XOffset + 2] << 4);
                    return (SignExtend12(x), SignExtend12(y));
            }
        }

        private static int SignExtend12(int v) => (v & 0x800) != 0 ? v - 0x1000 : v;
    }

    /// <summary>
    /// Works out a mouse's report layout from reports recorded while the user did one action at a time,
    /// by looking at which bytes change (and how) compared with the report of the mouse at rest.
    /// </summary>
    internal static class MouseReportAnalyzer
    {
        public sealed record Result(MouseReportLayout? Layout, List<string> Problems, List<string> Notes);

        public static Result Analyze(IReadOnlyDictionary<CaptureStep, List<byte[]>> captures)
        {
            var problems = new List<string>();
            var notes = new List<string>();

            if (!captures.TryGetValue(CaptureStep.Neutral, out var neutralReports) || neutralReports.Count == 0)
            {
                problems.Add("Record \"Mouse at rest\" first.");
                return new Result(null, problems, notes);
            }
            byte[] neutral = neutralReports[^1];
            int n = neutral.Length;

            // Only compare reports of the same kind (same length, same first byte if it looks like a report ID)
            List<byte[]> Get(CaptureStep step) =>
                captures.TryGetValue(step, out var list) ? list.Where(r => r.Length == n).ToList() : new List<byte[]>();

            foreach (CaptureStep step in Enum.GetValues(typeof(CaptureStep)))
            {
                if (step != CaptureStep.Neutral && Get(step).Count == 0)
                    problems.Add($"Nothing recorded for \"{StepTitle(step)}\".");
            }
            if (problems.Count > 0)
                return new Result(null, problems, notes);

            // Fraction of a step's reports where byte i differs from the rest report
            double Changed(CaptureStep step, int i)
            {
                var reports = Get(step);
                return reports.Count(r => r[i] != neutral[i]) / (double)reports.Count;
            }

            // A report ID is a first byte that is the same in every report and never changes with any action
            var all = captures.Values.SelectMany(v => v).Where(r => r.Length == n).ToList();
            int reportId = neutral[0] != 0 && all.All(r => r[0] == neutral[0]) ? neutral[0] : -1;
            int first = reportId >= 0 ? 1 : 0;

            // Buttons: the byte that changes most when a button is held
            int buttons = Enumerable.Range(first, n - first)
                .OrderByDescending(i => Changed(CaptureStep.LeftClick, i) + Changed(CaptureStep.RightClick, i))
                .ThenBy(i => i).First();
            byte leftMask = MostCommonDiff(Get(CaptureStep.LeftClick), neutral, buttons);
            byte rightMask = MostCommonDiff(Get(CaptureStep.RightClick), neutral, buttons);
            if (leftMask == 0 || rightMask == 0 || leftMask == rightMask)
                problems.Add("Couldn't tell the left and right buttons apart. Record them again, holding the button down.");
            byte middleMask = (byte)(leftMask == 0x01 && rightMask == 0x02 ? 0x04 : 0);
            if (middleMask == 0) notes.Add("Middle button not mapped (unusual button bits).");

            double Lr(int i) => (Changed(CaptureStep.MoveRight, i) + Changed(CaptureStep.MoveLeft, i)) / 2;
            double Ud(int i) => (Changed(CaptureStep.MoveUp, i) + Changed(CaptureStep.MoveDown, i)) / 2;

            var candidates = Enumerable.Range(first, n - first).Where(i => i != buttons).ToList();
            int x = candidates.Where(i => Lr(i) >= 0.4 && Lr(i) > Ud(i)).DefaultIfEmpty(-1).First();
            if (x < 0)
            {
                problems.Add("Couldn't find the X movement. Record \"Move right\" and \"Move left\" again, moving steadily.");
                return new Result(null, problems, notes);
            }

            // Y: first byte (after X) that changes mostly when moving up/down
            int y = candidates.Where(i => i > x && Ud(i) >= 0.4 && Ud(i) > Lr(i) * 0.8).DefaultIfEmpty(-1).First();
            if (y < 0)
            {
                problems.Add("Couldn't find the Y movement. Record \"Move up\" and \"Move down\" again, moving steadily.");
                return new Result(null, problems, notes);
            }

            XyFormat format;
            if (y == x + 1)
            {
                // Either 8-bit X/Y, or 12-bit packed where byte x+1 holds X's high nibble (low) and Y's low nibble (high)
                bool lowNibbleOnLr = NibbleChanges(Get(CaptureStep.MoveLeft), neutral, x + 1, low: true);
                bool highNibbleOnUd = NibbleChanges(Get(CaptureStep.MoveUp).Concat(Get(CaptureStep.MoveDown)).ToList(), neutral, x + 1, low: false);
                bool byte2OnUd = x + 2 < n && Ud(x + 2) >= 0.3;
                format = lowNibbleOnLr && highNibbleOnUd && byte2OnUd ? XyFormat.Packed12 : XyFormat.Bits8;
            }
            else if (y >= x + 2)
            {
                format = XyFormat.Bits16;
            }
            else
            {
                problems.Add("X and Y movement overlap; this mouse's report layout isn't supported.");
                return new Result(null, problems, notes);
            }
            if (format == XyFormat.Bits16 && y + 1 >= n)
            {
                problems.Add("The X/Y values run past the end of the report.");
                return new Result(null, problems, notes);
            }

            var used = new HashSet<int> { buttons, x, y };
            if (format == XyFormat.Bits16) { used.Add(x + 1); used.Add(y + 1); }
            if (format == XyFormat.Packed12) used.Add(x + 2);

            double Wheel(int i) => (Changed(CaptureStep.WheelUp, i) + Changed(CaptureStep.WheelDown, i)) / 2;
            int wheel = candidates.Where(i => !used.Contains(i) && Wheel(i) >= 0.3)
                .OrderByDescending(Wheel).ThenBy(i => i).DefaultIfEmpty(-1).First();
            if (wheel < 0) notes.Add("No scroll wheel byte found; scrolling won't pass through.");

            var layout = new MouseReportLayout(n, reportId, buttons, leftMask, rightMask, middleMask, x, y, format, wheel);

            // Check the directions decode the right way round
            CheckSign(layout, Get(CaptureStep.MoveRight), r => r.dx, positive: true, "Move right", problems);
            CheckSign(layout, Get(CaptureStep.MoveLeft), r => r.dx, positive: false, "Move left", problems);
            CheckSign(layout, Get(CaptureStep.MoveDown), r => r.dy, positive: true, "Move down", problems);
            CheckSign(layout, Get(CaptureStep.MoveUp), r => r.dy, positive: false, "Move up", problems);
            if (wheel >= 0)
            {
                CheckWheel(Get(CaptureStep.WheelUp), wheel, positive: true, "Wheel up", problems);
                CheckWheel(Get(CaptureStep.WheelDown), wheel, positive: false, "Wheel down", problems);
            }

            return new Result(layout, problems, notes);
        }

        public static string StepTitle(CaptureStep step) => step switch
        {
            CaptureStep.Neutral => "Mouse at rest",
            CaptureStep.LeftClick => "Left button",
            CaptureStep.RightClick => "Right button",
            CaptureStep.WheelUp => "Scroll up",
            CaptureStep.WheelDown => "Scroll down",
            CaptureStep.MoveRight => "Move right",
            CaptureStep.MoveLeft => "Move left",
            CaptureStep.MoveUp => "Move up",
            CaptureStep.MoveDown => "Move down",
            _ => step.ToString()
        };

        private static byte MostCommonDiff(List<byte[]> reports, byte[] neutral, int index) =>
            reports.Select(r => (byte)(r[index] ^ neutral[index])).Where(d => d != 0)
                .GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        private static bool NibbleChanges(List<byte[]> reports, byte[] neutral, int index, bool low)
        {
            int mask = low ? 0x0F : 0xF0;
            return reports.Count > 0 && reports.Count(r => ((r[index] ^ neutral[index]) & mask) != 0) >= reports.Count * 0.3;
        }

        private static void CheckSign(MouseReportLayout layout, List<byte[]> reports, Func<(int dx, int dy), int> axis,
            bool positive, string name, List<string> problems)
        {
            var values = reports.Select(r => axis(layout.ReadXy(r))).Where(v => v != 0).ToList();
            if (values.Count == 0) { problems.Add($"\"{name}\" didn't record any movement on that axis."); return; }
            int right = values.Count(v => positive ? v > 0 : v < 0);
            if (right < values.Count * 0.7)
                problems.Add($"\"{name}\" decodes the wrong way round in {values.Count - right} of {values.Count} reports. Record it again, moving only in that direction.");
        }

        private static void CheckWheel(List<byte[]> reports, int index, bool positive, string name, List<string> problems)
        {
            var values = reports.Select(r => (int)(sbyte)r[index]).Where(v => v != 0).ToList();
            if (values.Count == 0) return;
            if (values.Count(v => positive ? v > 0 : v < 0) < values.Count * 0.7)
                problems.Add($"\"{name}\" decodes the wrong way round. Record it again.");
        }
    }
}
