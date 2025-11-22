using System;
using System.Linq;
using System.Speech.Synthesis;

namespace NarratorHotkey.Speech
{
    public class SpeechManager
    {
        private const int MaxTextLength = 5000;
        private static SpeechManager _instance;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly AppSettings _settings;

        public static SpeechManager Instance => _instance ??= new SpeechManager();

        private SpeechManager()
        {
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _settings = AppSettings.Load();

            try
            {
                ApplySettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply settings during initialization: {ex.Message}");
            }
        }


        public void ApplySettings()
        {
            _settings.Reload();

            var installedVoices = _synthesizer.GetInstalledVoices();
            if (installedVoices.Count == 0)
            {
                Console.WriteLine("No voices installed on the system.");
                return;
            }

            // Try to select the configured voice
            try
            {
                _synthesizer.SelectVoice(_settings.SelectedVoice);
            }
            catch (ArgumentException)
            {
                // Voice doesn't exist or was uninstalled, fallback to first available
                try
                {
                    _synthesizer.SelectVoice(installedVoices[0].VoiceInfo.Name);
                    Console.WriteLine($"Selected voice fallback: {installedVoices[0].VoiceInfo.Name}");
                }
                catch (ArgumentException ex)
                {
                    Console.WriteLine($"Failed to select fallback voice: {ex.Message}");
                }
            }

            _synthesizer.Rate = _settings.SpeechRate;
        }
        
        public void Speak(string text)
        {
            if (_synthesizer.State == SynthesizerState.Speaking)
            {
                _synthesizer.SpeakAsyncCancelAll();
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Truncate text if it exceeds maximum length
                var textToSpeak = text.Length > MaxTextLength
                    ? text.Substring(0, MaxTextLength)
                    : text;

                _synthesizer.SpeakAsync(textToSpeak);
            }
        }
    }
}