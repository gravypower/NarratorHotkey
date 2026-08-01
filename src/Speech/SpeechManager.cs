using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    public class SpeechLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string OriginalText { get; set; }
        public string CleanedText { get; set; }
        public string Provider { get; set; }
    }

    public class SpeechManager
    {
        private static SpeechManager _instance;
        private readonly AppSettings _settings;
        private ITTSProvider _currentProvider;
        private Dictionary<string, ITTSProvider> _providers;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NarratorHotkey",
            "speech_log.json"
        );

        private readonly List<SpeechLogEntry> _speechHistory = new List<SpeechLogEntry>();
        private readonly object _logLock = new object();
        private readonly Task _initTask;

        public IReadOnlyList<SpeechLogEntry> SpeechHistory
        {
            get
            {
                lock (_logLock)
                {
                    return _speechHistory.ToArray();
                }
            }
        }

        private static readonly object _instanceLock = new object();
        public static SpeechManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new SpeechManager();
                    }
                }
                return _instance;
            }
        }

        public ITTSProvider CurrentProvider => GetActiveProvider();
        public bool IsSpeaking => GetActiveProvider()?.IsSpeaking ?? false;

        private ITTSProvider GetActiveProvider()
        {
            if (_initTask != null && !_initTask.IsCompleted)
            {
                try
                {
                    // Block briefly to let settings load, max 3 seconds
                    _initTask.Wait(3000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error waiting for settings initialization: {ex.Message}");
                }
            }

            if (_currentProvider != null)
            {
                return _currentProvider;
            }

#if WINDOWS
            // Fallback: try to select Windows TTS provider
            if (_providers != null && _providers.TryGetValue("Windows", out var winProvider))
            {
                return winProvider;
            }

            // Ultimate fallback: instantiate Windows TTS on the fly
            try
            {
                var fallback = new WindowsTTSProvider(_settings ?? AppSettings.Load());
                return fallback;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ultimate fallback failed to instantiate: {ex.Message}");
                return null;
            }
#else
            // Linux/cross-platform fallback
            if (_providers != null)
            {
                if (_providers.TryGetValue("Kokoro ONNX", out var kokoro)) return kokoro;
                if (_providers.TryGetValue("Piper", out var piper)) return piper;
            }
            return null;
#endif
        }

        private async Task<ITTSProvider> GetActiveProviderAsync()
        {
            if (_initTask != null)
            {
                try
                {
                    await _initTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error awaiting settings initialization: {ex.Message}");
                }
            }

            return GetActiveProvider();
        }

        private SpeechManager()
        {
            _settings = AppSettings.Load();
            _providers = new Dictionary<string, ITTSProvider>();

            // Initialize providers
            InitializeProviders();

            // Load persisted speech log
            LoadSpeechLog();

            // Initialize settings in background thread to avoid blocking UI during startup
            _initTask = Task.Run(async () =>
            {
                try
                {
                    await ApplySettingsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to apply settings during initialization: {ex.Message}");
                }
            });
        }

        private void LoadSpeechLog()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    string json = File.ReadAllText(LogPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var list = JsonSerializer.Deserialize<List<SpeechLogEntry>>(json);
                        if (list != null)
                        {
                            lock (_logLock)
                            {
                                _speechHistory.Clear();
                                foreach (var entry in list)
                                {
                                    if (entry != null)
                                    {
                                        _speechHistory.Add(entry);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading speech log: {ex.Message}");
            }
        }

        private void SaveSpeechLog()
        {
            try
            {
                string json;
                lock (_logLock)
                {
                    json = JsonSerializer.Serialize(_speechHistory);
                }
                string dir = Path.GetDirectoryName(LogPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(LogPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving speech log: {ex.Message}");
            }
        }

        public void AddLogEntry(string original, string cleaned, string provider)
        {
            if (string.IsNullOrWhiteSpace(original))
                return;

            lock (_logLock)
            {
                _speechHistory.Insert(0, new SpeechLogEntry
                {
                    Timestamp = DateTime.Now,
                    OriginalText = original,
                    CleanedText = cleaned ?? string.Empty,
                    Provider = provider ?? "Unknown"
                });

                // Limit history to 200 entries
                while (_speechHistory.Count > 200)
                {
                    _speechHistory.RemoveAt(_speechHistory.Count - 1);
                }
            }

            Task.Run(() => SaveSpeechLog());
        }

        public void ClearSpeechLog()
        {
            lock (_logLock)
            {
                _speechHistory.Clear();
            }
            try
            {
                if (File.Exists(LogPath))
                {
                    File.Delete(LogPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting speech log file: {ex.Message}");
            }
        }

        private void InitializeProviders()
        {
#if WINDOWS
            _providers["Windows"] = new WindowsTTSProvider(_settings);
#endif
            _providers["Kokoro ONNX"] = new KokoroTTSProvider(_settings);
            _providers["Piper"] = new PiperTTSProvider(_settings);
        }

        public void ApplySettings()
        {
            try
            {
                ApplySettingsAsync().Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying settings synchronously: {ex.Message}");
            }
        }

        public async Task ApplySettingsAsync()
        {
            _settings.Reload();

            // Select the appropriate provider
            string providerName = _settings.TTSProvider ?? "Windows";
#if !WINDOWS
            if (providerName == "Windows" || !_providers.ContainsKey(providerName))
            {
                providerName = "Kokoro ONNX";
            }
#else
            if (!_providers.ContainsKey(providerName))
            {
                Console.WriteLine($"Provider '{providerName}' not found. Falling back to Windows.");
                providerName = "Windows";
            }
#endif

            _currentProvider = _providers[providerName];
            _currentProvider.SetRate(_settings.SpeechRate);

            // Select the appropriate voice
#if WINDOWS
            if (providerName == "Windows")
            {
                await _currentProvider.SelectVoiceAsync(_settings.SelectedVoice);
            }
            else
#endif
            if (providerName == "Kokoro ONNX")
            {
                await _currentProvider.SelectVoiceAsync(_settings.KokoroVoice);
            }
            else if (providerName == "Piper")
            {
                await _currentProvider.SelectVoiceAsync(_settings.PiperVoice);
            }

            Console.WriteLine($"Using TTS Provider: {_currentProvider.GetProviderName()}");
        }

        public async Task ApplyTemporarySettingsAsync(string providerName, string voiceName)
        {
            if (string.IsNullOrEmpty(providerName) || !_providers.ContainsKey(providerName))
            {
                return;
            }

            _currentProvider = _providers[providerName];
            _currentProvider.SetRate(_settings.SpeechRate);

            if (!string.IsNullOrEmpty(voiceName))
            {
                await _currentProvider.SelectVoiceAsync(voiceName);
            }

            Console.WriteLine($"Using temporary TTS Provider: {_currentProvider.GetProviderName()} and voice: {voiceName}");
        }

        public static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                // 1. Join words split across lines with a hyphen (e.g., auto-\r\nmotive -> automotive)
                string result = Regex.Replace(text, @"(\w+)-\s*\r?\n\s*(\w+)", "$1$2");

                // 2. Join words split across lines without a hyphen and without spaces (e.g., sys\r\ntems -> systems)
                result = Regex.Replace(result, @"(\w)\r?\n(\w)", "$1$2");

                // 3. Replace any remaining newlines/tabs with a space to keep speech continuous
                result = Regex.Replace(result, @"[\r\n\t]+", " ");

                // 4. Break file names and paths into speakable words (e.g. src/Speech/SpeechManager.cs ->
                //    src slash Speech slash SpeechManager cs). The dot is a full stop but it is not
                //    the end of a sentence, and left in place the phonemizer tries to say the whole
                //    path as one token. Also catches extensions the rule below misses (.7z).
                result = TextNormalizer.NormalizeFileReferences(result);

                // 5. Break any remaining dot notation (e.g. SpeechManager.Instance -> SpeechManager Instance)
                //    to prevent TTS freakouts. The dot becomes a word break; speaking it as "dot"
                //    mid-sentence sounds like a full stop.
                result = Regex.Replace(result, @"\.([a-zA-Z])", " $1");

                // 6. Split CamelCase words (e.g. SpeechManager -> Speech Manager) so they are read as separate words
                result = Regex.Replace(result, @"([a-z])([A-Z])", "$1 $2");

                // 7. Replace hyphens between letters/digits with a space to keep speech natural (e.g. text-to-speech -> text to speech)
                result = Regex.Replace(result, @"(?<=[a-zA-Z0-9])-(?=[a-zA-Z0-9])", " ");

                // 8. Spell out consonant-only abbreviations/extensions (e.g. cs -> c s, ng -> n g, dll -> d l l)
                result = Regex.Replace(result, @"\b[bcdfghjklmnpqrstvwxzBCDFGHJKLMNPQRSTVWXZ]{2,}\b", 
                    m => string.Join(" ", m.Value.ToCharArray()));

                // 9. Replace underscores with spaces so code variables are read naturally
                result = result.Replace("_", " ");

                // 10. Remove control characters and zero-width/formatting characters
                var sb = new StringBuilder(result.Length);
                foreach (char c in result)
                {
                    if (!char.IsControl(c))
                    {
                        var category = CharUnicodeInfo.GetUnicodeCategory(c);
                        if (category != UnicodeCategory.Format &&
                            c != '\u200B' && c != '\u200C' && c != '\u200D' && c != '\uFEFF')
                        {
                            sb.Append(c);
                        }
                    }
                }
                result = sb.ToString();

                // 11. Collapse long repeating patterns of decorative dividers (e.g. -----------, __________, **********)
                result = Regex.Replace(result, @"([-_=*~#+]{3,})", " ");

                // 12. Remove emojis and high surrogate characters (which cause TTS crashes or raw hex speak)
                result = Regex.Replace(result, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", " ");

                // 13. Escape/replace XML-unsafe delimiters to prevent System.Speech SSML interpretation errors
                result = result.Replace("<", " less than ")
                               .Replace(">", " greater than ")
                               .Replace("&", " and ");

                // 14. Clean up excessive whitespace
                result = Regex.Replace(result, @"\s+", " ").Trim();

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning text: {ex.Message}");
                return text; // Fallback to original text if sanitization fails
            }
        }

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string cleanedText = CleanText(text);
            if (string.IsNullOrWhiteSpace(cleanedText))
                return;

            var provider = GetActiveProvider();
            AddLogEntry(text, cleanedText, provider?.GetProviderName() ?? "Unknown");

            if (provider != null)
            {
                // Use fire-and-forget safely
                _ = provider.SpeakAsync(cleanedText);
            }
        }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string cleanedText = CleanText(text);
            if (string.IsNullOrWhiteSpace(cleanedText))
                return;

            var provider = await GetActiveProviderAsync();
            AddLogEntry(text, cleanedText, provider?.GetProviderName() ?? "Unknown");

            if (provider != null)
            {
                await provider.SpeakAsync(cleanedText);
            }
        }

        public async Task StopAsync()
        {
            var provider = GetActiveProvider();
            if (provider != null)
            {
                await provider.StopAsync();
            }
        }

        public async Task<string[]> GetAvailableVoicesAsync()
        {
            var provider = await GetActiveProviderAsync();
            if (provider != null)
            {
                return await provider.GetAvailableVoicesAsync();
            }
            return new string[0];
        }

        public async Task<string[]> GetVoicesForProviderAsync(string providerName)
        {
            await GetActiveProviderAsync();
            if (_providers.ContainsKey(providerName))
            {
                return await _providers[providerName].GetAvailableVoicesAsync();
            }
            return new string[] { };
        }

        public ITTSProvider GetProviderByName(string name)
        {
            if (_providers.ContainsKey(name))
                return _providers[name];
            return null;
        }

        public string GetCurrentProvider()
        {
            return GetActiveProvider()?.GetProviderName() ?? "Unknown";
        }
    }
}