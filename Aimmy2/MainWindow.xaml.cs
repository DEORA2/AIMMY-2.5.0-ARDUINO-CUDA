using Whales2.Class;
using Whales2.Controls;
using Whales2.MouseMovementLibraries.HidDeviceSupport;
using Whales2.Other;
using Whales2.Theme;
using Whales2.UILibrary;
using WhalesWPF.Class;
using Class;
using InputLogic;
using Other;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UILibrary;
using Visuality;

namespace Whales2
{
    public partial class MainWindow : Window
    {
        #region Managers and Windows

        // Core managers (lazy-loaded)
        private readonly Lazy<InputBindingManager> _bindingManager = new(() => new InputBindingManager());
        private static readonly Lazy<GithubManager> _githubManager = new(() => new GithubManager());
        private readonly Lazy<UI> _uiManager = new(() => new UI());
        private Lazy<FileManager>? _fileManager;

        // Windows
        private static readonly Lazy<FOV> _fovWindow = new(() =>
        {
            var window = new FOV();
            // Force immediate reposition to current display
            window.ForceReposition();
            return window;
        });

        private static readonly Lazy<DetectedPlayerWindow> _dpWindow = new(() =>
        {
            var window = new DetectedPlayerWindow();
            // Force immediate reposition to current display
            window.ForceReposition();
            return window;
        });

        // Public accessors
        internal InputBindingManager bindingManager => _bindingManager.Value;
        internal FileManager fileManager => _fileManager?.Value ?? throw new InvalidOperationException("FileManager not initialized");
        public static FOV FOVWindow => _fovWindow.Value;
        public static DetectedPlayerWindow DPWindow => _dpWindow.Value;
        public static GithubManager githubManager => _githubManager.Value;
        public UI uiManager => _uiManager.Value;

        #endregion

        #region UI State

        internal Dictionary<string, AToggle> toggleInstances = new();
        public double ActualFOV { get; set; } = 640;

        private string? connectedPort;
        private DispatcherTimer? comPortTimer;
        private DispatcherTimer? _statusTimer;
        private bool _windowsReady;

        private static readonly Brush StatusIdleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8D, 0x99, 0xA3)));
        private static readonly Brush StatusReadyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xB4, 0x50)));

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        #endregion

        #region Initialization

        public MainWindow()
        {
            InitializeComponent();
            var randomizer = new WindowRandomizer();
            randomizer.Randomize();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveDictionary.EnsureDirectoriesExist();

                // The model list must exist before FileManager starts (it fills the list and may load the last model)
                _modelMenu = new ModelMenuControl();
                _modelMenu.Initialize(this);
                InitializeFileManager(_modelMenu);

                // Load saved settings before any control reads them
                await LoadConfigurationsAsync();

                BuildPages();
                ShowPage("Home", animate: false);

                StartCOMPortsUpdater();
                StartStatusUpdater();
                // Only nag about a missing RP2350 when it is the selected device
                string? movementMethod = Dictionary.dropdownState["Mouse Movement Method"]?.ToString();
                ConnectHidDevice(showMessageIfMissing: movementMethod == "RP2350");

                await InitializeApplicationAsync();
                _windowsReady = true;
                ApplyFovStyle();

                UpdateAboutSpecs();
                ThemeManager.LoadMediaSettings();
            }
            catch (Exception ex)
            {
                ShowError($"Error during startup: {ex.Message}", ex);
            }
        }

        /// <summary>Tries to open the RP2350 HID mouse. Returns true when it is connected.</summary>
        internal bool ConnectHidDevice(bool showMessageIfMissing)
        {
            var device = new HidMouseDevice();
            if (!device.Connect())
            {
                if (showMessageIfMissing)
                    MessageBox.Show("RP2350 HID Device not found. Please ensure it is plugged in.");
                return false;
            }

            MouseManager.HidDevice?.Dispose();
            MouseManager.HidDevice = device;
            return true;
        }

        private void InitializeFileManager(ModelMenuControl modelMenu)
        {
            if (_fileManager != null) return;

            _fileManager = new Lazy<FileManager>(() => new FileManager(
                modelMenu.ModelListBoxControl,
                modelMenu.SelectedModelNotifierControl,
                modelMenu.ConfigsListBoxControl,
                modelMenu.SelectedConfigNotifierControl));

            try
            {
                _ = _fileManager.Value;
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, $"File manager failed to start: {ex.Message}");
            }
        }

        private async Task InitializeApplicationAsync()
        {
            CheckRunningFromTemp();

            // Initialize DisplayManager FIRST before anything else that depends on display info
            DisplayManager.Initialize();

            // Now that DisplayManager is initialized, we can create windows
            InitializeWindows();

            EnsureRequiredFiles();

            SetupKeybindings();
            ConfigurePropertyChangers();
            ApplyInitialSettings();
            ListenForKeybinds();

            // Subscribe to display changes after everything is initialized
            DisplayManager.DisplayChanged += OnDisplayChanged;
            await Task.CompletedTask;
        }

        private void OnDisplayChanged(object? sender, DisplayChangedEventArgs e)
        {
            // Force update all windows to new display
            DisplayManager.ForceUpdateWindows();

            Dispatcher.BeginInvoke(() =>
            {
                Dictionary.sliderSettings["SelectedDisplay"] = e.DisplayIndex;
                LogManager.Log(LogManager.LogLevel.Info, $"AI focus switched to Display {e.DisplayIndex + 1} ({e.Bounds.Width}x{e.Bounds.Height})", true);
            });
        }

        private void CheckRunningFromTemp()
        {
            if (Directory.GetCurrentDirectory().Contains("Temp"))
            {
                MessageBox.Show(
                    "Hi, it is made aware that you are running Whales without extracting it from the zip file. " +
                    "Please extract Whales from the zip file or Whales will not be able to run properly.\n\nThank you.",
                    "Whales V2");
            }
        }

        private void InitializeWindows()
        {
            // Create windows but don't show them yet
            var fov = FOVWindow;  // This triggers lazy initialization
            var dpw = DPWindow;   // This triggers lazy initialization

            // Ensure they're positioned on the current display
            fov.ForceReposition();
            dpw.ForceReposition();

            // Set references in Dictionary
            Dictionary.DetectedPlayerOverlay = dpw;
            Dictionary.FOVWindow = fov;
        }

        private void EnsureRequiredFiles()
        {
            var labelsPath = "bin\\labels\\labels.txt";
            var labelsDir = Path.GetDirectoryName(labelsPath);

            if (!string.IsNullOrEmpty(labelsDir) && !Directory.Exists(labelsDir))
                Directory.CreateDirectory(labelsDir);

            if (!File.Exists(labelsPath))
                File.WriteAllText(labelsPath, "Enemy");
        }

        private async Task LoadConfigurationsAsync()
        {
            await Task.Run(() =>
            {
                var configs = new[]
                {
                    (Dictionary.minimizeState, "bin\\minimize.cfg"),
                    (Dictionary.bindingSettings, "bin\\binding.cfg"),
                    (Dictionary.colorState, "bin\\colors.cfg"),
                    (Dictionary.filelocationState, "bin\\filelocations.cfg"),
                    (Dictionary.dropdownState, "bin\\dropdown.cfg"),
                    (Dictionary.toggleState, "bin\\toggles.cfg")
                };

                foreach (var (dict, path) in configs)
                    SaveDictionary.LoadJSON(dict, path);
            });

            // These might show notifications, so run them on the UI thread
            LoadConfig();
            ApplyThemeColorFromConfig();
        }

        private void ApplyThemeColorFromConfig()
        {
            if (!Dictionary.colorState.TryGetValue("Theme Color", out var themeColor)) return;

            var colorString = themeColor?.ToString();
            if (string.IsNullOrEmpty(colorString)) return;

            try
            {
                ThemeManager.SetThemeColor(colorString);
            }
            catch
            {
                // Keep the default colour if the saved one is invalid
            }
        }

        private void SetupKeybindings()
        {
            var keybinds = new[]
            {
                "Aim Keybind", "Second Aim Keybind", "Dynamic FOV Keybind",
                "Emergency Stop Keybind", "Model Switch Keybind"
            };

            foreach (var keybind in keybinds)
                bindingManager.SetupDefault(keybind, Dictionary.bindingSettings[keybind].ToString());
        }

        private void ConfigurePropertyChangers()
        {
            PropertyChanger.ReceiveNewConfig = LoadConfig;
        }

        private void ApplyInitialSettings()
        {
            // FOV settings
            ActualFOV = Convert.ToDouble(Dictionary.sliderSettings["FOV Size"]);
            PropertyChanger.PostNewFOVSize(ActualFOV);
            PropertyChanger.PostColor((Color)ColorConverter.ConvertFromString(Dictionary.colorState["FOV Color"].ToString()));

            // Detected player window settings
            var dpSettings = new[]
            {
                ("Detected Player Color", (Action<object>)(c => PropertyChanger.PostDPColor((Color)c))),
                ("AI Confidence Font Size", (Action<object>)(s => PropertyChanger.PostDPFontSize((int)(double)s))),
                ("Corner Radius", (Action<object>)(r => PropertyChanger.PostDPWCornerRadius((int)(double)r))),
                ("Border Thickness", (Action<object>)(t => PropertyChanger.PostDPWBorderThickness((double)t))),
                ("Opacity", (Action<object>)(o => PropertyChanger.PostDPWOpacity((double)o)))
            };

            foreach (var (key, action) in dpSettings)
            {
                if (key.Contains("Color"))
                    action(ColorConverter.ConvertFromString(Dictionary.colorState[key].ToString()));
                else
                    action(Convert.ToDouble(Dictionary.sliderSettings[key]));
            }

            // Overlays that were left on last session
            if (Dictionary.toggleState["FOV"]) Toggle_Action("FOV");
            if (Dictionary.toggleState["Show Detected Player"]) Toggle_Action("Show Detected Player");
            if (Dictionary.toggleState["UI TopMost"]) Toggle_Action("UI TopMost");
            if (Dictionary.toggleState["StreamGuard"]) Toggle_Action("StreamGuard");
            // The saved smoothing switch and amount were previously only applied after touching them
            MouseManager.IsEMASmoothingEnabled = Dictionary.toggleState["EMA Smoothening"];
            if (Dictionary.sliderSettings.TryGetValue("EMA Smoothening", out var ema))
                MouseManager.smoothingFactor = Convert.ToDouble(ema);
        }

        private void UpdateAboutSpecs()
        {
            if (_aboutMenu == null) return;

            _aboutMenu.AboutSpecsControl.Content = "Loading system specs...";

            Task.Run(() =>
            {
                string specs;
                try
                {
                    specs = $"{GetProcessorName()} • {GetVideoControllerName()} • {GetFormattedMemorySize()}GB RAM";
                }
                catch
                {
                    specs = "System specs unavailable";
                }
                Dispatcher.Invoke(() => _aboutMenu.AboutSpecsControl.Content = specs);
            });
        }

        private void ShowError(string message, Exception ex)
        {
            MessageBox.Show($"{message}\n\nStack trace: {ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #endregion

        #region COM Ports

        private void StartCOMPortsUpdater()
        {
            comPortTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            comPortTimer.Tick += (s, e) => UpdateCOMPortsTextBlock();
            comPortTimer.Start();
            UpdateCOMPortsTextBlock();
        }

        private void UpdateCOMPortsTextBlock()
        {
            string[] ports = SerialPort.GetPortNames();

            UpdatePortChip(ports);
            UpdateDevicePortList(ports);

            if (COMPortsTextBlock == null) return;
            if (PortPopup != null && PortPopup.IsOpen) return;

            COMPortsTextBlock.Inlines.Clear();

            if (ports.Length == 0)
            {
                COMPortsTextBlock.Inlines.Add(new Run("No COM ports detected.\nPlug in your Arduino and try again.")
                {
                    Foreground = (Brush)FindResource("UI.TextDim")
                });
                return;
            }

            foreach (var port in ports)
            {
                bool connected = port == connectedPort;
                var link = new Hyperlink(new Run(connected ? $"{port}   (connected)" : port))
                {
                    TextDecorations = null,
                    FontWeight = connected ? FontWeights.Bold : FontWeights.Normal,
                    ToolTip = connected ? $"Connected to {port}" : $"Click to connect to {port}"
                };

                if (connected)
                    link.SetResourceReference(TextElement.ForegroundProperty, "ThemeColor");
                else
                    link.Foreground = (Brush)FindResource("UI.Text");

                link.Click += (_, _) =>
                {
                    ConnectPort(port);
                    if (PortPopup != null) PortPopup.IsOpen = false;
                };

                COMPortsTextBlock.Inlines.Add(link);
                COMPortsTextBlock.Inlines.Add(new LineBreak());
            }
        }

        /// <summary>Opens the Arduino on the given COM port and refreshes every port display.</summary>
        internal void ConnectPort(string port)
        {
            // InitArduino shows its own error message when the port can't be opened
            connectedPort = MouseManager.InitArduino(port) ? port : null;

            if (PortPopup?.IsOpen == true) PortPopup.IsOpen = false;
            UpdateCOMPortsTextBlock();
            UpdateStatus();
        }

        internal void DisconnectPort()
        {
            MouseManager.CloseArduino();
            connectedPort = null;
            UpdateCOMPortsTextBlock();
            UpdateStatus();
        }

        private void UpdatePortChip(string[] ports)
        {
            if (PortChipValue == null || PortDot == null) return;

            if (!string.IsNullOrEmpty(connectedPort) && Array.IndexOf(ports, connectedPort) >= 0)
            {
                PortChipValue.Text = connectedPort;
                PortChipValue.SetResourceReference(TextBlock.ForegroundProperty, "ThemeColor");
                PortDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ThemeColor");
            }
            else
            {
                connectedPort = null;
                PortChipValue.Text = ports.Length == 0 ? "No ports" : "Click to connect";
                PortChipValue.SetResourceReference(TextBlock.ForegroundProperty, "UI.Text");
                PortDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UI.TextDim");
            }
        }

        private void PortChip_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (PortPopup == null) return;

            bool shouldOpen = !PortPopup.IsOpen;
            PortPopup.IsOpen = false;

            if (shouldOpen)
            {
                UpdateCOMPortsTextBlock();
                Dispatcher.BeginInvoke(new Action(() => PortPopup.IsOpen = true), DispatcherPriority.Input);
            }
        }

        #endregion

        #region Window Events

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (PortPopup == null || !PortPopup.IsOpen) return;

            try
            {
                var hit = e.OriginalSource as DependencyObject;
                while (hit != null)
                {
                    if (hit == PortChip || hit == PortPopup.Child) return;

                    DependencyObject? parent = null;
                    if (hit is Visual || hit is System.Windows.Media.Media3D.Visual3D)
                        parent = VisualTreeHelper.GetParent(hit);

                    if (parent == null)
                    {
                        if (hit is FrameworkElement fe) parent = fe.Parent ?? fe.TemplatedParent;
                        else if (hit is FrameworkContentElement fce) parent = fce.Parent ?? fce.TemplatedParent;
                    }

                    if (parent == null || parent == hit) break;
                    hit = parent;
                }
                PortPopup.IsOpen = false;
            }
            catch
            {
                PortPopup.IsOpen = false;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_fileManager?.IsValueCreated == true)
                fileManager.InQuittingState = true;

            DisableAllFeatures();
            CloseWindows();
            DisposePages();

            SaveAllConfigurations();
            FileManager.AIManager?.Dispose();

            DisplayManager.DisplayChanged -= OnDisplayChanged;
            DisplayManager.Dispose();

            Application.Current.Shutdown();
        }

        private void DisableAllFeatures()
        {
            foreach (var feature in new[] { "Aim Assist", "FOV", "Show Detected Player" })
                Dictionary.toggleState[feature] = false;
        }

        private void CloseWindows()
        {
            FOVWindow.Close();
            DPWindow.Close();
        }

        private void SaveAllConfigurations()
        {
            Dictionary.colorState["Theme Color"] = ThemeManager.GetThemeColorHex();

            SaveDictionary.WriteJSON(Dictionary.sliderSettings
                .Concat(Dictionary.dropdownState)
                .GroupBy(kvp => kvp.Key)
                .ToDictionary(g => g.Key, g => g.First().Value));
            SaveDictionary.WriteJSON(Dictionary.minimizeState, "bin\\minimize.cfg");
            SaveDictionary.WriteJSON(Dictionary.bindingSettings, "bin\\binding.cfg");
            SaveDictionary.WriteJSON(Dictionary.dropdownState, "bin\\dropdown.cfg");
            SaveDictionary.WriteJSON(Dictionary.colorState, "bin\\colors.cfg");
            SaveDictionary.WriteJSON(Dictionary.filelocationState, "bin\\filelocations.cfg");
            SaveDictionary.WriteJSON(Dictionary.toggleState, "bin\\toggles.cfg");
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Never steal keys from text input or from an in-progress keybind capture
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
            if (bindingManager.IsCapturingBinding) return;

            var modifiers = Keyboard.Modifiers;
            if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
            {
                CyclePage(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
        }

        #endregion

        #region Status

        private void StartStatusUpdater()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
        }

        /// <summary>Refreshes the header chips, the sidebar aim card and the Home page.</summary>
        internal void UpdateStatus()
        {
            var aiManager = FileManager.AIManager;
            var fps = aiManager?.CurrentFps ?? 0;
            FpsText.Text = fps > 0 ? Math.Round(fps).ToString() : "0";
            if (fps > 0)
                FpsText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeColor");
            else
                FpsText.Foreground = (Brush)FindResource("UI.TextDim");

            bool running = aiManager?.IsRunning == true;
            ProviderText.Text = running ? Whales2.AILogic.AIManager.ExecutionProvider : "N/A";

            var model = Dictionary.lastLoadedModel;
            bool hasModel = !string.IsNullOrEmpty(model) && model != "N/A";
            bool aimOn = Dictionary.toggleState["Aim Assist"];
            var modelName = hasModel ? Path.GetFileNameWithoutExtension(model) : "";

            if (!hasModel)
            {
                StatusText.Text = "No model loaded";
                StatusDot.Fill = StatusIdleBrush;
            }
            else if (!running)
            {
                bool loading = FileManager.CurrentlyLoadingModel;
                StatusText.Text = $"{(loading ? "Loading" : "Load failed")} · {modelName}";
                StatusDot.Fill = loading ? StatusReadyBrush : StatusIdleBrush;
            }
            else
            {
                StatusText.Text = $"{(aimOn ? "Active" : "Ready")} · {modelName}";
                if (aimOn)
                    StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ThemeColor");
                else
                    StatusDot.Fill = StatusReadyBrush;
            }
            StatusText.ToolTip = StatusText.Text;

            _sidebarAimSwitch?.Set(aimOn);
            UpdateHomePage(fps, running, hasModel, modelName, aimOn);
        }

        #endregion

        #region Toggle Actions

        internal void Toggle_Action(string title)
        {
            switch (title)
            {
                case "FOV":
                    FOVWindow.Visibility = GetToggleVisibility(title);
                    if (Dictionary.toggleState[title])
                        FOVWindow.ForceReposition();
                    break;

                case "Show Detected Player":
                    ShowHideDPWindow();
                    DPWindow.DetectedPlayerFocus.Visibility = GetToggleVisibility(title, true);
                    if (Dictionary.toggleState[title])
                        DPWindow.ForceReposition();
                    break;

                case "Show AI Confidence":
                    DPWindow.DetectedPlayerConfidence.Visibility = GetToggleVisibility(title, true);
                    break;

                case "UI TopMost":
                    Topmost = Dictionary.toggleState[title];
                    break;

                case "StreamGuard":
                    StreamGuardManager.ApplyStreamGuardToAllWindows(Dictionary.toggleState[title]);
                    break;

                case "EMA Smoothening":
                    MouseManager.IsEMASmoothingEnabled = Dictionary.toggleState[title];
                    break;
            }
        }

        private Visibility GetToggleVisibility(string title, bool collapsed = false) =>
            Dictionary.toggleState[title]
                ? Visibility.Visible
                : (collapsed ? Visibility.Collapsed : Visibility.Hidden);

        private static void ShowHideDPWindow()
        {
            if (Dictionary.toggleState["Show Detected Player"])
            {
                DPWindow.Show();
                DPWindow.ForceReposition();
            }
            else
            {
                DPWindow.Hide();
            }
        }

        #endregion

        #region UI Helper Methods

        public void UpdateToggleUI(AToggle toggle, bool isEnabled)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (isEnabled)
                    toggle.EnableSwitch();
                else
                    toggle.DisableSwitch();
            });
        }

        public ComboBoxItem AddDropdownItem(ADropdown dropdown, string title)
        {
            var dropdownitem = new ComboBoxItem
            {
                Content = title,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                FontFamily = TryFindResource("UIFont") as FontFamily
            };

            dropdownitem.Selected += (s, e) =>
            {
                Dictionary.dropdownState[dropdown.DictionaryKey] = title;
            };

            dropdown.DropdownBox.Items.Add(dropdownitem);
            return dropdownitem;
        }

        #endregion

        #region Keybind Handling

        private void ListenForKeybinds()
        {
            bindingManager.OnBindingPressed += HandleKeybindPressed;
            bindingManager.OnBindingReleased += HandleKeybindReleased;
        }

        private void HandleKeybindPressed(string bindingId)
        {
            switch (bindingId)
            {
                case "Model Switch Keybind": HandleModelSwitch(); break;
                case "Dynamic FOV Keybind": ApplyDynamicFOV(true); break;
                case "Emergency Stop Keybind": HandleEmergencyStop(); break;
            }
        }

        private void HandleKeybindReleased(string bindingId)
        {
            if (bindingId == "Dynamic FOV Keybind")
                ApplyDynamicFOV(false);
        }

        private void HandleModelSwitch()
        {
            if (!Dictionary.toggleState["Enable Model Switch Keybind"] || FileManager.CurrentlyLoadingModel)
                return;

            var modelListBox = _modelMenu.ModelListBoxControl;
            modelListBox.SelectedIndex = (modelListBox.SelectedIndex >= 0 &&
                modelListBox.SelectedIndex < modelListBox.Items.Count - 1)
                ? modelListBox.SelectedIndex + 1
                : 0;
        }

        private void ApplyDynamicFOV(bool apply)
        {
            if (!Dictionary.toggleState["Dynamic FOV"])
            {
                FOVWindow.Circle.BeginAnimation(FrameworkElement.WidthProperty, null);
                FOVWindow.Circle.BeginAnimation(FrameworkElement.HeightProperty, null);
                FOVWindow.RectangleShape.BeginAnimation(FrameworkElement.WidthProperty, null);
                FOVWindow.RectangleShape.BeginAnimation(FrameworkElement.HeightProperty, null);

                FOVWindow.UpdateFOVSize(ActualFOV);
                return;
            }
            var targetSize = apply ? Convert.ToDouble(Dictionary.sliderSettings["Dynamic FOV Size"]) : ActualFOV;
            Dictionary.sliderSettings["FOV Size"] = targetSize;
            AnimateFOVSize(targetSize);
        }

        private void AnimateFOVSize(double targetSize)
        {
            var duration = TimeSpan.FromMilliseconds(500);
            Animator.WidthShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualWidth, targetSize);
            Animator.HeightShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualHeight, targetSize);
            Animator.WidthShift(duration, FOVWindow.RectangleShape, FOVWindow.RectangleShape.ActualWidth, targetSize);
            Animator.HeightShift(duration, FOVWindow.RectangleShape, FOVWindow.RectangleShape.ActualHeight, targetSize);
        }

        private void HandleEmergencyStop()
        {
            bool newState = !Dictionary.toggleState["Aim Assist"];

            Dictionary.toggleState["Aim Assist"] = newState;

            if (uiManager.T_AimAligner != null)
                UpdateToggleUI(uiManager.T_AimAligner, newState);
            UpdateStatus();

            new NoticeBar(
                $"[Emergency Stop Keybind] {(newState ? "Enabled" : "Disabled")} all AI features.",
                4000
            ).Show();
        }

        #endregion

        #region Configuration Management

        private void LoadConfig(string path = "bin\\configs\\Default.cfg", bool loading_from_configlist = false)
        {
            SaveDictionary.LoadJSON(Dictionary.sliderSettings, path);
            SaveDictionary.LoadJSON(Dictionary.dropdownState, path);

            if (!loading_from_configlist || !_pagesBuilt)
                return;

            try
            {
                ShowSuggestedModelIfSpecified();
                ApplyConfigToControls();
            }
            catch (Exception e)
            {
                MessageBox.Show($"Error loading config, possibly outdated\n{e}");
            }
        }

        private void ShowSuggestedModelIfSpecified()
        {
            if (Dictionary.sliderSettings.TryGetValue("Suggested Model", out var model))
            {
                var suggestedModel = model?.ToString() ?? "N/A";
                if (suggestedModel != "N/A" && !string.IsNullOrEmpty(suggestedModel))
                {
                    MessageBox.Show(
                        $"The creator of this model suggests you use this model:\n{suggestedModel}",
                        "Suggested Model - Whales");
                }
            }
        }

        /// <summary>Pushes the values from a freshly loaded config into every slider and dropdown.</summary>
        private void ApplyConfigToControls()
        {
            foreach (var (key, slider) in _sliders)
            {
                if (Dictionary.sliderSettings.TryGetValue(key, out var value))
                    slider.Slider.Value = Convert.ToDouble(value);
            }

            RestoreDropdowns();
            RefreshConditionalRows();
        }

        /// <summary>Called by AIManager when a fixed-size model forces a different image size.</summary>
        public void UpdateImageSizeDropdown(string newSize)
        {
            if (uiManager.D_ImageSize == null) return;

            _suppressDropdownSideEffects = true;
            try
            {
                SelectDropdownValue(uiManager.D_ImageSize, newSize);
            }
            finally
            {
                _suppressDropdownSideEffects = false;
            }
        }

        #endregion

        #region System Information

        private static string? GetProcessorName() => GetSpecs.GetSpecification("Win32_Processor", "Name");
        private static string? GetVideoControllerName() => GetSpecs.GetSpecification("Win32_VideoController", "Name");
        private static string? GetFormattedMemorySize()
        {
            var totalMemorySize = long.Parse(GetSpecs.GetSpecification("CIM_OperatingSystem", "TotalVisibleMemorySize")!);
            return Math.Round(totalMemorySize / (1024.0 * 1024.0), 0).ToString();
        }

        #endregion
    }

    #region Extension Methods

    internal static class DictionaryExtensions
    {
        public static T GetValueOrDefault<T>(this Dictionary<string, T> dictionary, string key, T defaultValue) =>
            dictionary.TryGetValue(key, out var value) ? value : defaultValue;

        public static T GetValueOrDefault<T>(this Dictionary<string, dynamic> dictionary, string key, T defaultValue)
        {
            if (dictionary.TryGetValue(key, out var value))
            {
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }

    #endregion
}
