using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SherpaOnnx;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// TTS provider using Kokoro ONNX model via sherpa-onnx, supporting low-latency progressive chunking.
    /// </summary>
    public class KokoroTTSProvider : ITTSProvider
    {
        private readonly AppSettings _settings;
        private string _kokoroDir;
        private string _modelDir;
        private string _modelPath;
        private OfflineTts _tts;
        private string _currentVoiceName = "af_heart";
        private float _currentRate = 1.0f;
        private volatile bool _isInitialized = false;
        private bool _isSpeaking = false;
        private System.Threading.CancellationTokenSource _playTokenSource;
        private PlaybackSession _playbackSession;
        private readonly object _lock = new object();
        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);

        private static readonly string[] PresetVoices = new[]
        {
            "af_heart",
            "af_bella",
            "af_nicole",
            "af_sarah",
            "af_sky",
            "am_adam",
            "am_michael",
            "bf_emma",
            "bf_isabella",
            "bm_george",
            "bm_lewis"
        };

        public bool IsSpeaking => _isSpeaking;

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _isSpeaking && (_playbackSession?.IsPaused ?? false);
                }
            }
        }

        public event Action<string> ProgressChanged;
        private void ReportProgress(string message)
        {
            Console.WriteLine($"[Kokoro] {message}");
            ProgressChanged?.Invoke(message);
        }

        public KokoroTTSProvider(AppSettings settings)
        {
            _settings = settings;
            _currentVoiceName = string.IsNullOrEmpty(settings.KokoroVoice) ? "af_heart" : settings.KokoroVoice;
        }

        public string GetProviderName() => "Kokoro ONNX";

        private async Task EnsureInitializedAsync()
        {
            if (_isInitialized && _tts != null) return;

            // Initialization downloads and extracts the model and builds the engine;
            // two concurrent runs would fight over the same files.
            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized && _tts != null) return;
                await InitializeAsync();
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                _kokoroDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NarratorHotkey",
                    "Kokoro"
                );

                if (!Directory.Exists(_kokoroDir))
                {
                    Directory.CreateDirectory(_kokoroDir);
                }

                _modelDir = Path.Combine(_kokoroDir, "kokoro-en-v0_19");
                _modelPath = Path.Combine(_modelDir, "model.onnx");

                if (!File.Exists(_modelPath))
                {
                    ReportProgress("Downloading Kokoro ONNX model files (approx. 80MB)...");
                    await DownloadAndExtractModelAsync();
                }

                // Initialize SherpaOnnx OfflineTts
                ReportProgress("Initializing Kokoro ONNX engine...");
                await Task.Run(() =>
                {
                    var config = new OfflineTtsConfig();
                    config.Model.Kokoro.Model = _modelPath;
                    config.Model.Kokoro.Voices = Path.Combine(_modelDir, "voices.bin");
                    config.Model.Kokoro.Tokens = Path.Combine(_modelDir, "tokens.txt");
                    config.Model.Kokoro.DataDir = Path.Combine(_modelDir, "espeak-ng-data");
                    config.Model.Kokoro.LengthScale = 1.0f;
                    
                    config.Model.NumThreads = 2;
                    config.Model.Provider = "cpu";

                    _tts = new OfflineTts(config);
                });

                _isInitialized = true;
                ReportProgress("Kokoro ONNX initialized successfully");
            }
            catch (Exception ex)
            {
                ReportProgress($"Failed to initialize Kokoro: {ex.Message}");
                _isInitialized = false;
                throw;
            }
        }

        private async Task DownloadAndExtractModelAsync()
        {
            string downloadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2";
            string archivePath = Path.Combine(_kokoroDir, "kokoro-en-v0_19.tar.bz2");

            // Download using HttpClient
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                ReportProgress("Downloading model archive...");
                using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
            }

            ReportProgress("Extracting model archive (tar.bz2)...");
            await Task.Run(() =>
            {
                try
                {
                    // A forward-only reader: SharpCompress's archive API cannot open a
                    // compressed tar, only the streaming one can.
                    using (var stream = File.OpenRead(archivePath))
                    using (var reader = ReaderFactory.OpenReader(stream, new ReaderOptions()))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            if (!reader.Entry.IsDirectory)
                            {
                                reader.WriteEntryToDirectory(_kokoroDir, new ExtractionOptions()
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to extract model archive using SharpCompress: {ex.Message}", ex);
                }

                // Verify extraction succeeded
                if (!File.Exists(_modelPath))
                {
                    throw new FileNotFoundException($"Model extraction failed. File not found at {_modelPath}");
                }

                // Clean up archive
                try
                {
                    File.Delete(archivePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete archive: {ex.Message}");
                }
            });

            ReportProgress("Extraction completed successfully");
        }

        public Task<string[]> GetAvailableVoicesAsync()
        {
            // The list is fixed, so it is answered without loading the engine: listing
            // voices should not cost an 80MB download or fail when that download does.
            return Task.FromResult(PresetVoices);
        }

        public Task SelectVoiceAsync(string voiceName)
        {
            if (PresetVoices.Contains(voiceName))
            {
                _currentVoiceName = voiceName;
            }
            return Task.CompletedTask;
        }

        public void SetRate(int rate)
        {
            // UI Rate is from -10 to 10. Kokoro speed is a multiplier:
            // 0 -> 1.0f
            // -10 -> 0.5f (rate < 0: 1.0f + rate * 0.05f)
            // 10 -> 2.0f (rate > 0: 1.0f + rate * 0.10f)
            float speed = 1.0f;
            if (rate < 0)
            {
                speed = 1.0f + (rate * 0.05f);
            }
            else if (rate > 0)
            {
                speed = 1.0f + (rate * 0.10f);
            }

            _currentRate = Math.Max(0.5f, Math.Min(2.5f, speed));
            ReportProgress($"Speech rate set to {rate} (speed multiplier: {_currentRate})");
        }

        private int GetSpeakerId(string voiceName)
        {
            switch (voiceName)
            {
                case "af_heart": return 0;
                case "af": return 0;
                case "af_bella": return 1;
                case "af_nicole": return 2;
                case "af_sarah": return 3;
                case "af_sky": return 4;
                case "am_adam": return 5;
                case "am_michael": return 6;
                case "bf_emma": return 7;
                case "bf_isabella": return 8;
                case "bm_george": return 9;
                case "bm_lewis": return 10;
                default: return 0;
            }
        }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await StopAsync();

            // The token and session are held in locals for the life of this utterance.
            // Reading them back from the fields would pick up the next utterance's as
            // soon as one started, and this one would carry on talking over it.
            var tokenSource = new System.Threading.CancellationTokenSource();
            var token = tokenSource.Token;
            // One session for the whole utterance, so a pause taken during one
            // chunk still holds the chunks that follow it.
            var session = new PlaybackSession();

            lock (_lock)
            {
                _playTokenSource = tokenSource;
                _playbackSession = session;
                _isSpeaking = true;
            }

            try
            {
                await EnsureInitializedAsync();

                if (token.IsCancellationRequested) return;

                int sid = GetSpeakerId(_currentVoiceName);

                if (!_settings.EnableProgressiveChunking)
                {
                    // Synthesize entire text at once
                    byte[] audioData = await InferChunkAsync(text, sid, token);
                    if (token.IsCancellationRequested || audioData == null) return;
                    await AudioPlayer.PlayWavAsync(audioData, token, session);
                }
                else
                {
                    var chunks = TextNormalizer.ChunkText(text);
                    if (chunks.Count == 0) return;

                    // Start generating first chunk
                    Task<byte[]> nextInferenceTask = InferChunkAsync(chunks[0], sid, token);

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        byte[] audioData = await nextInferenceTask;

                        if (token.IsCancellationRequested) break;

                        if (i + 1 < chunks.Count)
                        {
                            // Start pre-generating next chunk in background while current plays
                            nextInferenceTask = InferChunkAsync(chunks[i + 1], sid, token);
                        }

                        // A chunk that failed to synthesise is skipped rather than
                        // ending the whole reading.
                        if (audioData == null) continue;

                        // Play current chunk
                        await AudioPlayer.PlayWavAsync(audioData, token, session);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped naturally
            }
            catch (Exception ex)
            {
                ReportProgress($"Error during Kokoro Speech: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    // Only if a newer utterance has not already taken over.
                    if (ReferenceEquals(_playTokenSource, tokenSource))
                    {
                        _isSpeaking = false;
                        _playTokenSource = null;
                        _playbackSession = null;
                    }
                }
                tokenSource.Dispose();
            }
        }

        private async Task<byte[]> InferChunkAsync(string chunk, int sid, System.Threading.CancellationToken token)
        {
            string tempFile = null;
            try
            {
                var audio = await Task.Run(() => _tts.Generate(chunk, _currentRate, sid));

                if (token.IsCancellationRequested || audio == null) return null;

                tempFile = Path.Combine(_kokoroDir, $"temp_kokoro_{Guid.NewGuid()}.wav");
                return await Task.Run(() =>
                {
                    audio.SaveToWaveFile(tempFile);
                    return File.ReadAllBytes(tempFile);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error inferring chunk: {ex.Message}");
                return null;
            }
            finally
            {
                if (tempFile != null) TryDeleteFile(tempFile);
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete temp audio file: {ex.Message}");
            }
        }

        public Task StopAsync()
        {
            lock (_lock)
            {
                _isSpeaking = false;
                try { _playTokenSource?.Cancel(); }
                catch (ObjectDisposedException) { }
                _playTokenSource = null;
                _playbackSession = null;
            }
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            PlaybackSession session;
            lock (_lock)
            {
                session = _isSpeaking ? _playbackSession : null;
            }
            session?.Pause();
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            PlaybackSession session;
            lock (_lock)
            {
                session = _playbackSession;
            }
            session?.Resume();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Predownload model to ensure it is ready before speaking.
        /// </summary>
        public async Task PredownloadVoiceAsync()
        {
            await EnsureInitializedAsync();
        }
    }
}
