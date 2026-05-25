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
        private readonly System.Collections.Generic.Dictionary<Prompt, TaskCompletionSource<bool>> _pendingPrompts = new();
        private readonly object _lock = new object();

        public WindowsTTSProvider(AppSettings settings)
        {
            _settings = settings;
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.SpeakCompleted += Synthesizer_SpeakCompleted;
        }

        public bool IsSpeaking => _synthesizer.State == SynthesizerState.Speaking;

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Cancel any ongoing speech first
            await StopAsync();

            var textToSpeak = text.Length > MaxTextLength
                ? text.Substring(0, MaxTextLength)
                : text;

            var tcs = new TaskCompletionSource<bool>();
            Prompt prompt;

            lock (_lock)
            {
                prompt = _synthesizer.SpeakAsync(textToSpeak);
                _pendingPrompts[prompt] = tcs;
            }

            await tcs.Task;
        }

        public Task StopAsync()
        {
            lock (_lock)
            {
                foreach (var tcs in _pendingPrompts.Values)
                {
                    tcs.TrySetCanceled();
                }
                _pendingPrompts.Clear();
            }
            _synthesizer.SpeakAsyncCancelAll();
            return Task.CompletedTask;
        }

        private void Synthesizer_SpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            TaskCompletionSource<bool> tcs = null;

            lock (_lock)
            {
                if (_pendingPrompts.TryGetValue(e.Prompt, out tcs))
                {
                    _pendingPrompts.Remove(e.Prompt);
                }
            }

            if (tcs != null)
            {
                if (e.Error != null)
                {
                    tcs.TrySetException(e.Error);
                }
                else if (e.Cancelled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            }
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
