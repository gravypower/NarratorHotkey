#if WINDOWS
namespace NarratorHotkey;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Speech;

public class TrayApplication : Form
{
    private readonly NotifyIcon trayIcon;
    private HotkeyManager _hotkeyManager;

    public TrayApplication()
    {
        // Configure invisible Form state
        this.WindowState = FormWindowState.Minimized;
        this.ShowInTaskbar = false;
        this.Visible = false;
        this.Opacity = 0;
        this.Size = new System.Drawing.Size(1, 1);

        // Start local Web Settings Server
        WebGui.Start(49191);

        System.Drawing.Icon appIcon = null;
        try
        {
            // Try local icon.ico file in the directory first
            string localIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(localIcon))
            {
                appIcon = new System.Drawing.Icon(localIcon);
            }
        }
        catch { }

        if (appIcon == null)
        {
            try
            {
                // Try to extract from the executing executable first (the .exe host on Windows)
                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Ignore the generic dotnet runner icon
                    if (!Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }
            }
            catch { }
        }

        if (appIcon == null)
        {
            try
            {
                // Try the assembly location if possible
                string asmLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmLocation) && File.Exists(asmLocation))
                {
                    appIcon = System.Drawing.Icon.ExtractAssociatedIcon(asmLocation);
                }
            }
            catch { }
        }

        if (appIcon == null)
        {
            // Fallback so it never crashes
            appIcon = System.Drawing.SystemIcons.Application;
        }

        // Initialize tray icon
        trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = "Narrator Hotkey"
        };

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        var pauseItem = new ToolStripMenuItem("Pause", null, (s, e) => { _ = TogglePauseAsync(); });
        contextMenu.Items.Add(pauseItem);
        contextMenu.Items.Add("Stop", null, (s, e) => { _ = SpeechManager.Instance.StopAsync(); });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Settings", null, (s, e) => ShowSettings());
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

        // The one item that changes with what the app is doing, so it is labelled when
        // the menu opens rather than left saying "Pause" while speech is already held.
        contextMenu.Opening += (s, e) =>
        {
            pauseItem.Text = SpeechManager.Instance.IsPaused ? "Resume" : "Pause";
            pauseItem.Enabled = SpeechManager.Instance.IsSpeaking;
        };

        trayIcon.ContextMenuStrip = contextMenu;

        // Double click tray icon opens settings
        trayIcon.DoubleClick += (s, e) => ShowSettings();

        // This form is never shown, so its window handle would never be created, and
        // Control.InvokeRequired reports false until it is. Without a handle, settings
        // saved from the web UI would run ReloadHotkey on the HttpListener thread
        // instead of the message-loop thread that owns the hotkey registration.
        _ = this.Handle;

        // Initialize hotkeys with the triggering actions
        _hotkeyManager = new HotkeyManager(
            () => { _ = ProcessHotkeyAsync(); },
            () => { _ = TogglePauseAsync(); });

        // Subscribe to settings change notifications
        Program.OnSettingsChanged += ReloadHotkey;

        // Defer startup message to avoid blocking the UI thread during Piper/Kokoro initialization
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500); // Let UI settle
                var settings = AppSettings.Load();
                
                // Adjust default provider on Windows if it is set to Web-fallback on Linux
                if (settings.TTSProvider == "Kokoro ONNX" || settings.TTSProvider == "Piper" || settings.TTSProvider == "Windows")
                {
                    await SpeechManager.Instance.ApplySettingsAsync();
                }

                var mod = settings.HotkeyModifier == "None" ? "" : $"{settings.HotkeyModifier} and ";
                string startupMessage = $"Application started. Press {mod}{settings.HotkeyKey} to read selected text.";
                await SpeechManager.Instance.SpeakAsync(startupMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to play startup message: {ex.Message}");
            }
        });
    }



    private async Task ProcessHotkeyAsync()
    {
        try
        {
            Console.WriteLine("[TrayApplication] Processing hotkey...");
            if (SpeechManager.Instance.IsSpeaking)
            {
                Console.WriteLine("[TrayApplication] Speech is playing, stopping speech.");
                await SpeechManager.Instance.StopAsync();
            }
            else
            {
                Console.WriteLine("[TrayApplication] Speech is idle, retrieving selected text...");
                var selectedText = await HotkeyManager.GetSelectedTextAsync();
                Console.WriteLine($"[TrayApplication] Retrieved text: '{selectedText}'");
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    await SpeechManager.Instance.SpeakAsync(selectedText);
                }
                else
                {
                    await SpeechManager.Instance.SpeakAsync("No text selected.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing hotkey: {ex.Message}");
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            bool paused = await SpeechManager.Instance.TogglePauseAsync();
            Console.WriteLine($"[TrayApplication] Speech {(paused ? "paused" : "resumed")}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error toggling pause: {ex.Message}");
        }
    }

    private void ReloadHotkey()
    {
        if (this.IsHandleCreated && this.InvokeRequired)
        {
            this.BeginInvoke(new Action(ReloadHotkey));
            return;
        }
        _hotkeyManager?.ReloadHotKey();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Program.OnSettingsChanged -= ReloadHotkey;
            trayIcon?.Dispose();
            _hotkeyManager?.UnregisterHotKey();
            WebGui.Stop();
        }
        base.Dispose(disposing);
    }

    private void ShowSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:49191",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open web configuration: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        Application.Exit();
    }
}
#endif