using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace NarratorHotkey;

public partial class MainWindow
{
    private NotifyIcon trayIcon;

    public MainWindow()
    {
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
        // Remove the Owner property setting since it's causing issues
        if (settingsWindow.ShowDialog() != true) return;
        
        // Reload settings if needed
        var settings = AppSettings.Load();
        // Apply any immediate settings changes
        if (settings.MinimizeToTray)
        {
            // Update minimize behavior
        }
    }


}