using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using NarratorHotkey.Speech;
using Application = System.Windows.Application;

namespace NarratorHotkey;

public partial class MainWindow
{
    private NotifyIcon trayIcon;

    public MainWindow()
    {

        var s = new Synthesize();
        
        const string startupMessage = "Application started. Press Control and 2 to read selected text.";
        Synthesize.ReadText(startupMessage);
        InitializeComponent();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        trayIcon = new NotifyIcon()
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
            Visible = true
        };

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Settings", null, (s, e) => ShowSettings());
        contextMenu.Items.Add("Update Voice Registry", null, (s, e) => VoiceInstallerHelper.RunVoiceInstaller());
        contextMenu.Items.Add("Exit", null, (s, e) => 
            { Application.Current.Shutdown(); });
        
        trayIcon.ContextMenuStrip = contextMenu;

        // Handle double click on tray icon
        trayIcon.DoubleClick += (s, e) => 
        {
            Show();
            WindowState = WindowState.Normal;
        };

        // Handle window state changes
        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        };

        // Clean up tray icon when application closes
        Closed += (s, e) =>
        {
            trayIcon.Dispose();
        };
    }
    
    private void ShowSettings()
    {
        var settingsWindow = new Settings();
        var result = settingsWindow.ShowDialog();
        
        if (result == true)
        {
            // Settings were saved, reload them
            var settings = AppSettings.Load();
            // Apply any immediate settings changes here if needed
        }
    }


}