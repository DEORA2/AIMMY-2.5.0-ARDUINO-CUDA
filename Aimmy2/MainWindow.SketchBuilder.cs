using Whales2.Class;
using Whales2.MouseMovementLibraries.SketchBuilder;
using Whales2.UILibrary;
using Class;
using Microsoft.Win32;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UILibrary;
using InputLogic;
using Visuality;

namespace Whales2
{
    /// <summary>
    /// Sketch builder page (opened from the Device page): records the user's mouse through the USB Host Shield one
    /// action at a time, works out its report layout, and generates a pass-through sketch controlled over the COM
    /// port or Raw HID, which it can compile and upload with the installed Arduino IDE.
    /// </summary>
    public partial class MainWindow
    {
        private const string SketchPage = "Sketch builder";
        private const string ControlCom = "COM port";
        private const string ControlRawHid = "Raw HID";
        private static readonly TimeSpan RecordDuration = TimeSpan.FromSeconds(2.5);

        private ADropdown? _sketchPortDropdown;
        private ADropdown? _sketchControlDropdown;
        private TextBlock? _loggerStatus, _readStatus, _latestReport, _layoutText, _sketchStatus;
        private Button? _readButton, _useButton;
        private TextBox? _sketchPreview;

        private ReportCapture? _capture;
        private readonly object _captureLock = new();
        private readonly Dictionary<CaptureStep, List<byte[]>> _captures = new();
        private CaptureStep? _recordingStep;
        private List<byte[]>? _recordingBuffer;
        private readonly Dictionary<CaptureStep, TextBlock> _stepStatus = new();
        private readonly List<Button> _recordButtons = new();
        private MouseReportLayout? _layout;
        private int _reportCount;

        private static readonly (CaptureStep Step, string Instruction)[] RecordSteps =
        {
            (CaptureStep.Neutral, "Click the left button once and let go, then keep the mouse still"),
            (CaptureStep.LeftClick, "Press and hold the left button"),
            (CaptureStep.RightClick, "Press and hold the right button"),
            (CaptureStep.WheelUp, "Scroll the wheel up a few notches"),
            (CaptureStep.WheelDown, "Scroll the wheel down a few notches"),
            (CaptureStep.MoveRight, "Move the mouse to the right, steadily"),
            (CaptureStep.MoveLeft, "Move the mouse to the left, steadily"),
            (CaptureStep.MoveUp, "Move the mouse up (away from you), steadily"),
            (CaptureStep.MoveDown, "Move the mouse down (towards you), steadily")
        };

        private FrameworkElement BuildSketchBuilderPage()
        {
            var root = new StackPanel { Margin = PageMargin, MaxWidth = 860 };

            // Intro
            var back = MakeButton("Back to Device", () => ShowPage("Device"));
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(4, 0, 0, 12);
            root.Children.Add(back);

            bool ide = ArduinoUploader.IsAvailable;
            var intro = new CardBuilder(this, "Make a sketch for your board",
                "For an Arduino Leonardo with a USB Host Shield: your real mouse plugs into the shield and passes through, and Whales can move it. " +
                "Record your mouse once, then generate a sketch controlled over the COM port or Raw HID.");
            intro.AddElement(Paragraph(ide
                ? "Arduino IDE found: Whales can compile and upload the sketches for you. It needs the HID-Project and USB Host Shield Library 2.0 libraries installed."
                : "No Arduino IDE found: you can still save each sketch and upload it with the Arduino IDE yourself (board: Arduino Leonardo). It needs the HID-Project and USB Host Shield Library 2.0 libraries."));
            intro.AddDropdown("Sketch Board Port", "Board's COM port", "The Leonardo's port, for uploading and reading the mouse",
                Array.Empty<string>(), d => _sketchPortDropdown = d);
            intro.AddAction("Refresh ports", "Update the list after plugging the board in", RefreshSketchPorts);
            root.Children.Add(intro.Root);

            // 1. Report logger
            var logger = new CardBuilder(this, "1. Upload the report logger",
                "A small sketch that prints what your mouse sends. Plug your mouse into the USB Host Shield first.");
            var loggerButtons = ButtonRow(
                MakeButton("Upload report logger", () => _ = UploadSketchAsync("whales_report_logger", SketchTemplates.ReportLogger, _loggerStatus!, afterUpload: null), primary: true),
                MakeButton("Save .ino…", () => SaveSketchAs("whales_report_logger", SketchTemplates.ReportLogger)));
            logger.AddElement(loggerButtons);
            _loggerStatus = Paragraph("");
            logger.AddElement(_loggerStatus);
            root.Children.Add(logger.Root);

            // 2. Read the mouse
            var read = new CardBuilder(this, "2. Read your mouse", "Whales listens to the report logger on the board's COM port");
            _readButton = MakeButton("Start reading", ToggleReading, primary: true);
            read.AddElement(ButtonRow(_readButton));
            _readStatus = Paragraph("Not reading.");
            read.AddElement(_readStatus);
            _latestReport = Paragraph("");
            _latestReport.FontFamily = new FontFamily("Consolas");
            read.AddElement(_latestReport);
            root.Children.Add(read.Root);

            // 3. Record each action
            var record = new CardBuilder(this, "3. Record each action",
                $"Press Record, then do the action until it finishes ({RecordDuration.TotalSeconds:0.#} s). Any order; record again to replace one.");
            foreach (var (step, instruction) in RecordSteps)
                record.AddElement(MakeRecordRow(step, instruction));
            root.Children.Add(record.Root);

            // 4. Result
            var result = new CardBuilder(this, "4. Your mouse's layout", "Worked out from the recordings");
            _layoutText = Paragraph("Record every action above to see the result.");
            result.AddElement(_layoutText);
            root.Children.Add(result.Root);

            // 5. Sketch
            var make = new CardBuilder(this, "5. Make your sketch", "Upload it to the board, then use it in Whales")
                .AddDropdown("Sketch Control", "Control over", "COM port: the \"Arduino\" device type. Raw HID: \"Arduino (Raw HID)\", no COM port needed",
                    new[] { ControlCom, ControlRawHid }, d =>
                    {
                        _sketchControlDropdown = d;
                        d.DropdownBox.SelectionChanged += (_, _) => UpdateSketchPreview();
                    });
            _useButton = MakeButton("Use it in Whales", () =>
            {
                if (_layout == null) { ShowSketchStatus("Record every action first (step 3), then upload the sketch."); return; }
                UseSketchInWhales();
            });
            make.AddElement(ButtonRow(
                MakeButton("Upload to board", () =>
                {
                    if (_layout == null) { ShowSketchStatus("Record every action first (step 3)."); return; }
                    _ = UploadSketchAsync(CurrentSketchName, CurrentSketch()!, _sketchStatus!,
                        afterUpload: () => ShowSketchStatus(_sketchStatus!.Text + "\nNow press \"Use it in Whales\"."));
                }, primary: true),
                MakeButton("Save .ino…", () =>
                {
                    if (_layout == null) { ShowSketchStatus("Record every action first (step 3)."); return; }
                    SaveSketchAs(CurrentSketchName, CurrentSketch()!);
                }),
                MakeButton("Copy", () =>
                {
                    if (_layout == null) { ShowSketchStatus("Record every action first (step 3)."); return; }
                    Clipboard.SetText(CurrentSketch()!);
                    ShowSketchStatus("Copied to the clipboard.");
                }),
                _useButton));
            _sketchStatus = Paragraph("");
            make.AddElement(_sketchStatus);
            _sketchPreview = new TextBox
            {
                IsReadOnly = true,
                VerticalContentAlignment = VerticalAlignment.Top,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Height = 280,
                Margin = new Thickness(14, 6, 14, 10),
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Res<Brush>("UI.KeyBg"),
                Foreground = Res<Brush>("UI.TextSoft"),
                BorderBrush = Res<Brush>("UI.Box"),
                Text = "// The sketch appears here once your mouse is recorded."
            };
            make.AddElement(_sketchPreview);
            root.Children.Add(make.Root);

            RefreshSketchPorts();
            return MakeScrollPage(root);
        }

        #region Sketch builder helpers

        private bool SketchUsesRawHid => SelectedText(_sketchControlDropdown) == ControlRawHid;
        private string CurrentSketchName => SketchUsesRawHid ? "whales_mouse_rawhid" : "whales_mouse_com";
        private string? CurrentSketch() =>
            _layout == null ? null : SketchUsesRawHid ? SketchTemplates.RawHid(_layout) : SketchTemplates.Com(_layout);
        private string? SketchPort => SelectedText(_sketchPortDropdown);

        private TextBlock Paragraph(string text)
        {
            var t = MakeSmall(text, 12.5);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(14, 4, 14, 8);
            return t;
        }

        private static StackPanel ButtonRow(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 14, 6) };
            foreach (var b in buttons)
            {
                b.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(b);
            }
            return row;
        }

        private void ShowSketchStatus(string text)
        {
            if (_sketchStatus != null) _sketchStatus.Text = text;
        }

        private void RefreshSketchPorts()
        {
            if (_sketchPortDropdown == null) return;
            string? saved = Dictionary.dropdownState["Sketch Board Port"]?.ToString();
            var box = _sketchPortDropdown.DropdownBox;
            box.Items.Clear();
            foreach (var port in SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p))
                AddDropdownItem(_sketchPortDropdown, port);
            if (!SelectDropdownValue(_sketchPortDropdown, saved) && box.Items.Count > 0)
                box.SelectedIndex = 0;
        }

        private FrameworkElement MakeRecordRow(CaptureStep step, string instruction)
        {
            var grid = new Grid { Margin = new Thickness(14, 4, 14, 4), MinHeight = 42 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = MouseReportAnalyzer.StepTitle(step),
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = Res<Brush>("UI.Text")
            });
            text.Children.Add(MakeSmall(instruction));
            grid.Children.Add(text);

            var status = MakeSmall("Not recorded");
            status.VerticalAlignment = VerticalAlignment.Center;
            status.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);
            _stepStatus[step] = status;

            var button = MakeButton("Record", () => _ = RecordStepAsync(step));
            button.Height = 30;
            button.MinWidth = 96;
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
            _recordButtons.Add(button);
            return grid;
        }

        private void SaveSketchAs(string name, string code)
        {
            var dialog = new SaveFileDialog
            {
                FileName = name + ".ino",
                Filter = "Arduino sketch (*.ino)|*.ino",
                Title = "Save sketch"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, code);
                new NoticeBar($"Saved {Path.GetFileName(dialog.FileName)}. The Arduino IDE needs it in a folder with the same name.", 5000).Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't save the sketch: {ex.Message}", "Sketch builder");
            }
        }

        /// <summary>Compiles and uploads a sketch to the board's port with the Arduino IDE (after asking).</summary>
        private async Task UploadSketchAsync(string name, string code, TextBlock status, Action? afterUpload)
        {
            string? port = SketchPort;
            if (string.IsNullOrEmpty(port)) { status.Text = "Pick the board's COM port at the top first."; return; }
            if (!ArduinoUploader.IsAvailable)
            {
                status.Text = "No Arduino IDE found. Use \"Save .ino…\" and upload it with the Arduino IDE.";
                return;
            }
            if (MessageBox.Show($"Upload {name} to the board on {port}?\n\nThis replaces the sketch currently on that board.",
                    "Sketch builder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // The port must be free: stop reading the mouse, and disconnect Whales' own COM connection to it
            StopReading();
            if (connectedPort == port) DisconnectPort();
            if (UsesArduinoHid) { MouseManager.ArduinoHid?.Dispose(); MouseManager.ArduinoHid = null; UpdateHidStatus(); }

            status.Text = $"Compiling and uploading to {port}… (this takes up to a minute)";
            string ino = ArduinoUploader.Save(name, code);
            var outcome = await ArduinoUploader.RunAsync(ino, port);
            if (outcome.Success)
            {
                status.Text = $"Uploaded to {port}. Saved a copy at {ino}";
                afterUpload?.Invoke();
            }
            else
            {
                var lines = outcome.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                var errors = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
                status.Text = "Upload failed: " + string.Join(" | ", errors.Count > 0 ? errors : lines.TakeLast(3));
            }
            RefreshSketchPorts(); // the Leonardo's port can change while it resets
        }

        private void ToggleReading()
        {
            if (_capture != null) { StopReading(); return; }

            string? port = SketchPort;
            if (string.IsNullOrEmpty(port)) { _readStatus!.Text = "Pick the board's COM port at the top first."; return; }
            if (connectedPort == port) DisconnectPort();

            try
            {
                var capture = new ReportCapture(port);
                capture.ReportReceived += OnSketchReport;
                capture.StatusLine += line => Dispatcher.BeginInvoke(() => _readStatus!.Text = $"Board says: {line}");
                capture.Open();
                _capture = capture;
                _reportCount = 0;
                _readStatus!.Text = $"Reading {port}… move or click your mouse. If nothing shows, upload the report logger (step 1).";
                _readButton!.Content = "Stop reading";
            }
            catch (Exception ex)
            {
                _readStatus!.Text = $"Couldn't open {port}: {ex.Message}";
            }
        }

        private void StopReading()
        {
            var capture = _capture;
            _capture = null;
            capture?.Dispose();
            if (_readButton != null) _readButton.Content = "Start reading";
            if (_readStatus != null && capture != null) _readStatus.Text = "Stopped reading.";
        }

        private void OnSketchReport(byte[] report)
        {
            lock (_captureLock)
            {
                _reportCount++;
                _recordingBuffer?.Add(report);
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (_latestReport != null)
                    _latestReport.Text = $"Latest report ({_reportCount}): " + string.Join(" ", report.Select(b => b.ToString("X2")));
            });
        }

        private async Task RecordStepAsync(CaptureStep step)
        {
            if (_capture == null) { _stepStatus[step].Text = "Start reading first (step 2)"; return; }
            if (_recordingStep != null) return;

            var buffer = new List<byte[]>();
            lock (_captureLock) { _recordingStep = step; _recordingBuffer = buffer; }
            foreach (var b in _recordButtons) b.IsEnabled = false;

            var status = _stepStatus[step];
            var end = DateTime.UtcNow + RecordDuration;
            while (DateTime.UtcNow < end)
            {
                status.Text = $"Recording… {Math.Ceiling((end - DateTime.UtcNow).TotalSeconds):0}";
                await Task.Delay(200);
            }

            List<byte[]> recorded;
            lock (_captureLock) { recorded = buffer.ToList(); _recordingStep = null; _recordingBuffer = null; }
            foreach (var b in _recordButtons) b.IsEnabled = true;

            if (recorded.Count == 0)
            {
                status.Text = "Nothing received, try again";
                return;
            }
            _captures[step] = recorded;
            status.Text = $"✓ {recorded.Count} report{(recorded.Count == 1 ? "" : "s")}";
            AnalyzeRecordings();
        }

        private void AnalyzeRecordings()
        {
            if (_layoutText == null) return;
            if (RecordSteps.Any(s => !_captures.ContainsKey(s.Step)))
            {
                int left = RecordSteps.Count(s => !_captures.ContainsKey(s.Step));
                _layoutText.Text = $"{left} action{(left == 1 ? "" : "s")} still to record.";
                _layout = null;
                UpdateSketchPreview();
                return;
            }

            var result = MouseReportAnalyzer.Analyze(_captures);
            _layout = result.Problems.Count == 0 ? result.Layout : null;
            var lines = new List<string>();
            if (result.Layout != null) lines.Add(result.Layout.Describe() + ".");
            lines.AddRange(result.Problems.Select(p => "Problem: " + p));
            lines.AddRange(result.Notes);
            if (_layout != null) lines.Add("Looks good. Make your sketch below.");
            _layoutText.Text = string.Join("\n", lines);
            UpdateSketchPreview();
        }

        private void UpdateSketchPreview()
        {
            if (_sketchPreview == null) return;
            _sketchPreview.Text = CurrentSketch()?.Replace("\n", Environment.NewLine)
                                  ?? "// The sketch appears here once your mouse is recorded.";
        }

        /// <summary>Switches Whales' device type to match the sketch and connects to the board.</summary>
        private void UseSketchInWhales()
        {
            string method = SketchUsesRawHid ? "Arduino (Raw HID)" : "Arduino";
            if (uiManager.D_MouseMovementMethod != null)
                SelectDropdownValue(uiManager.D_MouseMovementMethod, method);

            if (SketchUsesRawHid)
                ConnectArduinoHidDevice(showMessageIfMissing: true); // the board may still be restarting; Reconnect retries
            else if (SketchPort is string port)
                ConnectPort(port);
            UpdateHidStatus();
            UpdateStatus();

            ShowPage("Device");
        }

        private void DisposeSketchBuilder()
        {
            StopReading();
        }

        #endregion
    }
}
