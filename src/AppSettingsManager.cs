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

    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;

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
}
