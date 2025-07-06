using System.Windows;
using Microsoft.Win32;

namespace NarratorHotkey
{
    public partial class Settings : Window
    {
        private readonly AppSettings _settings;

        public Settings()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadSettings();
        }

        private void LoadSettings()
        {
            StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
            MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked ?? false;
            _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked ?? false;

            _settings.Save();
            UpdateStartupRegistry();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void UpdateStartupRegistry()
        {
            const string applicationName = "NarratorHotkey";
            var registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            
            if (_settings.StartWithWindows)
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                registryKey?.SetValue(applicationName, exePath);
            }
            else
            {
                registryKey?.DeleteValue(applicationName, false);
            }
        }
    }
}
