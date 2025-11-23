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
        if (!File.Exists(SettingsPath)) return new AppSettings();
        var jsonString = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(jsonString) ?? new AppSettings();

    }
    public void Reload()
    {
        if (File.Exists(SettingsPath))
        {
            var jsonString = File.ReadAllText(SettingsPath);
            var newSettings = JsonSerializer.Deserialize<AppSettings>(jsonString);
            if (newSettings != null)
            {
                this.SelectedVoice = newSettings.SelectedVoice;
                this.SpeechRate = newSettings.SpeechRate;
                this.TTSProvider = newSettings.TTSProvider;
                this.PiperVoice = newSettings.PiperVoice;
            }
        }
    }
}