using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// TTS provider using Windows built-in text-to-speech (System.Speech).
    /// </summary>
    public class WindowsTTSProvider : ITTSProvider
    {
        private const int MaxTextLength = 5000;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly AppSettings _settings;

        public WindowsTTSProvider(AppSettings settings)
        {
            _settings = settings;
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
        }

        public Task SpeakAsync(string text)
        {
            return Task.Run(() =>
            {
                if (_synthesizer.State == SynthesizerState.Speaking)
                {
                    _synthesizer.SpeakAsyncCancelAll();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    var textToSpeak = text.Length > MaxTextLength
                        ? text.Substring(0, MaxTextLength)
                        : text;

                    _synthesizer.SpeakAsync(textToSpeak);
                }
            });
        }

        public Task StopAsync()
        {
            return Task.Run(() =>
            {
                _synthesizer.SpeakAsyncCancelAll();
            });
        }

        public Task<string[]> GetAvailableVoicesAsync()
        {
            return Task.FromResult(
                _synthesizer.GetInstalledVoices()
                    .Select(v => v.VoiceInfo.Name)
                    .ToArray()
            );
        }

        public Task SelectVoiceAsync(string voiceName)
        {
            return Task.Run(() =>
            {
                try
                {
                    _synthesizer.SelectVoice(voiceName);
                }
                catch (ArgumentException ex)
                {
                    Console.WriteLine($"Failed to select voice '{voiceName}': {ex.Message}");
                    // Fall back to first available voice
                    var voices = _synthesizer.GetInstalledVoices();
                    if (voices.Count > 0)
                    {
                        _synthesizer.SelectVoice(voices[0].VoiceInfo.Name);
                        Console.WriteLine($"Fallback to voice: {voices[0].VoiceInfo.Name}");
                    }
                }
            });
        }

        public void SetRate(int rate)
        {
            _synthesizer.Rate = rate;
        }

        public string GetProviderName() => "Windows TTS";
    }
}
