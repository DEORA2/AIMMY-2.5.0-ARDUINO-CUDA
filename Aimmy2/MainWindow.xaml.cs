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
using System.Windows.Media.Animation;
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
        public SettingsMenuControl? SettingsMenuControlInstance { get; set; }
        internal Dictionary<string, AToggle> toggleInstances = new();
        private readonly Dictionary<string, UserControl?> _menuControls = new();
        private readonly Dictionary<string, bool> _menuInitialized = new();
        private UserControl? _currentControl;
        private string _currentMenu = "AimMenu";
        private bool _currentlySwitching;
        private ScrollViewer? CurrentScrollViewer;
        public double ActualFOV { get; set; } = 640;
        private double _currentGradientAngle;

        // Menu names constant
        private static readonly string[] MenuNames = { "AimMenu", "ModelMenu", "SettingsMenu", "AboutMenu" };

        #endregion

        #region Initialization

        public MainWindow()
        {
            InitializeComponent();
            var randomizer = new WindowRandomizer();
            randomizer.Randomize();

            TabStrip.SizeChanged += (_, _) => MoveTabIndicator(animate: false);
        }

        private string connectedPort;
        private DispatcherTimer comPortTimer;

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveDictionary.EnsureDirectoriesExist();

                InitializeMenus();
                InitializeFileManagerEarly();

                // Load configurations BEFORE loading any menus
                // This ensures minimize states are loaded from file before menu initialization
                await LoadConfigurationsAsync();

                // Now load the initial menu - it will use the loaded minimize states
                LoadInitialMenu();
                StartCOMPortsUpdater();
                StartStatusUpdater();
                var device = new HidMouseDevice();
                if (!device.Connect())
                {
                    MessageBox.Show(
                        "RP2350 HID Device not found. Please ensure it is plugged in.");
                }
                else
                {
                    MouseManager.HidDevice?.Dispose();
                    MouseManager.HidDevice = device;
                }
                

                // Continue with the rest of initialization
                await InitializeApplicationAsync();
                UpdateAboutSpecs();
                ApplyThemeGradients();
                ThemeManager.LoadMediaSettings();
            }
            catch (Exception ex)
            {
                ShowError($"Error during startup: {ex.Message}", ex);
            }
        }

        private void StartCOMPortsUpdater()
        {
            comPortTimer = new DispatcherTimer();
            comPortTimer.Interval = TimeSpan.FromSeconds(2);
            comPortTimer.Tick += (s, e) => UpdateCOMPortsTextBlock();
            comPortTimer.Start();
        }
        private void UpdateCOMPortsTextBlock()
        {
            string[] ports = SerialPort.GetPortNames();

            UpdatePortChip(ports);

            if (COMPortsTextBlock == null) return;

            if (PortPopup != null && PortPopup.IsOpen) return;

            COMPortsTextBlock.Inlines.Clear();

            if (ports.Length == 0)
            {
                COMPortsTextBlock.Inlines.Add(new Run("No COM ports detected.\nPlug in your Arduino and try again.")
                {
                    Foreground = Brushes.Gray
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

                link.Click += (sender2, args) =>
                {
                    try
                    {
                        MouseManager.InitArduino(port);
                        connectedPort = port;
                        UpdateCOMPortsTextBlock();
                        if (PortPopup != null) PortPopup.IsOpen = false;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to connect to {port}:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };

                COMPortsTextBlock.Inlines.Add(link);
                COMPortsTextBlock.Inlines.Add(new LineBreak());
            }
        }

        private void UpdatePortChip(string[] ports)
        {
            if (PortChipValue == null || PortDot == null) return;

            if (!string.IsNullOrEmpty(connectedPort) && Array.IndexOf(ports, connectedPort) >= 0)
            {
                PortChipValue.Text = connectedPort;
                PortChipValue.SetResourceReference(TextElement.ForegroundProperty, "ThemeColor");
                PortDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ThemeColor");
            }
            else
            {
                connectedPort = null;
                PortChipValue.Text = ports.Length == 0 ? "NO PORTS" : "CLICK TO CONNECT";
                PortChipValue.SetResourceReference(TextElement.ForegroundProperty, "UI.Text");
                PortDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UI.TextDim");
            }
        }

        private void PortChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (PortPopup == null) return;

            bool shouldOpen = !PortPopup.IsOpen;
            PortPopup.IsOpen = false;

            if (shouldOpen)
            {
                UpdateCOMPortsTextBlock();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PortPopup.IsOpen = true;
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void InitializeMenus()
        {
            foreach (var menu in MenuNames)
            {
                _menuControls[menu] = null;
                _menuInitialized[menu] = false;
            }
        }

        private void InitializeFileManagerEarly()
        {
            var modelMenu = new ModelMenuControl();
            modelMenu.Initialize(this);
            _menuControls["ModelMenu"] = modelMenu;
            InitializeFileManager(modelMenu);
        }

        private void LoadInitialMenu()
        {
            LoadMenu("AimMenu");
            // Don't call UpdateSliderVisibility here - it would override collapsed menu states
            // Visibility is handled by the toggle click actions when user interacts with toggles
            _currentMenu = "AimMenu";
            UpdateTabVisuals(_currentMenu, animate: false);
            ResetDetailPanel();
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
        }

        private void OnDisplayChanged(object? sender, DisplayChangedEventArgs e)
        {

            // Force update all windows to new display
            DisplayManager.ForceUpdateWindows();
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

            // Ensure the directory exists
            if (!string.IsNullOrEmpty(labelsDir) && !Directory.Exists(labelsDir))
            {
                Directory.CreateDirectory(labelsDir);
            }

            // Create the file if it doesn't exist
            if (!File.Exists(labelsPath))
            {
                File.WriteAllText(labelsPath, "Enemy");
            }
        }

        private async Task LoadConfigurationsAsync()
        {
            // Run non-UI operations in background
            await Task.Run(() =>
            {
                // Load configurations that don't create UI
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
                {
                    SaveDictionary.LoadJSON(dict, path);
                }
            });

            // Load these on UI thread since they might show notifications
            LoadConfig();
            ApplyThemeColorFromConfig();
        }


        private void ApplyThemeColorFromConfig()
        {
            if (Dictionary.colorState.TryGetValue("Theme Color", out var themeColor))
            {
                var colorString = themeColor?.ToString();
                if (!string.IsNullOrEmpty(colorString))
                {
                    try
                    {
                        ThemeManager.SetThemeColor(colorString);
                    }
                    catch (Exception ex)
                    {
                    }
                }
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
            {
                bindingManager.SetupDefault(keybind, Dictionary.bindingSettings[keybind].ToString());
            }
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
                {
                    action(ColorConverter.ConvertFromString(Dictionary.colorState[key].ToString()));
                }
                else
                {
                    action(Convert.ToDouble(Dictionary.sliderSettings[key]));
                }
            }
        }

        private void UpdateAboutSpecs()
        {
            if (_menuControls["AboutMenu"] is AboutMenuControl aboutMenu)
            {
                aboutMenu.AboutSpecsControl.Content = "Loading system specs...";

                Task.Run(() =>
                {
                    var specs = $"{GetProcessorName()} • {GetVideoControllerName()} • {GetFormattedMemorySize()}GB RAM";
                    Dispatcher.Invoke(() => aboutMenu.AboutSpecsControl.Content = specs);
                });
            }
        }

        private void ShowError(string message, Exception ex)
        {
            MessageBox.Show($"{message}\n\nStack trace: {ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ApplyThemeGradients()
        {
            if (!Dictionary.colorState.TryGetValue("Theme Color", out var themeColor)) return;

            var colorString = themeColor?.ToString();
            if (string.IsNullOrEmpty(colorString)) return;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);

                var gradientMappings = new Dictionary<string, Func<Color, Color>>
                {
                    ["GradientThemeStop"] = c => Color.FromRgb((byte)(c.R * 0.3), (byte)(c.G * 0.3), (byte)(c.B * 0.3)),
                    ["HighlighterGradient1"] = c => c,
                    ["HighlighterGradient2"] = c => Color.FromArgb(102, c.R, c.G, c.B)
                };

                foreach (var (elementName, colorTransform) in gradientMappings)
                {
                    if (FindName(elementName) is GradientStop gradientStop)
                    {
                        gradientStop.Color = colorTransform(color);
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }

        private void InitializeFileManager(ModelMenuControl modelMenu)
        {
            if (_fileManager == null)
            {
                _fileManager = new Lazy<FileManager>(() => new FileManager(
                    modelMenu.ModelListBoxControl,
                    modelMenu.SelectedModelNotifierControl,
                    modelMenu.ConfigsListBoxControl,
                    modelMenu.SelectedConfigNotifierControl));

                try
                {
                    var fm = _fileManager.Value;
                }
                catch (Exception ex)
                {
                }
            }
        }

        #endregion

        #region Window Events

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

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
                    if (hit is System.Windows.Media.Visual || hit is System.Windows.Media.Media3D.Visual3D)
                        parent = System.Windows.Media.VisualTreeHelper.GetParent(hit);

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
            {
                fileManager.InQuittingState = true;
            }

            DisableAllFeatures();
            CloseWindows();

            // Dispose menu controls to save their states
            if (_menuControls["AimMenu"] is AimMenuControl aimMenu)
                aimMenu.Dispose();

            if (_menuControls["SettingsMenu"] is SettingsMenuControl settingsMenu)
                settingsMenu.Dispose();

            SaveAllConfigurations();
            FileManager.AIManager?.Dispose();

            // Clean up display manager
            DisplayManager.DisplayChanged -= OnDisplayChanged;
            DisplayManager.Dispose();

            Application.Current.Shutdown();
        }

        private void DisableAllFeatures()
        {
            var features = new[] { "Aim Assist", "FOV", "Show Detected Player" };
            foreach (var feature in features)
            {
                Dictionary.toggleState[feature] = false;
            }
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
                //.Where(kvp => kvp.Key != "Screen Capture Method")
                .GroupBy(kvp => kvp.Key)
                .ToDictionary(g => g.Key, g => g
                .First().Value));
            SaveDictionary.WriteJSON(Dictionary.minimizeState, "bin\\minimize.cfg");
            SaveDictionary.WriteJSON(Dictionary.bindingSettings, "bin\\binding.cfg");
            SaveDictionary.WriteJSON(Dictionary.dropdownState, "bin\\dropdown.cfg");
            SaveDictionary.WriteJSON(Dictionary.colorState, "bin\\colors.cfg");
            SaveDictionary.WriteJSON(Dictionary.filelocationState, "bin\\filelocations.cfg");
            SaveDictionary.WriteJSON(Dictionary.toggleState, "bin\\toggles.cfg");
        }

        #endregion

        #region Menu Management

        private UserControl GetOrCreateMenuControl(string menuName)
        {
            if (_menuControls[menuName] != null)
                return _menuControls[menuName]!;

            var newControl = menuName == "ModelMenu" && _menuControls["ModelMenu"] != null
                ? _menuControls["ModelMenu"]!
                : CreateMenuControl(menuName);

            _menuControls[menuName] = newControl;

            if (!_menuInitialized[menuName])
            {
                InitializeMenuControl(menuName, newControl);
                _menuInitialized[menuName] = true;
            }

            return newControl;
        }

        private UserControl CreateMenuControl(string menuName) => menuName switch
        {
            "AimMenu" => new AimMenuControl(),
            "ModelMenu" => new ModelMenuControl(),
            "SettingsMenu" => new SettingsMenuControl(),
            "AboutMenu" => new AboutMenuControl(),
            _ => throw new ArgumentException($"Unknown menu: {menuName}")
        };

        private void InitializeMenuControl(string menuName, UserControl control)
        {
            try
            {
                switch (control)
                {
                    case AimMenuControl aimMenu:
                        aimMenu.Initialize(this);
                        CurrentScrollViewer = aimMenu.AimMenuScrollViewer;
                        LoadDropdownStates();
                        break;

                    case ModelMenuControl modelMenu:
                        if (!_menuInitialized["ModelMenu"])
                            modelMenu.Initialize(this);
                        break;

                    case SettingsMenuControl settingsMenu:
                        settingsMenu.Initialize(this);
                        LoadDropdownStates();
                        SettingsMenuControlInstance = settingsMenu;
                        break;

                    case AboutMenuControl aboutMenu:
                        aboutMenu.Initialize(this);
                        UpdateAboutSpecs();
                        break;
                }
            }
            catch (Exception ex)
            {
            }
        }

        private void LoadMenu(string menuName)
        {
            var control = GetOrCreateMenuControl(menuName);
            ContentArea.Children.Clear();
            ContentArea.Children.Add(control);
            _currentControl = control;
            UpdateCurrentScrollViewer(menuName, control);
        }

        private void UpdateCurrentScrollViewer(string menuName, UserControl control)
        {
            CurrentScrollViewer = control switch
            {
                AimMenuControl aim => aim.AimMenuScrollViewer,
                ModelMenuControl model => model.ModelMenuScrollViewer,
                SettingsMenuControl settings => settings.SettingsMenuScrollViewer,
                AboutMenuControl about => about.AboutMenuScrollViewer,
                _ => CurrentScrollViewer
            };
        }

        private async void MenuSwitch(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string newMenuName })
                await SwitchMenuAsync(newMenuName);
        }

        private async Task SwitchMenuAsync(string newMenuName)
        {
            if (!IsValidMenu(newMenuName) ||
                _currentlySwitching ||
                _currentMenu == newMenuName) return;

            _currentlySwitching = true;

            try
            {
                UpdateTabVisuals(newMenuName, animate: true);
                await SwitchToMenu(newMenuName);
                _currentMenu = newMenuName;
                ResetDetailPanel();
            }
            catch (Exception ex)
            {
            }
            finally
            {
                _currentlySwitching = false;
            }
        }

        private void CycleMenu(int direction)
        {
            var index = Array.IndexOf(MenuNames, _currentMenu);
            var next = MenuNames[(index + direction + MenuNames.Length) % MenuNames.Length];
            _ = SwitchMenuAsync(next);
        }

        private void PrevTab_Click(object sender, RoutedEventArgs e) => CycleMenu(-1);
        private void NextTab_Click(object sender, RoutedEventArgs e) => CycleMenu(1);

        private bool IsValidMenu(string? menuName) =>
            !string.IsNullOrEmpty(menuName) && _menuControls.ContainsKey(menuName!);

        private async Task SwitchToMenu(string menuName)
        {
            if (_currentControl != null)
            {
                Animator.FadeOut(_currentControl);
                await Task.Delay(150); // Fade between menu content
            }

            LoadMenu(menuName);
            Animator.Fade(_currentControl!);
        }

        #endregion

        #region Tab Bar, Detail Panel and Status

        private static readonly Dictionary<string, (string Title, string Description)> MenuInfo = new()
        {
            ["AimMenu"] = ("Aim", "Aim assist, aim tuning, prediction, auto trigger and FOV options."),
            ["ModelMenu"] = ("Models", "Load local models and configs, or download new ones from the store."),
            ["SettingsMenu"] = ("Settings", "General behaviour, display selection, model settings and theming."),
            ["AboutMenu"] = ("About", "Version information, system specs and credits.")
        };

        private static readonly Duration TabAnimationDuration = new(TimeSpan.FromMilliseconds(220));
        private static readonly IEasingFunction TabEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly Brush BadgeOffBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush StatusFailedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8F, 0x85, 0x89)));
        private static readonly Brush StatusReadyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xB4, 0x50)));

        private string _activeTab = "AimMenu";
        private UserControl? _selectedRow;
        private DispatcherTimer? _statusTimer;

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        private IEnumerable<Button> TabButtons => TabStrip.Children.OfType<Button>();

        private void UpdateTabVisuals(string menuName, bool animate)
        {
            _activeTab = menuName;

            foreach (var tab in TabButtons)
            {
                if ((string)tab.Tag == menuName)
                    tab.Foreground = (Brush)FindResource("UI.Text");
                else
                    tab.ClearValue(ForegroundProperty); // back to the style (dim + hover trigger)
            }

            MoveTabIndicator(animate);
        }

        private void MoveTabIndicator(bool animate)
        {
            var tab = TabButtons.FirstOrDefault(t => (string)t.Tag == _activeTab);
            if (tab == null || tab.ActualWidth <= 0) return;

            var x = tab.TranslatePoint(new Point(0, 0), TabIndicatorCanvas).X;
            var width = tab.ActualWidth;

            if (!animate)
            {
                MenuHighlighterOffset.BeginAnimation(TranslateTransform.XProperty, null);
                MenuHighlighter.BeginAnimation(WidthProperty, null);
                MenuHighlighterOffset.X = x;
                MenuHighlighter.Width = width;
                return;
            }

            MenuHighlighterOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TabAnimationDuration) { EasingFunction = TabEase });
            MenuHighlighter.BeginAnimation(WidthProperty,
                new DoubleAnimation(width, TabAnimationDuration) { EasingFunction = TabEase });
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Never steal keys from text input or from an in-progress keybind capture
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
            if (bindingManager.IsCapturingBinding) return;

            var modifiers = Keyboard.Modifiers;
            if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
            {
                CycleMenu(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (modifiers != ModifierKeys.None) return;

            switch (e.Key)
            {
                case Key.Q:
                    CycleMenu(-1);
                    e.Handled = true;
                    break;
                case Key.E:
                    CycleMenu(1);
                    e.Handled = true;
                    break;
            }
        }

        private static bool IsOptionRow(object element) =>
            element is AToggle or ASlider or ADropdown or AKeyChanger or APButton or AColorChanger or AFileLocator;

        private static UserControl? FindOptionRow(DependencyObject? element)
        {
            while (element != null)
            {
                if (IsOptionRow(element)) return (UserControl)element;
                element = element is Visual
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        private static IEnumerable<UserControl> EnumerateOptionRows(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (IsOptionRow(child))
                {
                    yield return (UserControl)child;
                    continue;
                }
                foreach (var row in EnumerateOptionRows(child))
                    yield return row;
            }
        }

        private void ContentArea_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var row = FindOptionRow(e.OriginalSource as DependencyObject);
            if (row == null) return;

            if (row != _selectedRow)
                SelectRow(row);
            else if (e.LeftButton == MouseButtonState.Pressed)
                RefreshDetailPanel(); // live value while dragging a slider
        }

        private void ContentArea_StateMayHaveChanged(object sender, RoutedEventArgs e)
        {
            // Click handlers run after the preview event, so read the new state once they are done
            Dispatcher.BeginInvoke(() =>
            {
                RefreshDetailPanel();
                UpdateItemCounter();
                UpdateStatusChip();
            }, DispatcherPriority.Background);
        }

        private void SelectRow(UserControl row)
        {
            if (_selectedRow != null)
                _selectedRow.Tag = null;

            _selectedRow = row;
            row.Tag = "selected"; // keeps the row highlight (see UI.RowHighlight style)

            // The detail panel shows the description, so the hover popup would only duplicate it
            ToolTipService.SetIsEnabled(row, false);

            RefreshDetailPanel();
            UpdateItemCounter();
        }

        private void ResetDetailPanel()
        {
            if (_selectedRow != null)
                _selectedRow.Tag = null;
            _selectedRow = null;

            var (title, description) = MenuInfo.GetValueOrDefault(_currentMenu, ("", ""));
            DetailTitle.Text = title;
            DetailDescription.Text = description;
            DetailBadge.Visibility = Visibility.Collapsed;
            DetailHint.Text = "Hover an option to see what it does.  Q / E switch tabs.";

            // Rows only exist in the visual tree after layout
            Dispatcher.BeginInvoke(UpdateItemCounter, DispatcherPriority.Loaded);
        }

        private void RefreshDetailPanel()
        {
            if (_selectedRow == null) return;

            var (title, state, isOn, hint) = DescribeRow(_selectedRow);

            DetailTitle.Text = title;
            DetailDescription.Text = _selectedRow.ToolTip switch
            {
                ToolTip { Content: { } content } => content.ToString(),
                string text => text,
                _ => "No description available for this option."
            };
            DetailHint.Text = hint;

            DetailBadgeText.Text = state.ToUpperInvariant();
            DetailBadge.Visibility = Visibility.Visible;
            if (isOn)
                DetailBadge.SetResourceReference(Border.BackgroundProperty, "ThemeColor");
            else
                DetailBadge.Background = BadgeOffBrush;
        }

        private static (string Title, string State, bool IsOn, string Hint) DescribeRow(UserControl row)
        {
            switch (row)
            {
                case AToggle toggle:
                    {
                        var title = toggle.ToggleTitle.Content?.ToString() ?? "";
                        bool on = Dictionary.toggleState.TryGetValue(title, out var state) ? state : toggle.IsOn;
                        return (title, on ? "Enabled" : "Disabled", on, "Click the row to switch it on or off.");
                    }
                case ASlider slider:
                    return (slider.SliderTitle.Content?.ToString() ?? "",
                        slider.AdjustNotifier.Content?.ToString() ?? "",
                        true, "Drag the slider, or use - / + to fine-tune.");
                case ADropdown dropdown:
                    {
                        var selected = (dropdown.DropdownBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
                        return (dropdown.DropdownTitle.Content?.ToString() ?? "",
                            selected ?? "Not set", selected != null, "Pick a value from the list.");
                    }
                case AKeyChanger keyChanger:
                    return (keyChanger.KeyChangerTitle.Content?.ToString() ?? "",
                        keyChanger.KeyNotifier.Content?.ToString() ?? "None",
                        true, "Click the row, then press a key or mouse button to rebind.");
                case AColorChanger colorChanger:
                    {
                        var color = (colorChanger.ColorChangingBorder.Background as SolidColorBrush)?.Color;
                        return (colorChanger.ColorChangerTitle.Content?.ToString() ?? "",
                            color?.ToString() ?? "Default", true, "Click the row to pick a colour.");
                    }
                case AFileLocator fileLocator:
                    {
                        var file = Path.GetFileName(fileLocator.FileLocationTextbox.Text);
                        return (fileLocator.DropdownTitle.Content?.ToString() ?? "",
                            string.IsNullOrEmpty(file) ? "No file" : file,
                            !string.IsNullOrEmpty(file), "Use the folder button to choose a file.");
                    }
                case APButton button:
                    return (button.ButtonTitle.Content?.ToString() ?? "", "Action", true, "Click the row to run it.");
                default:
                    return ("", "", false, "");
            }
        }

        private void UpdateItemCounter()
        {
            if (_currentControl == null)
            {
                ItemCounter.Text = "0 / 0";
                return;
            }

            var rows = EnumerateOptionRows(_currentControl).Where(r => r.IsVisible).ToList();
            var index = _selectedRow == null ? 0 : rows.IndexOf(_selectedRow) + 1;
            ItemCounter.Text = $"{index} / {rows.Count}";
            ItemCounter.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        private void StartStatusUpdater()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => UpdateStatusChip();
            _statusTimer.Start();
            UpdateStatusChip();
        }

        private void UpdateStatusChip()
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

            var config = Dictionary.lastLoadedConfig;
            ConfigText.Text = string.IsNullOrEmpty(config) || config == "N/A"
                ? "DEFAULT"
                : Path.GetFileNameWithoutExtension(config).ToUpperInvariant();
            ConfigText.ToolTip = ConfigText.Text;

            var model = Dictionary.lastLoadedModel;
            bool hasModel = !string.IsNullOrEmpty(model) && model != "N/A";
            bool aiming = hasModel && Dictionary.toggleState.TryGetValue("Aim Assist", out var on) && on;

            if (!hasModel)
            {
                StatusText.Text = "NO MODEL";
                StatusDot.Fill = (Brush)FindResource("UI.TextDim");
            }

            var name = Path.GetFileNameWithoutExtension(model).ToUpperInvariant();

            // A model can be selected without its session having started (still loading, or it failed)
            if (!running)
            {
                bool loading = FileManager.CurrentlyLoadingModel;
                StatusText.Text = $"{(loading ? "LOADING" : "LOAD FAILED")}  ·  {name}";
                StatusDot.Fill = loading ? StatusReadyBrush : StatusFailedBrush;
            }

            StatusText.Text = $"{(aiming ? "ACTIVE" : "READY")}  ·  {name}";
            if (aiming)
                StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ThemeColor");
            else
                StatusDot.Fill = StatusReadyBrush;
        }

        #endregion

        #region Toggle Actions

        internal void Toggle_Action(string title)
        {
            var actions = new Dictionary<string, Action>
            {
                ["FOV"] = () =>
                {
                    FOVWindow.Visibility = GetToggleVisibility(title);
                    // Force reposition when showing the window
                    if (Dictionary.toggleState[title])
                    {
                        FOVWindow.ForceReposition();
                    }
                },
                ["Sticky Aim"] = () => UpdateSliderVisibility(uiManager),
                ["Show Detected Player"] = () =>
                {
                    ShowHideDPWindow();
                    DPWindow.DetectedPlayerFocus.Visibility = GetToggleVisibility(title, true);
                    // Force reposition when showing the window
                    if (Dictionary.toggleState[title])
                    {
                        DPWindow.ForceReposition();
                    }
                },
                ["Show AI Confidence"] = () => DPWindow.DetectedPlayerConfidence.Visibility = GetToggleVisibility(title, true),
                ["Mouse Background Effect"] = () => { if (!Dictionary.toggleState[title]) RotaryGradient.Angle = 0; },
                ["UI TopMost"] = () => Topmost = Dictionary.toggleState[title],
                ["StreamGuard"] = () =>
                {
                    StreamGuardManager.ApplyStreamGuardToAllWindows(Dictionary.toggleState[title]);
                },
                ["EMA Smoothening"] = () =>
                {
                    MouseManager.IsEMASmoothingEnabled = Dictionary.toggleState[title];
                },
                ["X Axis Percentage Adjustment"] = () => UpdateSliderVisibility(uiManager),
                ["Y Axis Percentage Adjustment"] = () => UpdateSliderVisibility(uiManager)
            };

            if (actions.TryGetValue(title, out var action))
            {
                action();
            }
        }
        private static void UpdateSliderVisibility(UI uiManager)
        {
            bool useYPercent = Dictionary.toggleState["Y Axis Percentage Adjustment"];
            bool useXPercent = Dictionary.toggleState["X Axis Percentage Adjustment"];
            bool thresholdEnabled = Dictionary.toggleState["Sticky Aim"];

            // Null checks in case AimMenu hasn't been loaded yet
            if (uiManager.S_StickyAimThreshold != null)
                uiManager.S_StickyAimThreshold.Visibility = thresholdEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (uiManager.S_YOffset != null)
                uiManager.S_YOffset.Visibility = useYPercent ? Visibility.Collapsed : Visibility.Visible;
            if (uiManager.S_YOffsetPercent != null)
                uiManager.S_YOffsetPercent.Visibility = useYPercent ? Visibility.Visible : Visibility.Collapsed;

            if (uiManager.S_XOffset != null)
                uiManager.S_XOffset.Visibility = useXPercent ? Visibility.Collapsed : Visibility.Visible;
            if (uiManager.S_XOffsetPercent != null)
                uiManager.S_XOffsetPercent.Visibility = useXPercent ? Visibility.Visible : Visibility.Collapsed;
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
                // Force reposition when showing
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
                var key = dropdown.DropdownTitle.Content?.ToString()
                        ?? throw new NullReferenceException("dropdown.DropdownTitle.Content.ToString() is null");
                Dictionary.dropdownState[key] = title;
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
            var handlers = new Dictionary<string, Action>
            {
                ["Model Switch Keybind"] = HandleModelSwitch,
                ["Dynamic FOV Keybind"] = () => ApplyDynamicFOV(true),
                ["Emergency Stop Keybind"] = HandleEmergencyStop
            };

            handlers.GetValueOrDefault(bindingId)?.Invoke();
        }

        private void HandleKeybindReleased(string bindingId)
        {
            var handlers = new Dictionary<string, Action>
            {
                ["Dynamic FOV Keybind"] = () => ApplyDynamicFOV(false)
            };

            handlers.GetValueOrDefault(bindingId)?.Invoke();
        }

        private void HandleModelSwitch()
        {
            if (!Dictionary.toggleState["Enable Model Switch Keybind"] || FileManager.CurrentlyLoadingModel)
                return;

            if (_menuControls["ModelMenu"] is ModelMenuControl modelMenu)
            {
                var modelListBox = modelMenu.ModelListBoxControl;
                modelListBox.SelectedIndex = (modelListBox.SelectedIndex >= 0 &&
                    modelListBox.SelectedIndex < modelListBox.Items.Count - 1)
                    ? modelListBox.SelectedIndex + 1
                    : 0;
            }
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
        /* Old
        private void ApplyDynamicFOV(bool apply)
        {
            if (!Dictionary.toggleState["Dynamic FOV"]) return;

            var targetSize = apply ? Convert.ToDouble(Dictionary.sliderSettings["Dynamic FOV Size"]) : ActualFOV;
            Dictionary.sliderSettings["FOV Size"] = targetSize;

            AnimateFOVSize(targetSize);
        }
        */
        private void AnimateFOVSize(double targetSize)
        {
            var duration = TimeSpan.FromMilliseconds(500);
            Animator.WidthShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualWidth, targetSize);
            Animator.HeightShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualHeight, targetSize);
            Animator.WidthShift(duration, FOVWindow.RectangleShape, FOVWindow.RectangleShape.ActualWidth, targetSize);
            Animator.HeightShift(duration, FOVWindow.RectangleShape, FOVWindow.RectangleShape.ActualHeight, targetSize);
        }
        /* Old
        private void AnimateFOVSize(double targetSize)
        {
            var duration = TimeSpan.FromMilliseconds(500);
            Animator.WidthShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualWidth, targetSize);
            Animator.HeightShift(duration, FOVWindow.Circle, FOVWindow.Circle.ActualHeight, targetSize);
        }
        */
        private void HandleEmergencyStop()
        {
            bool newState = !Dictionary.toggleState["Aim Assist"];

            Dictionary.toggleState["Aim Assist"] = newState;

            UpdateToggleUI(uiManager.T_AimAligner!, newState);

            new NoticeBar(
                $"[Emergency Stop Keybind] {(newState ? "Enabled" : "Disabled")} all AI features.",
                4000
            ).Show();
        }

        #endregion

        #region UI Effects

        private void Main_Background_Gradient(object sender, MouseEventArgs e)
        {
            if (!Dictionary.toggleState["Mouse Background Effect"]) return;

            var mousePosition = WinAPICaller.GetCursorPosition();
            var translatedMousePos = PointFromScreen(new Point(mousePosition.X, mousePosition.Y));

            var targetAngle = Math.Atan2(
                translatedMousePos.Y - (MainBorder.ActualHeight * 0.5),
                translatedMousePos.X - (MainBorder.ActualWidth * 0.5)) * (180 / Math.PI);

            _currentGradientAngle = CalculateSmoothedAngle(targetAngle);
            RotaryGradient.Angle = _currentGradientAngle;
        }

        private double CalculateSmoothedAngle(double targetAngle)
        {
            const double fullCircle = 360;
            const double halfCircle = 180;
            const double clamp = 1;

            var angleDifference = (targetAngle - _currentGradientAngle + fullCircle) % fullCircle;
            if (angleDifference > halfCircle)
                angleDifference -= fullCircle;

            var clampedDifference = Math.Max(Math.Min(angleDifference, clamp), -clamp);
            return (_currentGradientAngle + clampedDifference + fullCircle) % fullCircle;
        }

        #endregion

        #region Configuration Management

        private void LoadDropdownStates()
        {

            var dropdownConfigs = new[]
            {
                // AimMenu dropdowns
                (uiManager.D_PredictionMethod, "Prediction Method", new Dictionary<string, int>
                {
                    ["Kalman Filter"] = 0,
                    ["Shall0e's Prediction"] = 1,
                    ["wisethef0x's EMA Prediction"] = 2
                }),
                (uiManager.D_DetectionAreaType, "Detection Area Type", new Dictionary<string, int>
                {
                    ["Closest to Center Screen"] = 0,
                    ["Closest to Mouse"] = 1
                }),
                (uiManager.D_AimingBoundariesAlignment, "Aiming Boundaries Alignment", new Dictionary<string, int>
                {
                    ["Center"] = 0,
                    ["Top"] = 1,
                    ["Bottom"] = 2
                }),
                // SettingsMenu dropdowns
                (uiManager.D_MouseMovementMethod, "Mouse Movement Method", new Dictionary<string, int>
                {
                    ["Arduino"] = 0,
                    ["RP2350]"] = 1,
                }),
                (uiManager.D_ScreenCaptureMethod, "Screen Capture Method", new Dictionary<string, int>
                {
                    ["DirectX"] = 0,
                    ["GDI+"] = 1
                }),
                (uiManager.D_ImageSize, "Image Size", new Dictionary<string, int>
                {
                    ["1024"] = 0,  ["992"] = 1,  ["960"] = 2,  ["928"] = 3,
                    ["896"] = 4,   ["864"] = 5,  ["832"] = 6,  ["800"] = 7,
                    ["768"] = 8,   ["736"] = 9,  ["704"] = 10, ["672"] = 11,
                    ["640"] = 12,  ["608"] = 13, ["576"] = 14, ["544"] = 15,
                    ["512"] = 16,  ["480"] = 17, ["448"] = 18, ["416"] = 19,
                    ["384"] = 20,  ["352"] = 21, ["320"] = 22, ["288"] = 23,
                    ["256"] = 24,  ["224"] = 25, ["192"] = 26, ["160"] = 27
                }),
            };

            foreach (var (dropdown, key, mappings) in dropdownConfigs)
            {
                if (dropdown == null)
                {
                    continue;
                }

                if (Dictionary.dropdownState.TryGetValue(key, out var value))
                {
                    var stringValue = value?.ToString() ?? "";

                    if (mappings.TryGetValue(stringValue, out int index))
                    {
                        dropdown.DropdownBox.SelectedIndex = index;
                    }
                    else
                    {
                        LogManager.Log(LogManager.LogLevel.Warning, $"No mapping found for '{stringValue}' in '{key}' dropdown.");
                    }
                }
            }

            // Update slider visibility based on loaded states
            UpdatePredictionSliderVisibility();
            UpdateAimAssistSliderVisibility();
            UpdateAimConfigSliderVisibility();
        }

        private void LoadConfig(string path = "bin\\configs\\Default.cfg", bool loading_from_configlist = false)
        {
            SaveDictionary.LoadJSON(Dictionary.sliderSettings, path);
            SaveDictionary.LoadJSON(Dictionary.dropdownState, path);

            if (!loading_from_configlist || _menuControls["AimMenu"] == null || !_menuInitialized["AimMenu"])
                return;

            try
            {
                ShowSuggestedModelIfSpecified();
                ApplyConfigToSliders();
                ApplyConfigToDropdowns();
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

        private void ApplyConfigToSliders()
        {
            var sliderConfigs = new[]
            {
                ("FOV Size", uiManager.S_FOVSize, 640.0),
                ("Mouse Sensitivity (+/-)", uiManager.S_MouseSensitivity, 0.8),
                ("Mouse Jitter", uiManager.S_MouseJitter, 0.0),
                ("Sticky Aim Threshold", uiManager.S_StickyAimThreshold, 50),
                ("EMA Smoothening", uiManager.S_EMASmoothing, 0.5),
                ("Y Offset (Up/Down)", uiManager.S_YOffset, 0.0),
                ("X Offset (Left/Right)", uiManager.S_XOffset, 0.0),
                ("Y Offset (%)", uiManager.S_YOffsetPercent, 0.0),
                ("X Offset (%)", uiManager.S_XOffsetPercent, 0.0),
                ("Auto Trigger Delay", uiManager.S_AutoTriggerDelay, 0.25),
                ("AI Minimum Confidence", uiManager.S_AIMinimumConfidence, 50.0),
                ("Kalman Lead Time", uiManager.S_KalmanLeadTime, 0.10),
                ("WiseTheFox Lead Time", uiManager.S_WiseTheFoxLeadTime, 0.15),
                ("Shalloe Lead Multiplier", uiManager.S_ShalloeLeadMultiplier, 3.0)
            };

            ApplySliderValues(sliderConfigs, Dictionary.sliderSettings);
        }


        private void ApplyConfigToDropdowns()
        {
            var dropdownConfigs = new[]
            {

                ("Prediction Method", uiManager.D_PredictionMethod, new Dictionary<string, int>
                {
                    ["Kalman Filter"] = 0,
                    ["Shall0e's Prediction"] = 1,
                    ["wisethef0x's EMA Prediction"] = 2
                }),

                ("Detection Area Type", uiManager.D_DetectionAreaType, new Dictionary<string, int>
                {
                    ["Closest to Center Screen"] = 0,
                    ["Closest to Mouse"] = 1
                }),

                ("Aiming Boundaries Alignment", uiManager.D_AimingBoundariesAlignment, new Dictionary<string, int>
                {
                    ["Center"] = 0,
                    ["Top"] = 1,
                    ["Bottom"] = 2
                }),

                ("Mouse Movement Method", uiManager.D_MouseMovementMethod, new Dictionary<string, int>
                {
                    ["Arduino"] = 0,
                    ["RP2350]"] = 1,
                }),

                ("Movement Path", uiManager.D_MovementPath, new Dictionary<string, int>
                {
                    ["Cubic Bezier"] = 0,
                    ["Exponential"] = 1,
                    ["Linear"] = 2,
                    ["Adaptive"] = 3,
                    ["Perlin Noise"] = 4,
                    ["Sine Wave"] = 5,
                    ["Ease In-Out"] = 6,
                    ["Overshoot"] = 7,
                    ["Catmull-Rom"] = 8,
                    ["Smooth Step"] = 9,
                    ["Spiral"] = 10
                }),

                ("Tracer Position", uiManager.D_TracerPosition, new Dictionary<string, int>
                {
                    ["Bottom"] = 0,
                    ["Middle"] = 1,
                    ["Top"] = 2,
                }),

                ("Target Class", uiManager.D_TargetClass, new Dictionary<string, int>
                {
                    ["Best Confidence"] = 0,
                })
            };

            ApplyDropdownValues(dropdownConfigs, Dictionary.dropdownState);

            // Update prediction slider visibility based on selected method
            UpdatePredictionSliderVisibility();
        }

        public void UpdatePredictionSliderVisibility()
        {
            // Hide all prediction sliders first
            if (uiManager.S_KalmanLeadTime != null)
                uiManager.S_KalmanLeadTime.Visibility = Visibility.Collapsed;
            if (uiManager.S_WiseTheFoxLeadTime != null)
                uiManager.S_WiseTheFoxLeadTime.Visibility = Visibility.Collapsed;
            if (uiManager.S_ShalloeLeadMultiplier != null)
                uiManager.S_ShalloeLeadMultiplier.Visibility = Visibility.Collapsed;

            // Don't show sliders if Predictions section is collapsed
            if (Dictionary.minimizeState.TryGetValue("Predictions", out var collapsed) && collapsed == true)
                return;

            // Get selected method from actual dropdown selection
            var selectedItem = uiManager.D_PredictionMethod?.DropdownBox?.SelectedItem as ComboBoxItem;
            string selectedMethod = selectedItem?.Content?.ToString() ?? "";

            // Show only the relevant slider based on selected method
            switch (selectedMethod)
            {
                case "Kalman Filter":
                    if (uiManager.S_KalmanLeadTime != null)
                        uiManager.S_KalmanLeadTime.Visibility = Visibility.Visible;
                    break;
                case "Shall0e's Prediction":
                    if (uiManager.S_ShalloeLeadMultiplier != null)
                        uiManager.S_ShalloeLeadMultiplier.Visibility = Visibility.Visible;
                    break;
                case "wisethef0x's EMA Prediction":
                    if (uiManager.S_WiseTheFoxLeadTime != null)
                        uiManager.S_WiseTheFoxLeadTime.Visibility = Visibility.Visible;
                    break;
            }
        }

        public void UpdateAimAssistSliderVisibility()
        {
            // Don't show sliders if Aim Assist section is collapsed
            if (Dictionary.minimizeState.TryGetValue("Aim Assist", out var collapsed) && collapsed == true)
            {
                if (uiManager.S_StickyAimThreshold != null)
                    uiManager.S_StickyAimThreshold.Visibility = Visibility.Collapsed;
                return;
            }

            // Show Sticky Aim Threshold only if Sticky Aim is enabled
            if (uiManager.S_StickyAimThreshold != null)
            {
                uiManager.S_StickyAimThreshold.Visibility = Dictionary.toggleState["Sticky Aim"]
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void UpdateAimConfigSliderVisibility()
        {
            // Don't show sliders if Aim Config section is collapsed
            if (Dictionary.minimizeState.TryGetValue("Aim Config", out var collapsed) && collapsed == true)
            {
                if (uiManager.S_YOffset != null) uiManager.S_YOffset.Visibility = Visibility.Collapsed;
                if (uiManager.S_YOffsetPercent != null) uiManager.S_YOffsetPercent.Visibility = Visibility.Collapsed;
                if (uiManager.S_XOffset != null) uiManager.S_XOffset.Visibility = Visibility.Collapsed;
                if (uiManager.S_XOffsetPercent != null) uiManager.S_XOffsetPercent.Visibility = Visibility.Collapsed;
                return;
            }

            // Y Axis: Show pixel offset when toggle is OFF, percentage offset when toggle is ON
            bool yPercentEnabled = Dictionary.toggleState["Y Axis Percentage Adjustment"];
            if (uiManager.S_YOffset != null)
                uiManager.S_YOffset.Visibility = yPercentEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (uiManager.S_YOffsetPercent != null)
                uiManager.S_YOffsetPercent.Visibility = yPercentEnabled ? Visibility.Visible : Visibility.Collapsed;

            // X Axis: Show pixel offset when toggle is OFF, percentage offset when toggle is ON
            bool xPercentEnabled = Dictionary.toggleState["X Axis Percentage Adjustment"];
            if (uiManager.S_XOffset != null)
                uiManager.S_XOffset.Visibility = xPercentEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (uiManager.S_XOffsetPercent != null)
                uiManager.S_XOffsetPercent.Visibility = xPercentEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplySliderValues((string key, ASlider? slider, double defaultValue)[] configs, Dictionary<string, dynamic> source)
        {
            foreach (var (key, slider, defaultValue) in configs)
            {
                if (slider != null && source.TryGetValue(key, out var value))
                {
                    slider.Slider.Value = Convert.ToDouble(value);
                }
                else if (slider != null)
                {
                    slider.Slider.Value = defaultValue;
                }
            }
        }

        private void ApplyDropdownValues((string key, ADropdown? dropdown, Dictionary<string, int> mappings)[] configs, Dictionary<string, dynamic> source)
        {
            foreach (var (key, dropdown, mappings) in configs)
            {
                if (dropdown != null && source.TryGetValue(key, out var value))
                {
                    var stringValue = value?.ToString() ?? "";
                    if (mappings.TryGetValue(stringValue, out int index))
                    {
                        dropdown.DropdownBox.SelectedIndex = index;
                    }
                    else
                    {
                        LogManager.Log(LogManager.LogLevel.Warning, $"No mapping found for '{stringValue}' in '{key}' dropdown.");
                    }
                }
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

        #region Unimplemented Methods (For Controls)

        public AToggle AddToggle(StackPanel panel, string title) =>
            throw new NotImplementedException("Use control's internal implementation");

        public AKeyChanger AddKeyChanger(StackPanel panel, string title, string keybind) =>
            throw new NotImplementedException("Use control's internal implementation");

        public AColorChanger AddColorChanger(StackPanel panel, string title) =>
            throw new NotImplementedException("Use control's internal implementation");

        public ASlider AddSlider(StackPanel panel, string title, string label, double frequency, double buttonsteps, double min, double max) =>
            throw new NotImplementedException("Use control's internal implementation");

        public ADropdown AddDropdown(StackPanel panel, string title) =>
            throw new NotImplementedException("Use control's internal implementation");

        public AFileLocator AddFileLocator(StackPanel panel, string title, string filter = "All files (*.*)|*.*", string DLExtension = "") =>
            throw new NotImplementedException("Use control's internal implementation");

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