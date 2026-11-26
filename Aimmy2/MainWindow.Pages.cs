using Whales2.AILogic;
using Whales2.Class;
using Whales2.Controls;
using Whales2.Other;
using Whales2.UILibrary;
using Class;
using InputLogic;
using Other;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using UILibrary;
using Visuality;
using LogLevel = Other.LogManager.LogLevel;
using Path = System.IO.Path;

namespace Whales2
{
    /// <summary>
    /// Sidebar layout: builds the Home, Aim, Visuals, Models, Device and Settings pages in code.
    /// Every setting keeps its original Dictionary key, so saved settings and configs still load.
    /// </summary>
    public partial class MainWindow
    {
        #region Page state

        private ModelMenuControl _modelMenu = null!;
        private AboutMenuControl? _aboutMenu;

        private readonly Dictionary<string, FrameworkElement> _pages = new();
        private static readonly string[] PageOrder = { "Home", "Aim", "Visuals", "Models", "Device", "Settings" };
        private static readonly Dictionary<string, string> PageSubtitles = new()
        {
            ["Home"] = "Status and quick setup",
            ["Aim"] = "When and how the crosshair moves",
            ["Visuals"] = "What gets drawn on your screen",
            ["Models"] = "Detection models, configs and detection settings",
            ["Device"] = "Mouse hardware and screen capture",
            ["Settings"] = "Hotkeys, appearance and app options",
            ["Sketch builder"] = "Record your mouse, then generate a sketch for your Arduino"
        };
        // Plainer wording used by the Simple layout
        private static readonly Dictionary<string, string> SimplePageSubtitles = new()
        {
            ["Home"] = "Start here: three steps and you're aiming",
            ["Aim"] = "Turn aim on, pick your aim key and how fast it moves",
            ["Visuals"] = "What Whales draws on your screen",
            ["Models"] = "Pick the AI model that finds players",
            ["Device"] = "Connect the Arduino, Arduino (Raw HID) or RP2350 that moves your mouse",
            ["Settings"] = "Keys, colour and app options",
            ["Sketch builder"] = "Make the Arduino sketch that moves your mouse"
        };

        // Rows (by setting key) that the Simple layout hides: fine-tuning most people never touch
        private static readonly HashSet<string> SimpleHiddenKeys = new()
        {
            "EMA Smoothening", "Movement Preview",
            "Prediction Method", "Kalman Lead Time", "WiseTheFox Lead Time", "Shalloe Lead Multiplier",
            "Dynamic FOV", "Dynamic FOV Keybind", "Dynamic FOV Size", "Tracer Position",
            "Screen Capture Method", "StreamGuard", "Refresh displays",
            "Second Aim Keybind", "Model Switch Keybind"
        };

        // Two-column page bodies; the Simple layout stacks them into one column
        private readonly List<(Grid Grid, StackPanel Left, StackPanel Right)> _columnGrids = new();
        private readonly List<FrameworkElement> _pageBodies = new();
        private const double SimpleLayoutWidth = 760;

        private string _currentPage = "";
        private bool _pagesBuilt;
        private bool _suppressDropdownSideEffects;
        private string? _pendingTargetClass;

        // Rows that only show when a condition holds (advanced mode, a parent switch, a chosen method...)
        private readonly List<(FrameworkElement Element, Func<bool> IsVisible)> _conditionalRows = new();
        private readonly Dictionary<string, List<AKeyChanger>> _keyChangers = new();
        private readonly Dictionary<string, ASlider> _sliders = new();
        private readonly Dictionary<string, ADropdown> _dropdowns = new();

        private PillSwitch? _sidebarAimSwitch;
        private PillSwitch? _advancedSwitch;
        private PillSwitch? _homeAimSwitch;

        // Home page live elements
        private TextBlock? _homeAimTitle, _homeAimSub, _homeFps, _homeEngine, _homeDevice, _homeDeviceSub, _homeConfig, _homeModel;
        private readonly List<(Border Badge, TextBlock Mark, int Number, Func<bool> Done)> _setupSteps = new();

        // Device page live elements
        private StackPanel? _devicePortList;
        private TextBlock? _hidStatusText;
        private TextBlock? _arduinoHidStatusText;
        private string _portListSignature = "";

        private static readonly string[] MovementPaths = PathShapes.Names;

        // Every multiple of 32 from 1024 down to 160 (YOLO stride)
        private static readonly string[] ImageSizes = Enumerable.Range(0, 28).Select(i => (1024 - i * 32).ToString()).ToArray();

        private static bool IsOn(string key) =>
            Dictionary.toggleState.TryGetValue(key, out var value) && value is bool on && on;

        private static bool SimpleLayout => IsOn("Simple Layout");

        // The Simple layout never shows advanced rows
        private static bool ShowAdvanced => IsOn("Show Advanced Settings") && !SimpleLayout;

        private T Res<T>(string key) => (T)FindResource(key);

        private static string? SelectedText(ADropdown? dropdown) =>
            (dropdown?.DropdownBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

        private string SelectedMovementMethod =>
            SelectedText(uiManager.D_MouseMovementMethod)
            ?? Dictionary.dropdownState["Mouse Movement Method"]?.ToString()
            ?? "";

        private bool UsesRp2350       => SelectedMovementMethod == "RP2350";
        private bool UsesArduinoHid   => SelectedMovementMethod == "Arduino (Raw HID)";
        private bool UsesArduinoSerial => !UsesRp2350 && !UsesArduinoHid;

        private static bool HasModel =>
            !string.IsNullOrEmpty(Dictionary.lastLoadedModel) && Dictionary.lastLoadedModel != "N/A";

        private bool DeviceConnected =>
            UsesRp2350     ? MouseManager.HidDevice?.IsConnected == true :
            UsesArduinoHid ? MouseManager.ArduinoHid?.IsConnected == true :
                             connectedPort != null;

        private static string KeyName(string bindingKey) =>
            KeybindNameManager.ConvertToRegularKey(Dictionary.bindingSettings[bindingKey]?.ToString() ?? "");

        #endregion

        #region Build and navigation

        private void BuildPages()
        {
            // Adding items can auto-select the first one and overwrite the saved value, so keep a copy
            var savedDropdowns = new Dictionary<string, dynamic>(Dictionary.dropdownState);

            _suppressDropdownSideEffects = true;
            try
            {
                BuildShellSwitches();

                // Home mirrors sliders from the other pages, so it is built last
                _pages["Aim"] = BuildAimPage();
                _pages["Visuals"] = BuildVisualsPage();
                _pages["Models"] = BuildModelsPage();
                _pages["Device"] = BuildDevicePage();
                _pages["Settings"] = BuildSettingsPage();
                _pages[SketchPage] = BuildSketchBuilderPage();
                _pages["Home"] = BuildHomePage();

                RestoreDropdowns(savedDropdowns);
            }
            finally
            {
                _suppressDropdownSideEffects = false;
            }

            AIManager.ImageSizeUpdated += OnImageSizeChanged;
            AIManager.ClassesUpdated += OnClassesChanged;
            AIManager.DynamicModelStatusChanged += OnDynamicModelStatusChanged;

            UpdateDynamicModelDropdownsVisibility(AIManager.CurrentModelIsDynamic);
            if (uiManager.D_TargetClass != null)
                UpdateTargetClassDropdown(uiManager.D_TargetClass, null);

            _pagesBuilt = true;
            RefreshConditionalRows();
            UpdateKeyHints();
        }

        private void BuildShellSwitches()
        {
            _sidebarAimSwitch = new PillSwitch(44, 24, ToggleAimAssist, "Turn aim assist on or off");
            SidebarSwitchHost.Child = _sidebarAimSwitch.Root;

            _advancedSwitch = new PillSwitch(36, 20, () =>
            {
                Dictionary.toggleState["Show Advanced Settings"] = !ShowAdvanced;
                _advancedSwitch!.Set(ShowAdvanced);
                RefreshConditionalRows();
            });
            _advancedSwitch.Set(ShowAdvanced);
            AdvancedSwitchHost.Child = _advancedSwitch.Root;
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_pagesBuilt && sender is RadioButton { Tag: string page })
                ShowPage(page);
        }

        internal void ShowPage(string name, bool animate = true)
        {
            if (!_pages.TryGetValue(name, out var page) || name == _currentPage) return;

            _currentPage = name;
            ContentArea.Children.Clear();
            ContentArea.Children.Add(page);

            PageTitle.Text = name;
            PageSubtitle.Text = (SimpleLayout ? SimplePageSubtitles : PageSubtitles).GetValueOrDefault(name, "");

            foreach (var nav in NavPanel.Children.OfType<RadioButton>())
            {
                if ((string)nav.Tag == name && nav.IsChecked != true)
                    nav.IsChecked = true;
            }

            if (animate)
            {
                page.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }

            if (name == "Home") UpdateStatus();
        }

        private void CyclePage(int direction)
        {
            var index = Array.IndexOf(PageOrder, _currentPage);
            ShowPage(PageOrder[(index + direction + PageOrder.Length) % PageOrder.Length]);
        }

        /// <summary>Shows or hides every conditional row (advanced rows, sub-options of a switch, ...).</summary>
        internal void RefreshConditionalRows()
        {
            ApplyLayoutMode();

            foreach (var (element, isVisible) in _conditionalRows)
            {
                bool visible;
                try { visible = isVisible(); }
                catch { visible = true; }
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            // Toggles, config loads and the advanced switch all pass through here
            UpdateVisualPreview();
            SimulateMovementPath();
        }

        /// <summary>Applies the Simple or Full layout: column count, page width, header chips and page subtitle.</summary>
        private void ApplyLayoutMode()
        {
            bool simple = SimpleLayout;

            foreach (var (grid, left, right) in _columnGrids)
            {
                grid.ColumnDefinitions[1].Width = simple ? new GridLength(0) : new GridLength(16);
                grid.ColumnDefinitions[2].Width = simple ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(right, simple ? 0 : 2);
                Grid.SetRow(right, simple ? 1 : 0);
            }

            foreach (var body in _pageBodies)
                body.MaxWidth = simple ? SimpleLayoutWidth : double.PositiveInfinity;

            var chipVisibility = simple ? Visibility.Collapsed : Visibility.Visible;
            AdvancedChip.Visibility = chipVisibility;
            FpsChip.Visibility = chipVisibility;
            EngineChip.Visibility = chipVisibility;

            if (!string.IsNullOrEmpty(_currentPage))
                PageSubtitle.Text = (simple ? SimplePageSubtitles : PageSubtitles).GetValueOrDefault(_currentPage, "");

            // After switching layouts, start every page from the top
            if (_appliedSimpleLayout != null && _appliedSimpleLayout != simple)
            {
                foreach (var page in _pages.Values.OfType<ScrollViewer>())
                    page.ScrollToTop();
            }
            _appliedSimpleLayout = simple;
        }

        private bool? _appliedSimpleLayout;

        /// <summary>Switches between the Simple and Full layout through its Settings row, so both stay in sync.</summary>
        private void ToggleSimpleLayout()
        {
            if (toggleInstances.TryGetValue("Simple Layout", out var toggle))
                toggle.Reader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        /// <summary>Hides an element (a whole card, a row) while the Simple layout is on.</summary>
        private T HideInSimple<T>(T element) where T : FrameworkElement
        {
            _conditionalRows.Add((element, () => !SimpleLayout));
            return element;
        }

        private void UpdateKeyHints()
        {
            SidebarAimKey.Text = KeyName("Aim Keybind");
            SidebarStopKey.Text = KeyName("Emergency Stop Keybind");
        }

        /// <summary>Flips aim assist through its row, so the "load a model first" check still runs.</summary>
        private void ToggleAimAssist()
        {
            uiManager.T_AimAligner?.Reader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        private void DisposePages()
        {
            AIManager.ImageSizeUpdated -= OnImageSizeChanged;
            AIManager.ClassesUpdated -= OnClassesChanged;
            AIManager.DynamicModelStatusChanged -= OnDynamicModelStatusChanged;
            uiManager.DisplaySelector?.Dispose();
            DisposeSketchBuilder();
        }

        #endregion

        #region Layout helpers

        private ScrollViewer MakeScrollPage(UIElement content)
        {
            // Page bodies are narrowed (and centred) in the Simple layout
            if (content is FrameworkElement body)
                _pageBodies.Add(body);

            return new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                Focusable = false
            };
        }

        private Grid MakeColumns(out StackPanel left, out StackPanel right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            left = new StackPanel();
            right = new StackPanel();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            _columnGrids.Add((grid, left, right));
            return grid;
        }

        private static readonly Thickness PageMargin = new(24, 20, 24, 8);

        private Button MakeButton(string text, Action onClick, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                Style = Res<Style>(primary ? "UI.PrimaryButton" : "UI.GhostButton")
            };
            button.Click += (_, _) => onClick();
            return button;
        }

        private TextBlock MakeSmall(string text, double size = 12) => new()
        {
            Text = text,
            Style = Res<Style>("UI.Small"),
            FontSize = size
        };

        /// <summary>A card with a title, an optional subtitle and a list of option rows.</summary>
        private sealed class CardBuilder
        {
            private readonly MainWindow _w;
            public Border Root { get; }
            public StackPanel Body { get; }

            public CardBuilder(MainWindow w, string title, string? subtitle = null)
            {
                _w = w;
                var header = new StackPanel { Margin = new Thickness(18, 16, 18, 6) };
                header.Children.Add(new TextBlock { Text = title, Style = w.Res<Style>("UI.CardTitle") });
                if (!string.IsNullOrEmpty(subtitle))
                    header.Children.Add(new TextBlock { Text = subtitle, Style = w.Res<Style>("UI.CardSubtitle") });

                Body = new StackPanel { Margin = new Thickness(6, 0, 6, 10) };

                var stack = new StackPanel();
                stack.Children.Add(header);
                stack.Children.Add(Body);
                Root = new Border { Style = w.Res<Style>("UI.Card"), Child = stack };
            }

            public CardBuilder AddToggle(string key, string label, string? desc = null,
                Action<AToggle>? configure = null, Func<bool>? when = null)
            {
                var toggle = _w.CreateToggle(key, label, desc);
                configure?.Invoke(toggle);
                return Add(toggle, when, key);
            }

            public CardBuilder AddSlider(string key, string label, string unit, double frequency, double step,
                double min, double max, string? desc = null, Action<ASlider>? configure = null, Func<bool>? when = null)
            {
                var slider = _w.CreateSlider(key, label, unit, frequency, step, min, max, desc);
                configure?.Invoke(slider);
                return Add(slider, when, key);
            }

            public CardBuilder AddDropdown(string key, string label, string? desc, IEnumerable<string> items,
                Action<ADropdown>? configure = null, Func<bool>? when = null)
            {
                var dropdown = _w.CreateDropdown(key, label, desc, items);
                configure?.Invoke(dropdown);
                return Add(dropdown, when, key);
            }

            public CardBuilder AddKey(string key, string label, string? desc = null, Func<bool>? when = null) =>
                Add(_w.CreateKeyChanger(key, label, desc), when, key);

            public CardBuilder AddColor(string key, string label, string? desc, Action<Color> apply, Func<bool>? when = null) =>
                Add(_w.CreateColorChanger(key, label, desc, apply), when, key);

            public CardBuilder AddAction(string label, string? desc, Action onClick, Func<bool>? when = null)
            {
                var button = new APButton(label, desc);
                button.Reader.Click += (_, _) => onClick();
                return Add(button, when, label);
            }

            /// <param name="simpleKey">Name to look up in SimpleHiddenKeys, for elements the Simple layout hides.</param>
            public CardBuilder AddElement(FrameworkElement element, Func<bool>? when = null, string? simpleKey = null) =>
                Add(element, when, simpleKey);

            private CardBuilder Add(FrameworkElement element, Func<bool>? when, string? key = null)
            {
                Body.Children.Add(element);

                if (key != null && SimpleHiddenKeys.Contains(key))
                {
                    var inner = when;
                    when = () => !SimpleLayout && (inner?.Invoke() ?? true);
                }
                if (when != null)
                    _w._conditionalRows.Add((element, when));
                return this;
            }
        }

        /// <summary>Rounded on/off switch used outside option rows (sidebar, header, Home).</summary>
        private sealed class PillSwitch
        {
            private static readonly Brush OffTrack = FrozenBrush(0x2A, 0x34, 0x3A);
            private static readonly Brush OffKnob = FrozenBrush(0xC3, 0xCD, 0xD3);
            private static readonly Brush OnKnob = FrozenBrush(0x0B, 0x0E, 0x10);

            private readonly Border _knob;
            private readonly TranslateTransform _offset = new();
            private readonly double _travel;
            private bool? _state;

            public Border Root { get; }

            public PillSwitch(double width, double height, Action onClick, string? toolTip = null)
            {
                double knob = height - 6;
                _knob = new Border
                {
                    Width = knob,
                    Height = knob,
                    CornerRadius = new CornerRadius(knob / 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = OffKnob,
                    RenderTransform = _offset
                };
                Root = new Border
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new CornerRadius(height / 2),
                    Padding = new Thickness(3),
                    Background = OffTrack,
                    Cursor = Cursors.Hand,
                    Child = _knob,
                    ToolTip = toolTip
                };
                _travel = width - height;
                Root.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    onClick();
                };
            }

            public void Set(bool on)
            {
                if (_state == on) return;
                bool animate = _state != null;
                _state = on;

                if (on)
                    Root.SetResourceReference(Border.BackgroundProperty, "ThemeColor");
                else
                    Root.Background = OffTrack;
                _knob.Background = on ? OnKnob : OffKnob;

                double target = on ? _travel : 0;
                if (animate)
                    _offset.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                else
                    _offset.X = target;
            }

            private static Brush FrozenBrush(byte r, byte g, byte b)
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
        }

        #endregion

        #region Option row factories (same behaviour as the old menus)

        private AToggle CreateToggle(string key, string label, string? desc)
        {
            var toggle = new AToggle(label, desc);
            toggleInstances[key] = toggle;

            if (Dictionary.toggleState[key])
                toggle.EnableSwitch();
            else
                toggle.DisableSwitch();

            toggle.Reader.Click += (_, _) =>
            {
                Dictionary.toggleState[key] = !Dictionary.toggleState[key];
                UpdateToggleUI(toggle, Dictionary.toggleState[key]);
                Toggle_Action(key);

                // Other click handlers (e.g. "load a model first") may still change the state
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshConditionalRows();
                    UpdateStatus();
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            return toggle;
        }

        private ASlider CreateSlider(string key, string label, string unit, double frequency, double step,
            double min, double max, string? desc)
        {
            var slider = new ASlider(label, unit, step, desc)
            {
                Slider = { Minimum = min, Maximum = max, TickFrequency = frequency }
            };

            slider.Slider.Value = Dictionary.sliderSettings.TryGetValue(key, out var value) ? Convert.ToDouble(value) : min;
            slider.RefreshValueText();
            slider.Slider.ValueChanged += (_, _) => Dictionary.sliderSettings[key] = slider.Slider.Value;

            _sliders[key] = slider;
            return slider;
        }

        private ADropdown CreateDropdown(string key, string label, string? desc, IEnumerable<string> items)
        {
            var dropdown = new ADropdown(label, key, desc);
            foreach (var item in items)
                AddDropdownItem(dropdown, item);

            _dropdowns[key] = dropdown;
            return dropdown;
        }

        private AKeyChanger CreateKeyChanger(string key, string label, string? desc)
        {
            var keyChanger = new AKeyChanger(label, Dictionary.bindingSettings[key]?.ToString() ?? "", desc);

            // The same key can appear on two pages (e.g. Aim and Settings); keep them in sync
            if (!_keyChangers.TryGetValue(key, out var group))
                _keyChangers[key] = group = new List<AKeyChanger>();
            group.Add(keyChanger);

            keyChanger.Reader.Click += (_, _) =>
            {
                keyChanger.KeyNotifier.Content = "...";
                bindingManager.StartListeningForBinding(key);

                Action<string, string>? handler = null;
                handler = (bindingId, newKey) =>
                {
                    if (bindingId != key) return;
                    bindingManager.OnBindingSet -= handler;
                    Dictionary.bindingSettings[bindingId] = newKey;

                    Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var changer in _keyChangers[key])
                            changer.KeyNotifier.Content = KeybindNameManager.ConvertToRegularKey(newKey);
                        UpdateKeyHints();
                        UpdateStatus();
                    });
                };
                bindingManager.OnBindingSet += handler;
            };

            return keyChanger;
        }

        private AColorChanger CreateColorChanger(string key, string label, string? desc, Action<Color> apply)
        {
            var colorChanger = new AColorChanger(label, desc);
            colorChanger.ColorChangingBorder.Background =
                (Brush)new BrushConverter().ConvertFromString(Dictionary.colorState[key].ToString())!;

            global::UISections.ColorPicker? picker = null;
            colorChanger.Reader.Click += (_, _) =>
            {
                if (picker != null && picker.IsVisible)
                {
                    picker.Activate();
                    return;
                }

                var initial = (colorChanger.ColorChangingBorder.Background as SolidColorBrush)?.Color ?? Colors.White;
                picker = new global::UISections.ColorPicker(initial, label);
                picker.ColorChanged += color =>
                {
                    colorChanger.ColorChangingBorder.Background = new SolidColorBrush(color);
                    Dictionary.colorState[key] = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
                    apply(color);
                };
                picker.Closed += (_, _) => picker = null;
                picker.Show();
            };

            return colorChanger;
        }

        /// <summary>A second slider bound two-way to an existing one (used for Home's quick tune).</summary>
        private FrameworkElement MirrorSlider(ASlider? primary, string label, string desc)
        {
            if (primary == null) return new Border();

            var mirror = new ASlider(label, primary.Unit, primary.Step, desc);
            mirror.Slider.TickFrequency = primary.Slider.TickFrequency;
            mirror.Slider.SetBinding(RangeBase.MinimumProperty, new Binding(nameof(RangeBase.Minimum)) { Source = primary.Slider });
            mirror.Slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(RangeBase.Maximum)) { Source = primary.Slider });
            mirror.Slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(RangeBase.Value)) { Source = primary.Slider, Mode = BindingMode.TwoWay });
            mirror.RefreshValueText();
            return mirror;
        }

        private static bool SelectDropdownValue(ADropdown dropdown, string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            for (int i = 0; i < dropdown.DropdownBox.Items.Count; i++)
            {
                if ((dropdown.DropdownBox.Items[i] as ComboBoxItem)?.Content?.ToString() == value)
                {
                    dropdown.DropdownBox.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Selects each dropdown's saved value without triggering reloads or overlay refreshes.</summary>
        private void RestoreDropdowns(IReadOnlyDictionary<string, dynamic>? source = null)
        {
            source ??= Dictionary.dropdownState;
            bool wasSuppressed = _suppressDropdownSideEffects;
            _suppressDropdownSideEffects = true;
            try
            {
                foreach (var (key, dropdown) in _dropdowns)
                {
                    string? value = source.TryGetValue(key, out var saved) ? saved?.ToString() : null;
                    if (SelectDropdownValue(dropdown, value)) continue;

                    // The class list fills in after a model loads; remember the saved choice until then
                    if (key == "Target Class" && !string.IsNullOrEmpty(value) && value != "Best Confidence")
                        _pendingTargetClass = value;

                    if (dropdown.DropdownBox.SelectedIndex < 0 && dropdown.DropdownBox.Items.Count > 0)
                        dropdown.DropdownBox.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressDropdownSideEffects = wasSuppressed;
            }
        }

        #endregion

        #region Home

        private FrameworkElement BuildHomePage()
        {
            var root = new StackPanel { Margin = PageMargin };

            // Aim status with a big switch
            var heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _homeAimTitle = new TextBlock { Style = Res<Style>("UI.PageTitle"), FontSize = 22, Text = "Aim assist is off" };
            _homeAimSub = MakeSmall("", 13);
            _homeAimSub.Margin = new Thickness(0, 4, 0, 0);
            heroText.Children.Add(_homeAimTitle);
            heroText.Children.Add(_homeAimSub);
            heroGrid.Children.Add(heroText);
            _homeAimSwitch = new PillSwitch(56, 30, ToggleAimAssist, "Turn aim assist on or off");
            _homeAimSwitch.Root.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_homeAimSwitch.Root, 1);
            heroGrid.Children.Add(_homeAimSwitch.Root);
            root.Children.Add(new Border { Style = Res<Style>("UI.Card"), Padding = new Thickness(22, 18, 22, 18), Child = heroGrid });

            // Live tiles
            var tiles = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            for (int i = 0; i < 5; i++)
                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(16) : new GridLength(1, GridUnitType.Star) });
            AddTile(tiles, 0, "Detection speed", out _homeFps, out _, "frames per second");
            AddTile(tiles, 2, "Engine", out _homeEngine, out _, "runs the model");
            AddTile(tiles, 4, "Mouse device", out _homeDevice, out _homeDeviceSub, "");
            root.Children.Add(HideInSimple(tiles));

            var columns = MakeColumns(out var left, out var right);

            var setup = new CardBuilder(this, "Get set up", "Three steps before aim assist can move your mouse");
            setup.AddElement(MakeSetupStep(1, "Load a model", "Pick a detection model on the Models page", () => HasModel, "Models"));
            setup.AddElement(MakeSetupStep(2, "Connect your mouse device", "Arduino, Arduino (Raw HID) or RP2350, on the Device page", () => DeviceConnected, "Device"));
            setup.AddElement(MakeSetupStep(3, "Turn on aim assist", "Use the switch above, then hold your aim key", () => IsOn("Aim Assist"), null));
            left.Children.Add(setup.Root);

            // Current profile
            var profile = new CardBuilder(this, "Current setup", "What is loaded right now");
            var profileGrid = new Grid { Margin = new Thickness(14, 4, 14, 6) };
            profileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            profileGrid.ColumnDefinitions.Add(new ColumnDefinition());
            profileGrid.RowDefinitions.Add(new RowDefinition());
            profileGrid.RowDefinitions.Add(new RowDefinition());
            AddProfileLine(profileGrid, 0, "Config", out _homeConfig);
            AddProfileLine(profileGrid, 1, "Model", out _homeModel);
            profile.AddElement(profileGrid);
            var profileButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 8, 14, 6) };
            profileButtons.Children.Add(MakeButton("Save config", () => new ConfigSaver().ShowDialog(), primary: true));
            var modelsButton = MakeButton("Change model or config", () => ShowPage("Models"));
            modelsButton.Margin = new Thickness(8, 0, 0, 0);
            profileButtons.Children.Add(modelsButton);
            profile.AddElement(profileButtons);
            left.Children.Add(profile.Root);

            var tune = new CardBuilder(this, "Quick tune", "The settings people change most. Same values as on the other pages.");
            tune.AddElement(MirrorSlider(uiManager.S_MouseSensitivity, "Sensitivity", "Lower is faster and snappier, higher is slower and smoother"));
            tune.AddElement(MirrorSlider(uiManager.S_FOVSize, "FOV size", "Area searched for targets around your crosshair"));
            tune.AddElement(MirrorSlider(uiManager.S_AIMinimumConfidence, "Minimum confidence", "How sure the model must be before it aims"));
            right.Children.Add(tune.Root);

            root.Children.Add(columns);

            // One-click switch between the layouts, worded for whichever one is showing
            var toSimple = MakeLayoutHint("Too many options? The simple layout shows only the main ones, in one column.", "Switch to simple layout");
            _conditionalRows.Add((toSimple, () => !SimpleLayout));
            root.Children.Add(toSimple);
            var toFull = MakeLayoutHint("Want every option? The full layout shows all settings.", "Show all options");
            _conditionalRows.Add((toFull, () => SimpleLayout));
            root.Children.Add(toFull);

            return MakeScrollPage(root);
        }

        private FrameworkElement MakeLayoutHint(string text, string buttonText)
        {
            var grid = new Grid { Margin = new Thickness(6, 2, 6, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = MakeSmall(text, 12.5);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;
            label.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(label);

            var button = MakeButton(buttonText, ToggleSimpleLayout);
            button.Height = 30;
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            return grid;
        }

        private void AddTile(Grid host, int column, string label, out TextBlock value, out TextBlock sub, string subText)
        {
            var stack = new StackPanel();
            stack.Children.Add(MakeSmall(label));
            value = new TextBlock
            {
                Style = Res<Style>("UI.PageTitle"),
                FontSize = 24,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "—"
            };
            stack.Children.Add(value);
            sub = MakeSmall(subText);
            sub.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(sub);

            var tile = new Border { Style = Res<Style>("UI.Card"), Padding = new Thickness(18, 14, 18, 14), Child = stack };
            Grid.SetColumn(tile, column);
            host.Children.Add(tile);
        }

        private void AddProfileLine(Grid grid, int row, string label, out TextBlock value)
        {
            var name = MakeSmall(label, 13);
            name.Margin = new Thickness(0, 4, 18, 4);
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            value = new TextBlock
            {
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("UI.Text"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "—"
            };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        private FrameworkElement MakeSetupStep(int number, string title, string desc, Func<bool> done, string? page)
        {
            var grid = new Grid { Margin = new Thickness(12, 6, 12, 6), MinHeight = 44 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new TextBlock
            {
                Text = number.ToString(),
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res<Brush>("UI.TextSoft")
            };
            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Res<Brush>("UI.KeyBg"),
                BorderBrush = Res<Brush>("UI.Box"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = mark
            };
            grid.Children.Add(badge);

            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = Res<Brush>("UI.Text")
            });
            var descText = MakeSmall(desc);
            descText.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(descText);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (page != null)
            {
                var open = MakeButton("Open", () => ShowPage(page));
                open.Height = 30;
                open.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(open, 2);
                grid.Children.Add(open);
            }

            _setupSteps.Add((badge, mark, number, done));
            return grid;
        }

        private void UpdateHomePage(double fps, bool running, bool hasModel, string modelName, bool aimOn)
        {
            if (_homeAimTitle == null || _homeAimSub == null) return;

            string aimKey = KeyName("Aim Keybind");
            _homeAimTitle.Text = aimOn ? "Aim assist is on" : "Aim assist is off";
            _homeAimSub.Text = !hasModel
                ? "Load a model first, then switch it on."
                : aimOn
                    ? (IsOn("Constant AI Tracking") ? $"Tracking targets all the time with {modelName}" : $"Hold {aimKey} to aim  ·  {modelName}")
                    : $"Switch it on, then hold {aimKey} to aim  ·  {modelName}";
            _homeAimSwitch?.Set(aimOn);

            if (_homeFps != null) _homeFps.Text = fps > 0 ? Math.Round(fps).ToString() : "0";
            if (_homeEngine != null) _homeEngine.Text = running ? AIManager.ExecutionProvider : "Idle";

            if (_homeDevice != null && _homeDeviceSub != null)
            {
                if (UsesRp2350)
                {
                    _homeDevice.Text = MouseManager.HidDevice != null ? "RP2350" : "Not found";
                    _homeDeviceSub.Text = "RP2350 HID mouse";
                }
                else if (UsesArduinoHid)
                {
                    bool rawHidConnected = MouseManager.ArduinoHid?.IsConnected == true;
                    _homeDevice.Text = rawHidConnected ? "Raw HID" : "Not found";
                    _homeDeviceSub.Text = rawHidConnected
                        ? "Arduino Leonardo  ·  Raw HID"
                        : "Arduino Leonardo  ·  flash mouse_bridge_rawhid.ino";
                }
                else
                {
                    _homeDevice.Text = connectedPort ?? "Not connected";
                    _homeDeviceSub.Text = connectedPort != null ? "Arduino" : "Arduino  ·  pick a COM port";
                }
            }

            if (_homeConfig != null)
            {
                var config = Dictionary.lastLoadedConfig;
                _homeConfig.Text = string.IsNullOrEmpty(config) || config == "N/A" ? "Default" : Path.GetFileNameWithoutExtension(config);
            }
            if (_homeModel != null) _homeModel.Text = hasModel ? modelName : "None loaded";

            foreach (var (badge, mark, number, done) in _setupSteps)
            {
                bool complete;
                try { complete = done(); } catch { complete = false; }

                if (complete)
                {
                    badge.SetResourceReference(Border.BackgroundProperty, "ThemeColor");
                    badge.BorderThickness = new Thickness(0);
                    mark.FontFamily = new FontFamily("Segoe MDL2 Assets");
                    mark.Text = "";
                    mark.Foreground = Res<Brush>("UI.Bg");
                }
                else
                {
                    badge.Background = Res<Brush>("UI.KeyBg");
                    badge.BorderThickness = new Thickness(1);
                    mark.FontFamily = Res<FontFamily>("UIFont");
                    mark.Text = number.ToString();
                    mark.Foreground = Res<Brush>("UI.TextSoft");
                }
            }
        }

        #endregion

        #region Aim

        private FrameworkElement BuildAimPage()
        {
            var columns = MakeColumns(out var left, out var right);
            Func<bool> advanced = () => ShowAdvanced;

            var activation = new CardBuilder(this, "Activation", "When aim assist is allowed to move your mouse")
                .AddToggle("Aim Assist", "Aim assist", "Master switch. Load a model first.", t =>
                {
                    uiManager.T_AimAligner = t;
                    t.Reader.Click += (_, _) =>
                    {
                        if (Dictionary.toggleState["Aim Assist"] && Dictionary.lastLoadedModel == "N/A")
                        {
                            Dictionary.toggleState["Aim Assist"] = false;
                            UpdateToggleUI(t, false);
                            LogManager.Log(LogLevel.Warning, "Please load a model first", true);
                        }
                    };
                })
                .AddToggle("Constant AI Tracking", "Always track", "Aim without holding a key", t =>
                {
                    uiManager.T_ConstantAITracking = t;
                    t.Reader.Click += (_, _) =>
                    {
                        if (!Dictionary.toggleState["Constant AI Tracking"]) return;

                        if (Dictionary.lastLoadedModel == "N/A")
                        {
                            Dictionary.toggleState["Constant AI Tracking"] = false;
                            UpdateToggleUI(t, false);
                        }
                        else
                        {
                            Dictionary.toggleState["Aim Assist"] = true;
                            if (uiManager.T_AimAligner != null)
                                UpdateToggleUI(uiManager.T_AimAligner, true);
                        }
                    };
                })
                .AddKey("Aim Keybind", "Aim key", "Hold this to aim")
                .AddKey("Second Aim Keybind", "Second aim key", "Another key that also aims", when: advanced);
            left.Children.Add(activation.Root);

            var movement = new CardBuilder(this, "Movement", "How fast and smooth the crosshair moves")
                .AddSlider("Mouse Sensitivity (+/-)", "Sensitivity", "", 0.01, 0.01, 0.01, 1,
                    "Lower is faster and snappier, higher is slower and smoother", s =>
                    {
                        uiManager.S_MouseSensitivity = s;
                        s.Slider.PreviewMouseLeftButtonUp += (_, _) =>
                        {
                            var value = s.Slider.Value;
                            if (value >= 0.98)
                                LogManager.Log(LogLevel.Warning,
                                    "The Mouse Sensitivity you have set can cause Whales to be unable to aim, please decrease if you suffer from this problem", true);
                            else if (value <= 0.1)
                                LogManager.Log(LogLevel.Warning,
                                    "The Mouse Sensitivity you have set can cause Whales to be unstable to aim, please increase if you suffer from this problem", true);
                        };
                    })
                .AddToggle("EMA Smoothening", "Smoothing", "Evens out small jumps so tracking looks steadier",
                    t => uiManager.T_EMASmoothing = t)
                .AddSlider("EMA Smoothening", "Smoothing amount", "", 0.01, 0.01, 0.01, 1,
                    "Lower is smoother but slower to react", s =>
                    {
                        uiManager.S_EMASmoothing = s;
                        s.Slider.ValueChanged += (_, _) => MouseManager.smoothingFactor = s.Slider.Value;
                    }, when: () => IsOn("EMA Smoothening"))
                .AddSlider("Mouse Jitter", "Jitter", "px", 1, 1, 0, 15,
                    "Random small movements so aim looks more human", s => uiManager.S_MouseJitter = s, when: advanced)
                .AddDropdown("Movement Path", "Movement path", "The route to a new target. Once there, every path tracks the same way.",
                    MovementPaths, d =>
                    {
                        uiManager.D_MovementPath = d;
                        d.DropdownBox.SelectionChanged += (_, _) => SimulateMovementPath();
                    })
                .AddElement(BuildMovementPreview(), simpleKey: "Movement Preview");
            left.Children.Add(movement.Root);

            // Replay the movement preview whenever a setting it depends on changes
            foreach (var key in new[] { "Mouse Sensitivity (+/-)", "EMA Smoothening", "Mouse Jitter" })
            {
                if (_sliders.TryGetValue(key, out var slider))
                    slider.Slider.ValueChanged += (_, _) => SimulateMovementPath();
            }

            Func<bool> autoTrigger = () => IsOn("Auto Trigger");
            var trigger = new CardBuilder(this, "Auto trigger", "Click for you when a target is under the crosshair")
                .AddToggle("Auto Trigger", "Auto trigger", null, t => uiManager.T_AutoTrigger = t)
                .AddToggle("Cursor Check", "Only when on target", "Fire only when the crosshair is inside the target box",
                    t => uiManager.T_CursorCheck = t, when: autoTrigger)
                .AddToggle("Spray Mode", "Spray mode", "Hold fire instead of single clicks, for automatic weapons",
                    t => uiManager.T_SprayMode = t, when: autoTrigger)
                .AddSlider("Auto Trigger Delay", "Delay", "s", 0.01, 0.1, 0.01, 1,
                    "Wait this long after a target appears before firing", s => uiManager.S_AutoTriggerDelay = s, when: autoTrigger);
            left.Children.Add(trigger.Root);

            var targeting = new CardBuilder(this, "Targeting", "Which target to pick and where to aim on it")
                .AddDropdown("Detection Area Type", "Pick target", "Which detected target gets aimed at",
                    new[] { "Closest to Center Screen", "Closest to Mouse" }, d =>
                    {
                        uiManager.D_DetectionAreaType = d;
                        uiManager.DDI_ClosestToCenterScreen = d.DropdownBox.Items[0] as ComboBoxItem;
                        uiManager.DDI_ClosestToCenterScreen!.Selected += async (_, _) =>
                        {
                            if (!_windowsReady) return;
                            await Task.Delay(100);
                            FOVWindow.FOVStrictEnclosure.Margin = new Thickness(
                                Convert.ToInt16((WinAPICaller.ScreenWidth / 2) / WinAPICaller.scalingFactorX) - 320,
                                Convert.ToInt16((WinAPICaller.ScreenHeight / 2) / WinAPICaller.scalingFactorY) - 320,
                                0, 0);
                        };
                    })
                .AddDropdown("Aiming Boundaries Alignment", "Aim at", "The point on the target box to aim at",
                    new[] { "Center", "Top", "Bottom" }, d => uiManager.D_AimingBoundariesAlignment = d)
                .AddToggle("Sticky Aim", "Stick to target", "Stay locked on one target until it's gone, even if another is closer",
                    t => uiManager.T_StickyAim = t)
                .AddSlider("Sticky Aim Threshold", "Switch radius", "px", 1, 1, 0, 100,
                    "Switch to another target only when you put your crosshair this close to it. 0 = never switch while your target is visible",
                    s => uiManager.S_StickyAimThreshold = s,
                    when: () => IsOn("Sticky Aim"))
                .AddToggle("Third Person Support", "Third-person games", "Adjusts the detection area for third-person cameras",
                    t => uiManager.T_ThirdPersonSupport = t, when: advanced)
                .AddToggle("Y Axis Percentage Adjustment", "Vertical offset in %", "Use a percentage of the box height instead of pixels",
                    t => uiManager.T_YAxisPercentageAdjustment = t, when: advanced)
                .AddSlider("Y Offset (Up/Down)", "Vertical offset", "px", 1, 1, -150, 150,
                    "Move the aim point up (-) or down (+)", s => uiManager.S_YOffset = s,
                    when: () => ShowAdvanced && !IsOn("Y Axis Percentage Adjustment"))
                .AddSlider("Y Offset (%)", "Vertical offset", "%", 1, 1, 0, 100,
                    "Aim point as a percentage of the box height", s => uiManager.S_YOffsetPercent = s,
                    when: () => ShowAdvanced && IsOn("Y Axis Percentage Adjustment"))
                .AddToggle("X Axis Percentage Adjustment", "Horizontal offset in %", "Use a percentage of the box width instead of pixels",
                    t => uiManager.T_XAxisPercentageAdjustment = t, when: advanced)
                .AddSlider("X Offset (Left/Right)", "Horizontal offset", "px", 1, 1, -150, 150,
                    "Move the aim point left (-) or right (+)", s => uiManager.S_XOffset = s,
                    when: () => ShowAdvanced && !IsOn("X Axis Percentage Adjustment"))
                .AddSlider("X Offset (%)", "Horizontal offset", "%", 1, 1, 0, 100,
                    "Aim point as a percentage of the box width", s => uiManager.S_XOffsetPercent = s,
                    when: () => ShowAdvanced && IsOn("X Axis Percentage Adjustment"));
            right.Children.Add(targeting.Root);

            Func<bool> predictions = () => IsOn("Predictions");
            Func<string, Func<bool>> method = name => () => IsOn("Predictions") && SelectedText(uiManager.D_PredictionMethod) == name;
            var prediction = new CardBuilder(this, "Prediction", "Lead moving targets so the crosshair keeps up")
                .AddToggle("Predictions", "Prediction", "Aim where a moving target is going, not where it is",
                    t => uiManager.T_Predictions = t)
                .AddDropdown("Prediction Method", "Method", "Try each one to see which tracks best",
                    new[] { "Kalman Filter", "Shall0e's Prediction", "wisethef0x's EMA Prediction" }, d =>
                    {
                        uiManager.D_PredictionMethod = d;
                        d.DropdownBox.SelectionChanged += (_, _) => RefreshConditionalRows();
                    }, when: predictions)
                .AddSlider("Kalman Lead Time", "Lead time", "s", 0.01, 0.01, 0.02, 0.30,
                    "How far ahead to aim. Too high overshoots", s => uiManager.S_KalmanLeadTime = s,
                    when: method("Kalman Filter"))
                .AddSlider("WiseTheFox Lead Time", "Lead time", "s", 0.01, 0.01, 0.02, 0.30,
                    "How far ahead to aim. Too high overshoots", s => uiManager.S_WiseTheFoxLeadTime = s,
                    when: method("wisethef0x's EMA Prediction"))
                .AddSlider("Shalloe Lead Multiplier", "Lead", "frames", 0.5, 0.5, 1, 10,
                    "How many frames ahead to aim. Too high overshoots", s => uiManager.S_ShalloeLeadMultiplier = s,
                    when: method("Shall0e's Prediction"));
            right.Children.Add(prediction.Root);

            columns.Margin = PageMargin;
            return MakeScrollPage(columns);
        }

        #endregion

        #region Visuals

        private FrameworkElement BuildVisualsPage()
        {
            var columns = MakeColumns(out var left, out var right);
            Func<bool> dynamicFov = () => IsOn("Dynamic FOV");

            var fov = new CardBuilder(this, "FOV area", "The region around your crosshair that is searched for targets")
                .AddToggle("FOV", "Show FOV", "Draw the search area on screen", t => uiManager.T_FOV = t)
                .AddSlider("FOV Size", "FOV size", "px", 1, 1, 10, 640,
                    "Smaller is more precise, larger covers more of the screen", s =>
                    {
                        uiManager.S_FOVSize = s;
                        s.Slider.ValueChanged += (_, _) =>
                        {
                            ActualFOV = s.Slider.Value;
                            PropertyChanger.PostNewFOVSize(ActualFOV);
                        };
                    })
                .AddDropdown("FOV Style", "Shape", null, new[] { "Circle", "Rectangle" }, d =>
                {
                    uiManager.D_FOVSTYLE = d;
                    d.DropdownBox.SelectionChanged += (_, _) => ApplyFovStyle();
                })
                .AddColor("FOV Color", "Colour", null, color =>
                {
                    PropertyChanger.PostColor(color);
                    UpdateVisualPreview();
                })
                .AddToggle("Dynamic FOV", "Dynamic FOV", "Use a different size while holding a key, e.g. when scoped in",
                    t => uiManager.T_DynamicFOV = t)
                .AddKey("Dynamic FOV Keybind", "Dynamic FOV key", "Hold to switch to the size below", when: dynamicFov)
                .AddSlider("Dynamic FOV Size", "Dynamic FOV size", "px", 1, 1, 10, 640, null, s =>
                {
                    uiManager.S_DynamicFOVSize = s;
                    s.Slider.ValueChanged += (_, _) =>
                    {
                        if (Dictionary.toggleState["Dynamic FOV"])
                            PropertyChanger.PostNewFOVSize(s.Slider.Value);
                    };
                }, when: dynamicFov);
            left.Children.Add(fov.Root);

            Func<bool> boxes = () => IsOn("Show Detected Player");
            Func<bool> boxesAdvanced = () => IsOn("Show Detected Player") && ShowAdvanced;
            var overlay = new CardBuilder(this, "Target overlay", "Boxes and labels drawn on detected targets")
                .AddToggle("Show Detected Player", "Box around targets", "Draw a box on every detected target",
                    t => uiManager.T_ShowDetectedPlayer = t)
                .AddToggle("Show AI Confidence", "Confidence label", "Show how sure the model is (0-100%)",
                    t => uiManager.T_ShowAIConfidence = t, when: boxes)
                .AddToggle("Show Tracers", "Tracer lines", "Draw a line from the screen edge to each target",
                    t => uiManager.T_ShowTracers = t, when: boxes)
                .AddDropdown("Tracer Position", "Tracer start", null, new[] { "Top", "Middle", "Bottom" }, d =>
                {
                    uiManager.D_TracerPosition = d;
                    d.DropdownBox.SelectionChanged += (_, _) =>
                    {
                        if (_suppressDropdownSideEffects || !_windowsReady) return;

                        if (Dictionary.toggleState["Show Detected Player"] && uiManager.T_ShowDetectedPlayer != null)
                        {
                            // Switching the overlay off and on again is what makes the tracer move
                            var reader = uiManager.T_ShowDetectedPlayer.Reader;
                            reader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            reader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        }
                        else
                        {
                            Dictionary.DetectedPlayerOverlay?.ForceReposition();
                        }
                    };
                }, when: () => IsOn("Show Detected Player") && IsOn("Show Tracers"))
                .AddColor("Detected Player Color", "Box colour", null, color =>
                {
                    PropertyChanger.PostDPColor(color);
                    UpdateVisualPreview();
                }, when: boxes)
                .AddSlider("AI Confidence Font Size", "Label size", "", 1, 1, 1, 30, null, s =>
                {
                    uiManager.S_DPFontSize = s;
                    s.Slider.ValueChanged += (_, _) => PropertyChanger.PostDPFontSize((int)s.Slider.Value);
                }, when: boxesAdvanced)
                .AddSlider("Corner Radius", "Corner radius", "px", 1, 1, 0, 100, null, s =>
                {
                    uiManager.S_DPCornerRadius = s;
                    s.Slider.ValueChanged += (_, _) => PropertyChanger.PostDPWCornerRadius((int)s.Slider.Value);
                }, when: boxesAdvanced)
                .AddSlider("Border Thickness", "Border thickness", "px", 0.1, 1, 0.1, 10, null, s =>
                {
                    uiManager.S_DPBorderThickness = s;
                    s.Slider.ValueChanged += (_, _) => PropertyChanger.PostDPWBorderThickness(s.Slider.Value);
                }, when: boxesAdvanced)
                .AddSlider("Opacity", "Opacity", "", 0.1, 0.1, 0, 1, null, s =>
                {
                    uiManager.S_DPOpacity = s;
                    s.Slider.ValueChanged += (_, _) => PropertyChanger.PostDPWOpacity(s.Slider.Value);
                }, when: boxesAdvanced);

            var preview = new CardBuilder(this, "Preview", "Updates as you change the settings on this page")
                .AddElement(BuildVisualPreview());
            right.Children.Add(preview.Root);
            right.Children.Add(overlay.Root);

            // Redraw the preview whenever a visual setting changes (toggles redraw via RefreshConditionalRows)
            foreach (var key in new[] { "FOV Size", "Dynamic FOV Size", "AI Confidence Font Size", "Corner Radius", "Border Thickness", "Opacity" })
            {
                if (_sliders.TryGetValue(key, out var slider))
                    slider.Slider.ValueChanged += (_, _) => UpdateVisualPreview();
            }
            foreach (var key in new[] { "FOV Style", "Tracer Position" })
            {
                if (_dropdowns.TryGetValue(key, out var dropdown))
                    dropdown.DropdownBox.SelectionChanged += (_, _) => UpdateVisualPreview();
            }

            columns.Margin = PageMargin;
            return MakeScrollPage(columns);
        }

        #region Visuals preview

        // The preview shows an 800x450 area around the crosshair at real proportions (1 unit = 1 screen pixel)
        private const double PreviewWidth = 800;
        private const double PreviewHeight = 450;
        private const double PreviewFaded = 0.2;

        private Canvas? _previewCanvas;
        private double _previewScale = 0.55;

        private static readonly Brush PreviewSky = FrozenGradient(Color.FromRgb(0x1D, 0x27, 0x31), Color.FromRgb(0x14, 0x1B, 0x22));
        private static readonly Brush PreviewGround = FrozenGradient(Color.FromRgb(0x17, 0x1E, 0x24), Color.FromRgb(0x0F, 0x14, 0x18));
        private static readonly Brush PreviewFigure = Frozen(new SolidColorBrush(Color.FromRgb(0x3E, 0x4A, 0x55)));
        private static readonly Brush PreviewFigureDark = Frozen(new SolidColorBrush(Color.FromRgb(0x31, 0x3B, 0x44)));
        private static readonly Brush PreviewCrosshair = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

        private static Brush FrozenGradient(Color top, Color bottom) =>
            Frozen(new LinearGradientBrush(top, bottom, 90));

        private FrameworkElement BuildVisualPreview()
        {
            _previewCanvas = new Canvas
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
                ClipToBounds = true,
                Clip = new RectangleGeometry(new Rect(0, 0, PreviewWidth, PreviewHeight), 16, 16)
            };

            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _previewCanvas };
            viewbox.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 0) return;
                _previewScale = e.NewSize.Width / PreviewWidth;
                UpdateVisualPreview();
            };

            var stack = new StackPanel { Margin = new Thickness(12, 2, 12, 4) };
            stack.Children.Add(viewbox);
            var legend = MakeSmall("Real proportions around your crosshair. Faded parts are switched off; dashed is the Dynamic FOV size.");
            legend.Margin = new Thickness(2, 8, 2, 0);
            stack.Children.Add(legend);
            return stack;
        }

        private static Color ParseColor(string key, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(Dictionary.colorState[key].ToString());
            }
            catch
            {
                return fallback;
            }
        }

        private double SliderValue(string key, double fallback) =>
            _sliders.TryGetValue(key, out var slider) ? slider.Slider.Value : fallback;

        /// <summary>Redraws the Visuals preview from the current settings.</summary>
        private void UpdateVisualPreview()
        {
            var canvas = _previewCanvas;
            if (canvas == null) return;

            canvas.Children.Clear();
            double onePixel = 1 / Math.Max(_previewScale, 0.05); // canvas units per on-screen pixel
            double cx = PreviewWidth / 2, cy = PreviewHeight / 2;

            // Scene: sky, ground and a stand-in target
            AddAt(canvas, new Rectangle { Width = PreviewWidth, Height = PreviewHeight, Fill = PreviewSky }, 0, 0);
            AddAt(canvas, new Rectangle { Width = PreviewWidth, Height = PreviewHeight - 300, Fill = PreviewGround }, 0, 300);

            double boxW = 66, boxH = 150;
            double boxX = cx + 120 - boxW / 2, boxY = cy - 52;
            AddAt(canvas, new Ellipse { Width = 26, Height = 28, Fill = PreviewFigure }, boxX + 20, boxY + 4);
            AddAt(canvas, new Border { Width = 46, Height = 64, CornerRadius = new CornerRadius(10), Background = PreviewFigure }, boxX + 10, boxY + 34);
            AddAt(canvas, new Rectangle { Width = 17, Height = 54, Fill = PreviewFigureDark, RadiusX = 5, RadiusY = 5 }, boxX + 14, boxY + 94);
            AddAt(canvas, new Rectangle { Width = 17, Height = 54, Fill = PreviewFigureDark, RadiusX = 5, RadiusY = 5 }, boxX + 35, boxY + 94);

            // FOV (1 px outline, like the real overlay)
            bool fovOn = IsOn("FOV");
            bool rectangle = (SelectedText(uiManager.D_FOVSTYLE) ?? Dictionary.dropdownState["FOV Style"]?.ToString()) == "Rectangle";
            var fovBrush = new SolidColorBrush(ParseColor("FOV Color", Colors.White));
            AddFovShape(canvas, SliderValue("FOV Size", 640), rectangle, fovBrush, onePixel, fovOn ? 1 : PreviewFaded, dashed: false);
            if (IsOn("Dynamic FOV"))
                AddFovShape(canvas, SliderValue("Dynamic FOV Size", 640), rectangle, fovBrush, onePixel, fovOn ? 0.7 : PreviewFaded, dashed: true);

            // Target overlay: box, confidence label and tracer share the overlay's opacity
            bool boxesOn = IsOn("Show Detected Player");
            double overlayOpacity = Math.Clamp(SliderValue("Opacity", 1), 0, 1);
            var dpBrush = new SolidColorBrush(ParseColor("Detected Player Color", Colors.Cyan));
            double thickness = Math.Max(SliderValue("Border Thickness", 1), onePixel);
            double radius = Math.Min(SliderValue("Corner Radius", 0), boxW / 2);

            double Fade(bool on) => (boxesOn && on ? 1 : PreviewFaded) * overlayOpacity;

            // Tracer (drawn faded when switched off, like everything else)
            {
                string tracer = SelectedText(uiManager.D_TracerPosition) ?? Dictionary.dropdownState["Tracer Position"]?.ToString() ?? "Bottom";
                double screenHalfHeight = Math.Max(DisplayManager.ScreenHeight / Math.Max(WinAPICaller.scalingFactorY, 0.1), PreviewHeight) / 2;
                double startY = tracer switch
                {
                    "Top" => cy - screenHalfHeight,
                    "Middle" => cy,
                    _ => cy + screenHalfHeight
                };
                (double endX, double endY) = tracer switch
                {
                    "Top" => (boxX + boxW / 2, boxY),
                    "Middle" => (boxX, boxY + boxH / 2), // box is right of centre, so the line meets its left side
                    _ => (boxX + boxW / 2, boxY + boxH)
                };
                canvas.Children.Add(new Line
                {
                    X1 = cx, Y1 = startY, X2 = endX, Y2 = endY,
                    Stroke = dpBrush,
                    StrokeThickness = thickness,
                    Opacity = Fade(IsOn("Show Tracers"))
                });
            }

            AddAt(canvas, new Border
            {
                Width = boxW,
                Height = boxH,
                BorderBrush = dpBrush,
                BorderThickness = new Thickness(thickness),
                CornerRadius = new CornerRadius(radius),
                Opacity = Fade(true)
            }, boxX, boxY);

            string className = FileManager.AIManager?.ModelClasses?.Values.FirstOrDefault() ?? "Enemy";
            var label = new TextBlock
            {
                Text = $"{className}: 87.5%",
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(SliderValue("AI Confidence Font Size", 20), 1),
                Foreground = dpBrush,
                Opacity = Fade(IsOn("Show AI Confidence"))
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            AddAt(canvas, label, boxX + boxW / 2 - label.DesiredSize.Width / 2, boxY - label.DesiredSize.Height - 2);

            // Crosshair marks the screen centre
            double arm = 9 * onePixel, gap = 3 * onePixel, crossWidth = 1.5 * onePixel;
            canvas.Children.Add(new Line { X1 = cx - arm, Y1 = cy, X2 = cx - gap, Y2 = cy, Stroke = PreviewCrosshair, StrokeThickness = crossWidth });
            canvas.Children.Add(new Line { X1 = cx + gap, Y1 = cy, X2 = cx + arm, Y2 = cy, Stroke = PreviewCrosshair, StrokeThickness = crossWidth });
            canvas.Children.Add(new Line { X1 = cx, Y1 = cy - arm, X2 = cx, Y2 = cy - gap, Stroke = PreviewCrosshair, StrokeThickness = crossWidth });
            canvas.Children.Add(new Line { X1 = cx, Y1 = cy + gap, X2 = cx, Y2 = cy + arm, Stroke = PreviewCrosshair, StrokeThickness = crossWidth });
        }

        private static void AddFovShape(Canvas canvas, double size, bool rectangle, Brush stroke, double onePixel, double opacity, bool dashed)
        {
            Shape shape = rectangle ? new Rectangle() : new Ellipse();
            shape.Width = size;
            shape.Height = size;
            shape.Stroke = stroke;
            shape.StrokeThickness = Math.Max(1, onePixel);
            shape.Opacity = opacity;
            if (dashed)
                shape.StrokeDashArray = new DoubleCollection { 5, 4 };
            AddAt(canvas, shape, PreviewWidth / 2 - size / 2, PreviewHeight / 2 - size / 2);
        }

        private static void AddAt(Canvas canvas, UIElement element, double left, double top)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            canvas.Children.Add(element);
        }

        #endregion

        private void ApplyFovStyle()
        {
            if (!_windowsReady) return;

            bool rectangle = (SelectedText(uiManager.D_FOVSTYLE) ?? Dictionary.dropdownState["FOV Style"]?.ToString()) == "Rectangle";
            FOVWindow.Circle.Visibility = rectangle ? Visibility.Collapsed : Visibility.Visible;
            FOVWindow.RectangleShape.Visibility = rectangle ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Models

        private FrameworkElement BuildModelsPage()
        {
            var grid = new Grid { Margin = new Thickness(24, 20, 24, 24) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });

            // Library (existing model/config lists and store)
            var library = new DockPanel();
            var header = new StackPanel { Margin = new Thickness(4, 2, 4, 6) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock { Text = "Library", Style = Res<Style>("UI.CardTitle") });
            header.Children.Add(new TextBlock
            {
                Text = "Click a model or config to load it. Drag .onnx or .cfg files onto the list to add them.",
                Style = Res<Style>("UI.CardSubtitle")
            });
            library.Children.Add(header);
            library.Children.Add(_modelMenu);
            grid.Children.Add(new Border
            {
                Style = Res<Style>("UI.Card"),
                Margin = new Thickness(0),
                Padding = new Thickness(12, 12, 12, 4),
                Child = library
            });

            var side = new StackPanel();
            var detection = new CardBuilder(this, "Detection", "How the loaded model looks for targets")
                .AddSlider("AI Minimum Confidence", "Minimum confidence", "%", 1, 1, 1, 100,
                    "Higher means fewer false hits but may miss targets", s =>
                    {
                        uiManager.S_AIMinimumConfidence = s;
                        s.Slider.PreviewMouseLeftButtonUp += (_, _) =>
                        {
                            var value = s.Slider.Value;
                            if (value >= 95)
                                LogManager.Log(LogLevel.Warning, "The minimum confidence you have set for Whales to be too high and may be unable to detect players.", true);
                            else if (value <= 35)
                                LogManager.Log(LogLevel.Warning, "The minimum confidence you have set for Whales may be too low can cause false positives.", true);
                        };
                    })
                .AddDropdown("Target Class", "Target class", "Which kind of detection to aim at",
                    new[] { "Best Confidence" }, d => uiManager.D_TargetClass = d)
                .AddDropdown("Image Size", "Image size", "Resolution the model sees. Smaller is faster, larger is more accurate",
                    ImageSizes, d =>
                    {
                        uiManager.D_ImageSize = d;
                        d.DropdownBox.SelectionChanged += ImageSize_SelectionChanged;
                    });
            side.Children.Add(detection.Root);

            var switching = new CardBuilder(this, "Model switching", "Cycle through your models with a hotkey")
                .AddToggle("Enable Model Switch Keybind", "Model switch key", "Press the key below to load the next model",
                    t => uiManager.T_EnableModelSwitchKeybind = t)
                .AddKey("Model Switch Keybind", "Switch key", null, when: () => IsOn("Enable Model Switch Keybind"));
            side.Children.Add(HideInSimple(switching.Root));

            var sideScroll = MakeScrollPage(side);
            Grid.SetColumn(sideScroll, 2);
            grid.Children.Add(sideScroll);
            return grid;
        }

        private async void ImageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDropdownSideEffects) return;

            var dropdown = uiManager.D_ImageSize;
            if (dropdown?.DropdownBox.SelectedItem == null || e.AddedItems.Count == 0) return;

            var newSize = SelectedText(dropdown);
            if (string.IsNullOrEmpty(newSize)) return;

            if (FileManager.AIManager == null || Dictionary.lastLoadedModel == "N/A")
            {
                Dictionary.dropdownState["Image Size"] = newSize;
                LogManager.Log(LogLevel.Info, $"Image size set to {newSize}x{newSize} (no model loaded)", true, 2000);
                return;
            }

            FileManager.CurrentlyLoadingModel = true;
            LogManager.Log(LogLevel.Info, $"Image size changing to {newSize}");

            try
            {
                // Let the AI loop pause before tearing the session down
                FileManager.AIManager.RequestSizeChange(int.Parse(newSize));
                await Task.Delay(100);

                var modelPath = Path.Combine("bin/models", Dictionary.lastLoadedModel);
                FileManager.AIManager?.Dispose();
                FileManager.AIManager = null;

                Dictionary.dropdownState["Image Size"] = newSize;
                FileManager.AIManager = new AIManager(modelPath);

                LogManager.Log(LogLevel.Info, $"Successfully changed image size to {newSize}x{newSize}", true, 2000);
            }
            catch (Exception ex)
            {
                LogManager.Log(LogLevel.Error, $"Error changing image size: {ex.Message}", true, 5000);
            }
            finally
            {
                FileManager.CurrentlyLoadingModel = false;
            }
        }

        private void OnImageSizeChanged(int imageSize)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateFovSizeSlider(uiManager.S_FOVSize, imageSize);
                UpdateFovSizeSlider(uiManager.S_DynamicFOVSize, imageSize);
            });
        }

        private static void UpdateFovSizeSlider(ASlider? slider, int imageSize)
        {
            if (slider?.Slider == null) return;
            if (imageSize < slider.Slider.Value)
                slider.Slider.Value = imageSize;
            slider.Slider.Maximum = imageSize;
        }

        private void OnClassesChanged(Dictionary<int, string> classes)
        {
            Dispatcher.Invoke(() =>
            {
                if (uiManager.D_TargetClass != null)
                    UpdateTargetClassDropdown(uiManager.D_TargetClass, classes);
            });
        }

        private void OnDynamicModelStatusChanged(bool isDynamic)
        {
            Dispatcher.Invoke(() => UpdateDynamicModelDropdownsVisibility(isDynamic));
        }

        /// <summary>Image size can only be changed for models with a dynamic input size.</summary>
        private void UpdateDynamicModelDropdownsVisibility(bool isDynamic)
        {
            if (uiManager.D_ImageSize != null)
                uiManager.D_ImageSize.Visibility = isDynamic ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateTargetClassDropdown(ADropdown dropdown, Dictionary<int, string>? newClasses)
        {
            var classes = newClasses ?? FileManager.AIManager?.ModelClasses ?? new Dictionary<int, string>();
            dropdown.Visibility = classes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            string? selection = _pendingTargetClass ?? SelectedText(dropdown);

            var removed = dropdown.DropdownBox.Items.Cast<ComboBoxItem>()
                .Where(item => item.Content?.ToString() != "Best Confidence")
                .ToList();
            foreach (var item in removed)
                dropdown.DropdownBox.Items.Remove(item);

            foreach (var kvp in classes.OrderBy(x => x.Key))
                AddDropdownItem(dropdown, kvp.Value);

            if (classes.Count > 1)
                _pendingTargetClass = null;

            if (!SelectDropdownValue(dropdown, selection))
                dropdown.DropdownBox.SelectedIndex = 0;
        }

        #endregion

        #region Device

        private FrameworkElement BuildDevicePage()
        {
            var columns = MakeColumns(out var left, out var right);

            // Arduino: list of COM ports with Connect buttons (also available from the header PORT chip)
            var arduinoPanel = new StackPanel { Margin = new Thickness(14, 6, 14, 8) };
            var portsTitle = MakeSmall("COM ports", 12);
            portsTitle.FontWeight = FontWeights.SemiBold;
            portsTitle.Margin = new Thickness(0, 0, 0, 6);
            arduinoPanel.Children.Add(portsTitle);
            _devicePortList = new StackPanel();
            arduinoPanel.Children.Add(_devicePortList);

            // RP2350: HID status with a reconnect button
            var hidPanel = new Grid { Margin = new Thickness(14, 6, 14, 8) };
            hidPanel.ColumnDefinitions.Add(new ColumnDefinition());
            hidPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _hidStatusText = new TextBlock
            {
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 13,
                Foreground = Res<Brush>("UI.Text"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            hidPanel.Children.Add(_hidStatusText);
            var reconnect = MakeButton("Reconnect", () =>
            {
                ConnectHidDevice(showMessageIfMissing: true);
                UpdateHidStatus();
                UpdateStatus();
            });
            Grid.SetColumn(reconnect, 1);
            hidPanel.Children.Add(reconnect);

            // Arduino (Raw HID): HID status with a reconnect button — parallel to the RP2350 one
            var arduinoHidPanel = new Grid { Margin = new Thickness(14, 6, 14, 8) };
            arduinoHidPanel.ColumnDefinitions.Add(new ColumnDefinition());
            arduinoHidPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _arduinoHidStatusText = new TextBlock
            {
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 13,
                Foreground = Res<Brush>("UI.Text"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            arduinoHidPanel.Children.Add(_arduinoHidStatusText);
            var arduinoHidReconnect = MakeButton("Reconnect", () =>
            {
                ConnectArduinoHidDevice(showMessageIfMissing: true);
                UpdateHidStatus();
                UpdateStatus();
            });
            Grid.SetColumn(arduinoHidReconnect, 1);
            arduinoHidPanel.Children.Add(arduinoHidReconnect);

            var output = new CardBuilder(this, "Mouse output", "The device that moves your mouse")
                .AddDropdown("Mouse Movement Method", "Device type", "What sends the mouse movement",
                    new[] { "Arduino", "Arduino (Raw HID)", "RP2350" }, d =>
                    {
                        uiManager.D_MouseMovementMethod = d;
                        d.DropdownBox.SelectionChanged += (_, _) =>
                        {
                            // Picking a HID board connects to it straight away (quietly; its row shows the status)
                            if (!_suppressDropdownSideEffects)
                            {
                                if (UsesArduinoHid) ConnectArduinoHidDevice(showMessageIfMissing: false);
                                else if (UsesRp2350 && MouseManager.HidDevice?.IsConnected != true) ConnectHidDevice(showMessageIfMissing: false);
                            }
                            RefreshConditionalRows();
                            UpdateHidStatus();
                            if (_pagesBuilt) UpdateStatus();
                        };
                    })
                .AddElement(arduinoPanel, when: () => UsesArduinoSerial)
                .AddElement(arduinoHidPanel, when: () => UsesArduinoHid)
                .AddElement(hidPanel, when: () => UsesRp2350)
                .AddAction("Send test move", "Nudges your cursor right and back to check the device works",
                    () => _ = MouseManager.SendTestMoveAsync())
                .AddAction("Make a sketch for your board", "Step by step: record your mouse, then generate and upload a COM or Raw HID sketch",
                    () => ShowPage(SketchPage), when: () => !UsesRp2350);
            left.Children.Add(output.Root);

            // Screen capture
            uiManager.DisplaySelector = new ADisplaySelector();
            uiManager.DisplaySelector.RefreshDisplays();
            var capture = new CardBuilder(this, "Screen capture", "Which screen the model watches and how it is captured")
                .AddElement(uiManager.DisplaySelector)
                .AddAction("Refresh displays", "Update the list after plugging in a monitor", () =>
                {
                    try
                    {
                        DisplayManager.RefreshDisplays();
                        uiManager.DisplaySelector.RefreshDisplays();
                        LogManager.Log(LogLevel.Info, "Display list refreshed successfully.", true);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Log(LogLevel.Error, $"Error refreshing displays: {ex.Message}", true);
                    }
                })
                .AddDropdown("Screen Capture Method", "Capture method", "DirectX is faster. GDI+ works on more systems",
                    new[] { "DirectX", "GDI+" }, d => uiManager.D_ScreenCaptureMethod = d)
                .AddToggle("StreamGuard", "Hide from recordings", "Hide this window and the overlays from recordings and streams",
                    t => uiManager.T_StreamGuard = t);
            right.Children.Add(capture.Root);

            UpdateHidStatus();
            columns.Margin = PageMargin;
            return MakeScrollPage(columns);
        }

        private void UpdateHidStatus()
        {
            if (_hidStatusText != null)
            {
                _hidStatusText.Text = MouseManager.HidDevice != null
                    ? "RP2350 connected"
                    : "RP2350 not found. Plug it in, then press Reconnect.";
            }

            if (_arduinoHidStatusText != null)
            {
                _arduinoHidStatusText.Text = MouseManager.ArduinoHid?.IsConnected == true
                    ? "Arduino (Raw HID) connected"
                    : "Not found. Flash mouse_bridge_rawhid.ino to your Leonardo, plug it in, then press Reconnect.";
            }
        }

        /// <summary>Rebuilds the Device page port list when the ports or the connection change.</summary>
        private void UpdateDevicePortList(string[] ports)
        {
            if (_devicePortList == null) return;

            var signature = string.Join(",", ports) + "|" + connectedPort;
            if (signature == _portListSignature) return;
            _portListSignature = signature;

            _devicePortList.Children.Clear();

            if (ports.Length == 0)
            {
                _devicePortList.Children.Add(MakeSmall("No COM ports found. Plug in your Arduino and it will show up here.", 13));
                return;
            }

            foreach (var port in ports)
            {
                bool connected = port == connectedPort;

                var row = new Border
                {
                    Background = Res<Brush>("UI.KeyBg"),
                    BorderBrush = Res<Brush>("UI.Box"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 6, 6, 6),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                if (connected) row.SetResourceReference(Border.BorderBrushProperty, "ThemeColor");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                if (connected)
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ThemeColor");
                else
                    dot.Fill = Res<Brush>("UI.TextDim");
                grid.Children.Add(dot);

                var label = new TextBlock
                {
                    Text = connected ? $"{port}  ·  connected" : port,
                    FontFamily = Res<FontFamily>("UIFont"),
                    FontSize = 13.5,
                    FontWeight = connected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Res<Brush>("UI.Text"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);

                var button = connected
                    ? MakeButton("Disconnect", DisconnectPort)
                    : MakeButton("Connect", () => ConnectPort(port), primary: true);
                button.Height = 28;
                Grid.SetColumn(button, 2);
                grid.Children.Add(button);

                row.Child = grid;
                _devicePortList.Children.Add(row);
            }
        }

        #endregion

        #region Settings

        private FrameworkElement BuildSettingsPage()
        {
            var root = new StackPanel { Margin = PageMargin };
            var columns = MakeColumns(out var left, out var right);

            var layout = new CardBuilder(this, "Layout", "How much is shown on each page")
                .AddToggle("Simple Layout", "Simple layout",
                    "Shows only the main options, in one column, with a quieter header. Switch off to see every option");
            left.Children.Add(layout.Root);

            var hotkeys = new CardBuilder(this, "Hotkeys", "Click a key, then press the new key or mouse button")
                .AddKey("Aim Keybind", "Aim", "Hold to aim")
                .AddKey("Second Aim Keybind", "Second aim", "Another key that also aims")
                .AddKey("Emergency Stop Keybind", "Emergency stop", "Turns aim assist off instantly (press again to turn it back on)")
                .AddKey("Dynamic FOV Keybind", "Dynamic FOV", "Hold to use the dynamic FOV size")
                .AddKey("Model Switch Keybind", "Next model", "Loads the next model when model switching is on");
            left.Children.Add(hotkeys.Root);

            var training = new CardBuilder(this, "Training data", "Collect screenshots to train your own models")
                .AddToggle("Collect Data While Playing", "Save training images", "Save screenshots of detections while you play",
                    t => uiManager.T_CollectDataWhilePlaying = t)
                .AddToggle("Auto Label Data", "Auto-label images", "Write the detection boxes next to each saved image",
                    t => uiManager.T_AutoLabelData = t, when: () => IsOn("Collect Data While Playing"));
            left.Children.Add(HideInSimple(training.Root));

            // Theme colour wheel (existing control)
            uiManager.ThemeColorWheel = new AColorWheel { Margin = new Thickness(8, 0, 8, 4) };
            if (uiManager.ThemeColorWheel.FindName("ArrowButton") is Button arrowButton)
                arrowButton.Visibility = Visibility.Visible;
            var appearance = new CardBuilder(this, "Appearance", "Accent colour used across the app")
                .AddElement(uiManager.ThemeColorWheel);
            right.Children.Add(appearance.Root);

            var app = new CardBuilder(this, "App", "Window and troubleshooting options")
                .AddToggle("UI TopMost", "Keep window on top", "Stay above other windows",
                    t => uiManager.T_UITopMost = t)
                .AddToggle("Check For Updates On Start", "Check for updates on start",
                    "Look for a new version on GitHub each time Whales opens. You're asked before anything is downloaded")
                .AddAction("Save config", "Save your current settings as a config you can load later",
                    () => new ConfigSaver().ShowDialog())
                .AddToggle("Debug Mode", "Debug mode", "Log extra details for troubleshooting",
                    t => uiManager.T_DebugMode = t, when: () => ShowAdvanced);
            right.Children.Add(app.Root);

            root.Children.Add(columns);

            // About (existing control: version, update check, credits)
            _aboutMenu = new AboutMenuControl();
            _aboutMenu.Initialize(this);
            _aboutMenu.AboutMenuScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var about = new CardBuilder(this, "About")
                .AddElement(_aboutMenu);
            root.Children.Add(about.Root);

            return MakeScrollPage(root);
        }

        #endregion
    }
}
