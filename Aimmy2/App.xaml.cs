using Whales2.Theme;
using Class;
using System.Runtime.InteropServices;
using System.Windows;

namespace Whales2
{
    public partial class App : Application
    {
        // Force 1ms system timer resolution. Without this, the default 15.6ms Windows
        // timer gates every Task.Delay / async continuation on the UI thread, pinning
        // the AI loop to ~64 fps when the mouse is idle. Mouse movement implicitly
        // raises the resolution — which is why fps jumped to 120+ only while moving.
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const uint TimerResolutionMs = 1;

        protected override void OnStartup(StartupEventArgs e)
        {
            TimeBeginPeriod(TimerResolutionMs);

            // Initialize the application theme from saved settings
            InitializeTheme();

            // Set shutdown mode to prevent app from closing when startup window closes
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

#if DEBUG
            var _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Show();
            return;
#endif
            // code IS reachable, only in release though
            try
            {
                // Create and show startup window
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();

                // Reset shutdown mode after startup window is shown
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            catch (Exception ex)
            {
                // If startup window fails, launch main window directly
                MessageBox.Show($"Startup animation failed: {ex.Message}\nLaunching main application...",
                              "Whales AI", MessageBoxButton.OK, MessageBoxImage.Information);

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();

                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            TimeEndPeriod(TimerResolutionMs);
            base.OnExit(e);
        }

        private void InitializeTheme()
        {
            try
            {
                // Load the color state configuration
                var colorState = new Dictionary<string, dynamic>
                {
                    { "Theme Color", "#FFD7264B" }
                };

                // Load saved colors
                SaveDictionary.LoadJSON(colorState, "bin\\colors.cfg");

                // Apply theme color if found
                if (colorState.TryGetValue("Theme Color", out var themeColor) && themeColor is string colorString)
                {
                    ThemeManager.SetThemeColor(colorString);
                }
                else
                {
                    // Use default purple if no saved color
                    ThemeManager.SetThemeColor("#FFD7264B");
                }
            }
            catch (Exception ex)
            {
                // Log error and use default color
                ThemeManager.SetThemeColor("#FFD7264B");
            }
        }
    }
}