using System.Windows;

namespace NarratorHotkey
{
    public partial class App
    {
        private TrayApplication _trayApp;
        
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            MainWindow = new MainWindow();
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