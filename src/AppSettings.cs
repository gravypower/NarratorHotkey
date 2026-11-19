using System;
using System.IO;
using System.Text.Json;

namespace NarratorHotkey;

public class AppSettings
{
    public const int MinSpeechRate = -10;
    public const int MaxSpeechRate = 10;

    /// <summary>
    /// The TTS providers this build can speak with, under the names stored in settings.
    /// </summary>
    public static readonly string[] ProviderNames =
    {
        "Kokoro ONNX",
        "Piper",
#if WINDOWS
        "Windows",
#endif
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NarratorHotkey",
        "settings.json"
    );

    public string SelectedVoice { get; set; } = "Microsoft David Desktop"; // Default voice
    public int SpeechRate { get; set; } = 6; // Default rate
    public string TTSProvider { get; set; } = "Windows"; // "Windows", "Piper" or "Kokoro ONNX"
    public string PiperVoice { get; set; } = "en_US-lessac-medium"; // Default Piper voice
    public string WindowsNaturalVoice { get; set; } = ""; // Default Windows Natural voice
    public string KokoroVoice { get; set; } = "af_heart"; // Default Kokoro voice
    public string HotkeyModifier { get; set; } = "Control"; // "Control", "Alt", "Shift", "None"
    public string HotkeyKey { get; set; } = "2"; // Default key
    public string PauseHotkeyModifier { get; set; } = "Control"; // Pause/resume hotkey
    public string PauseHotkeyKey { get; set; } = "3";
    public bool EnableProgressiveChunking { get; set; } = true;

    /// <summary>
    /// Resolves a provider name typed by a person ("piper", "kokoro") to the name
    /// stored in settings, or null if this build has no such provider.
    /// </summary>
    public static string ResolveProviderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        foreach (string known in ProviderNames)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return known;
        }

        // "kokoro" for "Kokoro ONNX".
        foreach (string known in ProviderNames)
        {
            if (known.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) return known;
        }

        return null;
    }

    public void Save()
    {
        var dirPath = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dirPath);

        // Written beside the real file and moved over it. The CLI and the daemon both
        // read this file, and a reader that caught it half written would fall back to
        // defaults - and then save those defaults over the user's settings.
        string tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // A stray temp file is harmless.
            }
        }
    }

    public static AppSettings Load()
    {
        return TryRead() ?? new AppSettings();
    }

    /// <summary>
    /// Picks up the file as it is now. Keeps the current values if it cannot be read.
    /// </summary>
    public void Reload()
    {
        var newSettings = TryRead();
        if (newSettings == null) return;

        SelectedVoice = newSettings.SelectedVoice;
        SpeechRate = newSettings.SpeechRate;
        TTSProvider = newSettings.TTSProvider;
        PiperVoice = newSettings.PiperVoice;
        WindowsNaturalVoice = newSettings.WindowsNaturalVoice;
        KokoroVoice = newSettings.KokoroVoice;
        HotkeyModifier = newSettings.HotkeyModifier;
        HotkeyKey = newSettings.HotkeyKey;
        PauseHotkeyModifier = newSettings.PauseHotkeyModifier;
        PauseHotkeyKey = newSettings.PauseHotkeyKey;
        EnableProgressiveChunking = newSettings.EnableProgressiveChunking;
    }

    /// <summary>
    /// Reads and tidies the settings file. Null if there is none or it is unreadable.
    /// </summary>
    private static AppSettings TryRead()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var jsonString = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(jsonString)) return null;
            var settings = JsonSerializer.Deserialize<AppSettings>(jsonString);
            if (settings == null) return null;

            // Migrate "Windows Natural" to "Windows"
            if (settings.TTSProvider == "Windows Natural")
            {
                settings.TTSProvider = "Windows";
                if (!string.IsNullOrEmpty(settings.WindowsNaturalVoice))
                {
                    settings.SelectedVoice = settings.WindowsNaturalVoice;
                }
            }

            // A hand-edited file can hold anything; SAPI throws on a rate out of range.
            settings.SpeechRate = Math.Clamp(settings.SpeechRate, MinSpeechRate, MaxSpeechRate);
            return settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings: {ex.Message}");
            return null;
        }
    }
}
