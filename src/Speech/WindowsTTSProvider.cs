using System;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// TTS provider using Windows built-in text-to-speech (combining SAPI5 System.Speech and WinRT SpeechSynthesizer).
    /// </summary>
    public class WindowsTTSProvider : ITTSProvider
    {
        private const int MaxTextLength = 5000;
        private readonly SpeechSynthesizer _sapiSynthesizer;
        private Windows.Media.SpeechSynthesis.SpeechSynthesizer _winrtSynthesizer;
        private readonly AppSettings _settings;
        private readonly System.Collections.Generic.Dictionary<Prompt, TaskCompletionSource<bool>> _pendingPrompts = new();
        private readonly object _lock = new object();

        // WinRT playback state
        private System.Threading.CancellationTokenSource _playTokenSource;
        private PlaybackSession _playbackSession;
        private bool _isSpeakingWinRT = false;
        private bool _useWinRT = false;

        public WindowsTTSProvider(AppSettings settings)
        {
            _settings = settings;
            _sapiSynthesizer = new SpeechSynthesizer();
            _sapiSynthesizer.SetOutputToDefaultAudioDevice();
            _sapiSynthesizer.SpeakCompleted += Synthesizer_SpeakCompleted;

            try
            {
                _winrtSynthesizer = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize WinRT SpeechSynthesizer: {ex.Message}");
            }
        }

        public bool IsSpeaking
        {
            get
            {
                if (_useWinRT)
                {
                    return _isSpeakingWinRT;
                }

                // A paused synthesizer reports Paused rather than Speaking, so the
                // prompts still in flight are what say whether an utterance is running.
                if (_sapiSynthesizer.State == SynthesizerState.Paused)
                {
                    lock (_lock)
                    {
                        return _pendingPrompts.Count > 0;
                    }
                }

                return _sapiSynthesizer.State == SynthesizerState.Speaking;
            }
        }

        public bool IsPaused
        {
            get
            {
                if (_useWinRT)
                {
                    lock (_lock)
                    {
                        return _isSpeakingWinRT && (_playbackSession?.IsPaused ?? false);
                    }
                }

                lock (_lock)
                {
                    return _sapiSynthesizer.State == SynthesizerState.Paused && _pendingPrompts.Count > 0;
                }
            }
        }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Cancel any ongoing speech first
            await StopAsync();

            if (_useWinRT && _winrtSynthesizer != null)
            {
                System.Threading.CancellationTokenSource tokenSource;
                PlaybackSession session;
                lock (_lock)
                {
                    tokenSource = _playTokenSource = new System.Threading.CancellationTokenSource();
                    session = _playbackSession = new PlaybackSession();
                    _isSpeakingWinRT = true;
                }

                try
                {
                    var synthesisStream = await _winrtSynthesizer.SynthesizeTextToStreamAsync(text);

                    if (tokenSource.Token.IsCancellationRequested)
                        return;

                    byte[] audioData;
                    using (var netStream = synthesisStream.AsStreamForRead())
                    using (var ms = new MemoryStream())
                    {
                        await netStream.CopyToAsync(ms, tokenSource.Token);
                        audioData = ms.ToArray();
                    }

                    if (tokenSource.Token.IsCancellationRequested)
                        return;

                    await AudioPlayer.PlayWavAsync(audioData, tokenSource.Token, session);
                }
                catch (TaskCanceledException)
                {
                    // Normal cancellation via StopAsync
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during WinRT Speech synthesis: {ex.Message}");
                }
                finally
                {
                    lock (_lock)
                    {
                        _isSpeakingWinRT = false;
                        if (ReferenceEquals(_playbackSession, session))
                        {
                            _playbackSession = null;
                        }
                    }
                }
            }
            else
            {
                var textToSpeak = text.Length > MaxTextLength
                    ? text.Substring(0, MaxTextLength)
                    : text;

                var tcs = new TaskCompletionSource<bool>();
                Prompt prompt;

                lock (_lock)
                {
                    prompt = _sapiSynthesizer.SpeakAsync(textToSpeak);
                    _pendingPrompts[prompt] = tcs;
                }

                await tcs.Task;
            }
        }

        public Task StopAsync()
        {
            lock (_lock)
            {
                // SAPI stop
                foreach (var tcs in _pendingPrompts.Values)
                {
                    tcs.TrySetCanceled();
                }
                _pendingPrompts.Clear();
                try
                {
                    // A synthesizer left paused stays paused, and the next prompt would
                    // sit in the queue in silence.
                    if (_sapiSynthesizer.State == SynthesizerState.Paused)
                    {
                        _sapiSynthesizer.Resume();
                    }
                    _sapiSynthesizer.SpeakAsyncCancelAll();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping SAPI speech: {ex.Message}");
                }

                // WinRT stop
                _isSpeakingWinRT = false;
                _playbackSession = null;
                _playTokenSource?.Cancel();
            }
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            lock (_lock)
            {
                if (_useWinRT)
                {
                    if (_isSpeakingWinRT)
                    {
                        _playbackSession?.Pause();
                    }
                    return Task.CompletedTask;
                }

                try
                {
                    if (_sapiSynthesizer.State == SynthesizerState.Speaking)
                    {
                        _sapiSynthesizer.Pause();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to pause SAPI speech: {ex.Message}");
                }
            }
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            lock (_lock)
            {
                if (_useWinRT)
                {
                    _playbackSession?.Resume();
                    return Task.CompletedTask;
                }

                try
                {
                    if (_sapiSynthesizer.State == SynthesizerState.Paused)
                    {
                        _sapiSynthesizer.Resume();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to resume SAPI speech: {ex.Message}");
                }
            }
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
            var sapiVoices = _sapiSynthesizer.GetInstalledVoices()
                .Select(v => v.VoiceInfo.Name)
                .ToList();

            var winrtVoices = Array.Empty<string>();
            try
            {
                winrtVoices = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .Select(v => v.DisplayName)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get WinRT voices: {ex.Message}");
            }

            return Task.FromResult(sapiVoices.Concat(winrtVoices).Distinct().ToArray());
        }

        public Task SelectVoiceAsync(string voiceName)
        {
            if (string.IsNullOrEmpty(voiceName))
            {
                return Task.CompletedTask;
            }

            bool isWinRT = false;
            try
            {
                isWinRT = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .Any(v => v.DisplayName == voiceName);
            }
            catch { }

            if (isWinRT && _winrtSynthesizer != null)
            {
                _useWinRT = true;
                try
                {
                    var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v => v.DisplayName == voiceName);
                    if (voice != null)
                    {
                        _winrtSynthesizer.Voice = voice;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to select WinRT voice '{voiceName}': {ex.Message}");
                }
            }
            else
            {
                _useWinRT = false;
                try
                {
                    _sapiSynthesizer.SelectVoice(voiceName);
                }
                catch (ArgumentException ex)
                {
                    Console.WriteLine($"Failed to select SAPI voice '{voiceName}': {ex.Message}");
                    // Fall back to first available SAPI voice
                    var voices = _sapiSynthesizer.GetInstalledVoices();
                    if (voices.Count > 0)
                    {
                        _sapiSynthesizer.SelectVoice(voices[0].VoiceInfo.Name);
                        Console.WriteLine($"Fallback to voice: {voices[0].VoiceInfo.Name}");
                    }
                }
            }

            return Task.CompletedTask;
        }

        public void SetRate(int rate)
        {
            // SAPI rate (typically -10 to 10)
            try
            {
                _sapiSynthesizer.Rate = rate;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set SAPI rate: {ex.Message}");
            }

            // WinRT rate
            if (_winrtSynthesizer != null)
            {
                double speakingRate = 1.0;
                if (rate < 0)
                {
                    speakingRate = 1.0 + (rate * 0.05);
                }
                else if (rate > 0)
                {
                    speakingRate = 1.0 + (rate * 0.1);
                }

                try
                {
                    _winrtSynthesizer.Options.SpeakingRate = Math.Max(0.5, Math.Min(6.0, speakingRate));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to set WinRT SpeakingRate: {ex.Message}");
                }
            }
        }

        public string GetProviderName() => "Windows";
    }
}
