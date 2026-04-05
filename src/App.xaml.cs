using System.Windows;

namespace NarratorHotkey
{
    public partial class App
    {
        private TrayApplication _trayApp;
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Set ShutdownMode to explicit because we don't have a visible main window
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindow = new MainWindow();
            ((MainWindow)MainWindow).InitializeHidden();

            // Create the tray application
            _trayApp = new TrayApplication();
        }
        
        protected override void OnExit(ExitEventArgs e)
        {
            _trayApp?.Dispose();
            base.OnExit(e);
        }
    }
}