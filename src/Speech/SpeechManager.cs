using System;
using System.Collections.Generic;
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

        private SpeechManager()
        {
            _settings = AppSettings.Load();
            _providers = new Dictionary<string, ITTSProvider>();

            // Initialize providers
            InitializeProviders();

            try
            {
                ApplySettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply settings during initialization: {ex.Message}");
            }
        }

        private void InitializeProviders()
        {
            _providers["Windows"] = new WindowsTTSProvider(_settings);
            _providers["Piper"] = new PiperTTSProvider(_settings);
        }

        public void ApplySettings()
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
                _currentProvider.SelectVoiceAsync(_settings.SelectedVoice).Wait();
            }
            else if (providerName == "Piper")
            {
                _currentProvider.SelectVoiceAsync(_settings.PiperVoice).Wait();
            }

            Console.WriteLine($"Using TTS Provider: {_currentProvider.GetProviderName()}");
        }

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Use fire-and-forget pattern for sync context
            _ = _currentProvider.SpeakAsync(text);
        }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await _currentProvider.SpeakAsync(text);
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