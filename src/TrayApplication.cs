namespace NarratorHotkey;

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Forms;
using Speech;
using Application = System.Windows.Application;

public class TrayApplication : Component
{
    private readonly NotifyIcon trayIcon;

    public TrayApplication()
    {
        // Initialize tray icon
        trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
            Visible = true
        };

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Settings", null, (s, e) => ShowSettings());
        contextMenu.Items.Add("Update Voice Registry", null, (s, e) => VoiceInstallerHelper.RunVoiceInstaller());
        contextMenu.Items.Add("Exit", null, (s, e) => Application.Current.Shutdown());

        trayIcon.ContextMenuStrip = contextMenu;

        // Defer startup message to avoid blocking the UI thread during Piper initialization
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500); // Let UI settle
                const string startupMessage = "Application started. Press Control and 2 to read selected text.";
                await SpeechManager.Instance.SpeakAsync(startupMessage);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to play startup message: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            trayIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowSettings()
    {
        var settingsWindow = new Settings();
        settingsWindow.ShowDialog();
    }
}