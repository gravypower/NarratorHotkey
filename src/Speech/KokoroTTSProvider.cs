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
    /// TTS provider using the Kokoro v1.0 ONNX model via sherpa-onnx, supporting low-latency progressive chunking.
    /// </summary>
    public class KokoroTTSProvider : ITTSProvider
    {
        // The multi-language v1.0 model. Only its English voices are offered: the
        // others need their own language front end to read anything but English text.
        private const string ModelName = "kokoro-multi-lang-v1_0";
        private const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + ModelName + ".tar.bz2";

        // The model this replaced. Removed once the new one is working.
        private const string LegacyModelName = "kokoro-en-v0_19";

        private const string DefaultVoice = "af_heart";
        private const string AmericanLexicon = "lexicon-us-en.txt";
        private const string BritishLexicon = "lexicon-gb-en.txt";

        // A voice's position here is its speaker id in the model's voices.bin, so the
        // order must not change. These are the first 28 of the model's speakers.
        private static readonly string[] Voices = new[]
        {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",
            "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael",
            "am_onyx", "am_puck", "am_santa",
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily",
            "bm_daniel", "bm_fable", "bm_george", "bm_lewis"
        };

        private readonly AppSettings _settings;
        private string _kokoroDir;
        private string _modelDir;
        private string _currentVoiceName = DefaultVoice;
        private float _currentRate = 1.0f;
        private bool _isSpeaking = false;
        private System.Threading.CancellationTokenSource _playTokenSource;
        private PlaybackSession _playbackSession;
        private readonly object _lock = new object();
        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);

        // The engine, and the pronunciation lexicon it was built with. An engine takes
        // one lexicon for its lifetime, so moving between an American and a British
        // voice rebuilds it. _engineLock keeps a rebuild from disposing the engine
        // underneath a chunk that is still being synthesised.
        private readonly object _engineLock = new object();
        private volatile OfflineTts _tts;
        private volatile string _loadedLexicon;

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
            _currentVoiceName = Voices.Contains(settings.KokoroVoice) ? settings.KokoroVoice : DefaultVoice;
        }

        public string GetProviderName() => "Kokoro ONNX";

        /// <summary>
        /// British voices (bf_, bm_) are given British pronunciations; the rest American.
        /// </summary>
        private static string LexiconFor(string voiceName)
        {
            return voiceName != null && voiceName.StartsWith("b", StringComparison.Ordinal)
                ? BritishLexicon
                : AmericanLexicon;
        }

        /// <summary>
        /// Makes sure the model is on disk and an engine suited to
        /// <paramref name="voiceName"/> is loaded.
        /// </summary>
        private async Task EnsureInitializedAsync(string voiceName)
        {
            string lexicon = LexiconFor(voiceName);
            if (_tts != null && _loadedLexicon == lexicon) return;

            // Initialization downloads and extracts the model and builds the engine;
            // two concurrent runs would fight over the same files.
            await _initLock.WaitAsync();
            try
            {
                if (_tts != null && _loadedLexicon == lexicon) return;
                await InitializeAsync(lexicon);
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task InitializeAsync(string lexicon)
        {
            try
            {
                _kokoroDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NarratorHotkey",
                    "Kokoro"
                );
                Directory.CreateDirectory(_kokoroDir);

                _modelDir = Path.Combine(_kokoroDir, ModelName);

                if (!IsModelPresent())
                {
                    await DownloadAndExtractModelAsync();
                }

                // Initialize SherpaOnnx OfflineTts
                ReportProgress("Initializing Kokoro ONNX engine...");
                var engine = await Task.Run(() =>
                {
                    var config = new OfflineTtsConfig();
                    config.Model.Kokoro.Model = Path.Combine(_modelDir, "model.onnx");
                    config.Model.Kokoro.Voices = Path.Combine(_modelDir, "voices.bin");
                    config.Model.Kokoro.Tokens = Path.Combine(_modelDir, "tokens.txt");
                    config.Model.Kokoro.DataDir = Path.Combine(_modelDir, "espeak-ng-data");
                    config.Model.Kokoro.Lexicon = Path.Combine(_modelDir, lexicon);
                    config.Model.Kokoro.LengthScale = 1.0f;

                    config.Model.NumThreads = 2;
                    config.Model.Provider = "cpu";

                    return new OfflineTts(config);
                });

                if (engine.NumSpeakers < Voices.Length)
                {
                    int found = engine.NumSpeakers;
                    engine.Dispose();
                    throw new InvalidDataException(
                        $"The Kokoro model has {found} voices where at least {Voices.Length} were expected.");
                }

                OfflineTts previous;
                lock (_engineLock)
                {
                    previous = _tts;
                    _tts = engine;
                    _loadedLexicon = lexicon;
                }
                previous?.Dispose();

                ReportProgress("Kokoro ONNX initialized successfully");
                RemoveLegacyModel();
            }
            catch (Exception ex)
            {
                ReportProgress($"Failed to initialize Kokoro: {ex.Message}");
                throw;
            }
        }

        private bool IsModelPresent()
        {
            return File.Exists(Path.Combine(_modelDir, "model.onnx"))
                && File.Exists(Path.Combine(_modelDir, "voices.bin"))
                && File.Exists(Path.Combine(_modelDir, "tokens.txt"))
                && File.Exists(Path.Combine(_modelDir, AmericanLexicon))
                && File.Exists(Path.Combine(_modelDir, BritishLexicon))
                && Directory.Exists(Path.Combine(_modelDir, "espeak-ng-data"));
        }

        /// <summary>
        /// Frees the space held by the v0.19 model this provider used before v1.0.
        /// </summary>
        private void RemoveLegacyModel()
        {
            try
            {
                string legacyDir = Path.Combine(_kokoroDir, LegacyModelName);
                if (Directory.Exists(legacyDir))
                {
                    Directory.Delete(legacyDir, true);
                    ReportProgress("Removed the superseded v0.19 model");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not remove the old Kokoro model: {ex.Message}");
            }
        }

        private async Task DownloadAndExtractModelAsync()
        {
            string archivePath = Path.Combine(_kokoroDir, ModelName + ".tar.bz2");
            // Unpacked beside the final location and moved into place when complete, so
            // an interrupted download or extraction never leaves a model directory that
            // looks usable.
            string stagingDir = Path.Combine(_kokoroDir, "_staging_" + ModelName);

            try
            {
                ReportProgress("Downloading the Kokoro v1.0 model (approx. 350MB)...");
                using (var client = new HttpClient())
                {
                    // Bounds the wait for the server, not the transfer: at 350MB the
                    // download itself can fairly take a long while on a slow line.
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                    using var headerTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                    using var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                    response.EnsureSuccessStatusCode();

                    long? total = response.Content.Headers.ContentLength;
                    using var source = await response.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);

                    var buffer = new byte[1 << 16];
                    long received = 0;
                    int lastReported = 0;
                    while (true)
                    {
                        // A connection that goes quiet is given up on rather than
                        // waited for indefinitely.
                        using var stall = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                        int read = await source.ReadAsync(buffer, stall.Token);
                        if (read == 0) break;

                        await fs.WriteAsync(buffer.AsMemory(0, read));
                        received += read;

                        if (total > 0)
                        {
                            int percent = (int)(received * 100 / total.Value);
                            if (percent >= lastReported + 10)
                            {
                                lastReported = percent - percent % 10;
                                ReportProgress($"Downloading model: {lastReported}%");
                            }
                        }
                    }

                    if (total > 0 && received != total.Value)
                    {
                        throw new IOException($"The model download ended early ({received} of {total.Value} bytes).");
                    }
                }

                ReportProgress("Extracting model archive (tar.bz2)...");
                await Task.Run(() =>
                {
                    if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                    Directory.CreateDirectory(stagingDir);

                    // A forward-only reader: SharpCompress's archive API cannot open a
                    // compressed tar, only the streaming one can.
                    using (var stream = File.OpenRead(archivePath))
                    using (var reader = ReaderFactory.OpenReader(stream, new ReaderOptions()))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            if (!reader.Entry.IsDirectory)
                            {
                                reader.WriteEntryToDirectory(stagingDir, new ExtractionOptions()
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true
                                });
                            }
                        }
                    }

                    string extracted = Path.Combine(stagingDir, ModelName);
                    if (!File.Exists(Path.Combine(extracted, "model.onnx")))
                    {
                        throw new FileNotFoundException($"Model extraction failed. No model.onnx under {extracted}");
                    }

                    if (Directory.Exists(_modelDir)) Directory.Delete(_modelDir, true);
                    Directory.Move(extracted, _modelDir);
                });

                ReportProgress("Extraction completed successfully");
            }
            finally
            {
                try
                {
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                    if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to clean up after the model download: {ex.Message}");
                }
            }
        }

        public Task<string[]> GetAvailableVoicesAsync()
        {
            // The list is fixed, so it is answered without loading the engine: listing
            // voices should not cost a 350MB download or fail when that download does.
            return Task.FromResult(Voices);
        }

        public Task SelectVoiceAsync(string voiceName)
        {
            if (Voices.Contains(voiceName))
            {
                _currentVoiceName = voiceName;
            }
            else if (!string.IsNullOrEmpty(voiceName))
            {
                Console.WriteLine($"[Kokoro] Unknown voice '{voiceName}'; keeping {_currentVoiceName}.");
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

        private static int GetSpeakerId(string voiceName)
        {
            int id = Array.IndexOf(Voices, voiceName);
            return id >= 0 ? id : Array.IndexOf(Voices, DefaultVoice);
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
                string voiceName = _currentVoiceName;
                await EnsureInitializedAsync(voiceName);

                if (token.IsCancellationRequested) return;

                int sid = GetSpeakerId(voiceName);

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
                var audio = await Task.Run(() =>
                {
                    lock (_engineLock)
                    {
                        // Checked under the lock: by the time it is free the engine may
                        // have been rebuilt for a later reading in a different accent.
                        if (token.IsCancellationRequested) return null;
                        return _tts.Generate(chunk, _currentRate, sid);
                    }
                });

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
            await EnsureInitializedAsync(_currentVoiceName);
        }
    }
}
