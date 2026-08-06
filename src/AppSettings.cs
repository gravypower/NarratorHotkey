using System;
using System.IO;
using System.Text.Json;

namespace NarratorHotkey;

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NarratorHotkey",
        "settings.json"
    );

    public string SelectedVoice { get; set; } = "Microsoft David Desktop"; // Default voice
    public int SpeechRate { get; set; } = 6; // Default rate
    public string TTSProvider { get; set; } = "Windows"; // "Windows" or "Piper"
    public string PiperVoice { get; set; } = "en_US-lessac-medium"; // Default Piper voice
    public string WindowsNaturalVoice { get; set; } = ""; // Default Windows Natural voice
    public string KokoroVoice { get; set; } = "af_heart"; // Default Kokoro voice
    public string HotkeyModifier { get; set; } = "Control"; // "Control", "Alt", "Shift", "None"
    public string HotkeyKey { get; set; } = "2"; // Default key
    public string PauseHotkeyModifier { get; set; } = "Control"; // Pause/resume hotkey
    public string PauseHotkeyKey { get; set; } = "3";
    public bool EnableProgressiveChunking { get; set; } = true;


    public void Save()
    {
        var dirPath = Path.GetDirectoryName(SettingsPath);
        if (!Directory.Exists(dirPath))
        {
            Directory.CreateDirectory(dirPath!);
        }

        var jsonString = JsonSerializer.Serialize(this);
        File.WriteAllText(SettingsPath, jsonString);
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var jsonString = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(jsonString)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(jsonString) ?? new AppSettings();

            // Migrate "Windows Natural" to "Windows"
            if (settings.TTSProvider == "Windows Natural")
            {
                settings.TTSProvider = "Windows";
                if (!string.IsNullOrEmpty(settings.WindowsNaturalVoice))
                {
                    settings.SelectedVoice = settings.WindowsNaturalVoice;
                }
            }
            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Reload()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var jsonString = File.ReadAllText(SettingsPath);
                if (!string.IsNullOrWhiteSpace(jsonString))
                {
                    var newSettings = JsonSerializer.Deserialize<AppSettings>(jsonString);
                    if (newSettings != null)
                    {
                        this.SelectedVoice = newSettings.SelectedVoice;
                        this.SpeechRate = newSettings.SpeechRate;
                        this.TTSProvider = newSettings.TTSProvider;
                        this.PiperVoice = newSettings.PiperVoice;
                        this.WindowsNaturalVoice = newSettings.WindowsNaturalVoice;
                        this.KokoroVoice = newSettings.KokoroVoice;
                        this.HotkeyModifier = newSettings.HotkeyModifier;
                        this.HotkeyKey = newSettings.HotkeyKey;
                        this.PauseHotkeyModifier = newSettings.PauseHotkeyModifier;
                        this.PauseHotkeyKey = newSettings.PauseHotkeyKey;
                        this.EnableProgressiveChunking = newSettings.EnableProgressiveChunking;

                        // Migrate "Windows Natural" to "Windows"
                        if (this.TTSProvider == "Windows Natural")
                        {
                            this.TTSProvider = "Windows";
                            if (!string.IsNullOrEmpty(this.WindowsNaturalVoice))
                            {
                                this.SelectedVoice = this.WindowsNaturalVoice;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reloading settings: {ex.Message}");
        }
    }
}