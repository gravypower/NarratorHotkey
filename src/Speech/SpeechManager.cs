using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    public class SpeechManager
    {
        private static SpeechManager _instance;
        private readonly AppSettings _settings;
        private ITTSProvider _currentProvider;
        private Dictionary<string, ITTSProvider> _providers;

        public static SpeechManager Instance => _instance ??= new SpeechManager();
        public ITTSProvider CurrentProvider => _currentProvider;
        public bool IsSpeaking => _currentProvider?.IsSpeaking ?? false;

        private SpeechManager()
        {
            _settings = AppSettings.Load();
            _providers = new Dictionary<string, ITTSProvider>();

            // Initialize providers
            InitializeProviders();

            // Initialize settings in background thread to avoid blocking UI during startup
            Task.Run(async () =>
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

        private void InitializeProviders()
        {
            _providers["Windows"] = new WindowsTTSProvider(_settings);
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
            if (!_providers.ContainsKey(providerName))
            {
                Console.WriteLine($"Provider '{providerName}' not found. Falling back to Windows TTS.");
                providerName = "Windows";
            }

            _currentProvider = _providers[providerName];
            _currentProvider.SetRate(_settings.SpeechRate);

            // Select the appropriate voice
            if (providerName == "Windows")
            {
                await _currentProvider.SelectVoiceAsync(_settings.SelectedVoice);
            }
            else if (providerName == "Piper")
            {
                await _currentProvider.SelectVoiceAsync(_settings.PiperVoice);
            }

            Console.WriteLine($"Using TTS Provider: {_currentProvider.GetProviderName()}");
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

                // 4. Translate dots followed immediately by letters (e.g. .cs -> dot cs, SpeechManager.cs -> SpeechManager dot cs) to prevent TTS freakouts
                result = Regex.Replace(result, @"\.([a-zA-Z])", " dot $1");

                // 5. Replace underscores with spaces so code variables are read naturally
                result = result.Replace("_", " ");

                // 6. Remove control characters and zero-width/formatting characters
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

                // 7. Collapse long repeating patterns of decorative dividers (e.g. -----------, __________, **********)
                result = Regex.Replace(result, @"([-_=*~#+]{3,})", " ");

                // 8. Remove emojis and high surrogate characters (which cause TTS crashes or raw hex speak)
                result = Regex.Replace(result, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", " ");

                // 9. Escape/replace XML-unsafe delimiters to prevent System.Speech SSML interpretation errors
                result = result.Replace("<", " less than ")
                               .Replace(">", " greater than ")
                               .Replace("&", " and ");

                // 10. Clean up excessive whitespace
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

            // Use fire-and-forget pattern for sync context
            _ = _currentProvider.SpeakAsync(cleanedText);
        }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string cleanedText = CleanText(text);
            if (string.IsNullOrWhiteSpace(cleanedText))
                return;

            await _currentProvider.SpeakAsync(cleanedText);
        }

        public async Task StopAsync()
        {
            await _currentProvider.StopAsync();
        }

        public async Task<string[]> GetAvailableVoicesAsync()
        {
            return await _currentProvider.GetAvailableVoicesAsync();
        }

        public async Task<string[]> GetVoicesForProviderAsync(string providerName)
        {
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
            return _currentProvider?.GetProviderName() ?? "Unknown";
        }
    }
}