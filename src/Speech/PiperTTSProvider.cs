
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PiperSharp;
using PiperSharp.Models;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// TTS provider using Piper (local neural TTS).
    /// </summary>
    public class PiperTTSProvider : ITTSProvider
    {
        private readonly AppSettings _settings;
        private string _piperDir;
        private string _piperExePath;
        private string _currentVoiceName;
        private float _currentRate = 1.0f;
        private bool _isInitialized = false;
        private bool _isSpeaking = false;
        private readonly List<string> _availableVoiceNames = [];
        private readonly WindowsTTSProvider _windowsFallback;

        // Progress reporting
        public event Action<string> ProgressChanged;
        private void ReportProgress(string message)
        {
            Console.WriteLine($"[Piper] {message}");
            ProgressChanged?.Invoke(message);
        }

        // Announce errors to user via Windows TTS as fallback
        private void AnnounceError(string message)
        {
            ReportProgress(message);
            try
            {
                // Use Windows TTS to announce the error so user knows what's happening
                // Fire and forget so we don't block
                _ = _windowsFallback.SpeakAsync($"Piper error: {message}");
            }
            catch
            {
                // Silently fail if Windows TTS isn't available
            }
        }

        public PiperTTSProvider(AppSettings settings)
        {
            _settings = settings;
            _currentVoiceName = "en_US-lessac-medium";
            _windowsFallback = new WindowsTTSProvider(settings);
        }

        private async Task EnsureInitializedAsync()
        {
            if (_isInitialized) return;

            try
            {
                _piperDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NarratorHotkey",
                    "Piper"
                );

                if (!Directory.Exists(_piperDir))
                {
                    ReportProgress("Creating Piper directory...");
                    Directory.CreateDirectory(_piperDir);
                }

                // Download Piper if not present
                // PiperDownloader downloads to {_piperDir}/piper/piper.exe
                _piperExePath = Path.Combine(_piperDir, "piper", "piper.exe");
                if (!File.Exists(_piperExePath))
                {
                    ReportProgress("Downloading Piper TTS executable (this may take a minute)...");
                    await Task.Run(async () =>
                    {
                        try
                        {
                            ReportProgress("Connecting to download server...");
                            await PiperDownloader.DownloadPiper().ExtractPiper(_piperDir);
                            ReportProgress("Piper executable downloaded successfully");

                            // Verify the file was actually created
                            if (!File.Exists(_piperExePath))
                            {
                                throw new FileNotFoundException($"Piper executable was not created at {_piperExePath} after download. Check directory permissions and available disk space.");
                            }
                        }
                        catch (Exception ex)
                        {
                            ReportProgress($"ERROR: Piper download failed: {ex.Message}");
                            Console.WriteLine($"Full error: {ex}");
                            throw;
                        }
                    });
                }
                else
                {
                    ReportProgress("Piper executable found");
                }

                // Get available models from HuggingFace
                ReportProgress("Fetching available voice models from HuggingFace...");
                await Task.Run(async () =>
                {
                    try
                    {
                        ReportProgress("Downloading model list...");
                        var modelList = await PiperDownloader.GetHuggingFaceModelList();
                        if (modelList != null)
                        {
                            _availableVoiceNames.Clear();
                            _availableVoiceNames.AddRange(modelList.Keys);
                            ReportProgress($"Loaded {_availableVoiceNames.Count} voice models");
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportProgress($"Warning: Failed to load model list: {ex.Message}");
                    }
                });

                _isInitialized = true;
                ReportProgress("Piper TTS initialized successfully");
            }
            catch (Exception ex)
            {
                ReportProgress($"Failed to initialize Piper TTS: {ex.Message}");
                _isInitialized = false;
                throw;
            }
        }

        public async Task SpeakAsync(string text)
        {
            if (_isSpeaking)
            {
                await StopAsync();
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                await EnsureInitializedAsync();

                _isSpeaking = true;

                // Load or download the voice model from cache
                dynamic voiceModel = null;
                try
                {
                    ReportProgress($"Loading voice model: {_currentVoiceName}");
                    // This will use cached model if available, otherwise download
                    voiceModel = await LoadOrDownloadModelAsync(_currentVoiceName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load model for '{_currentVoiceName}': {ex.Message}");
                    return;
                }

                if (voiceModel == null)
                {
                    AnnounceError($"Could not load voice model: {_currentVoiceName}");
                    return;
                }

                // Create a new PiperProvider with the model configured
                var piperWorkingDir = Path.Combine(_piperDir, "piper");
                var piperProvider = new PiperProvider(new PiperConfiguration
                {
                    ExecutableLocation = _piperExePath,
                    WorkingDirectory = piperWorkingDir,
                    Model = voiceModel,
                    SpeakingRate = _currentRate
                });

                // Synthesize speech
                var audioData = await piperProvider.InferAsync(text, AudioOutputType.Wav);

                // Play audio from byte array
                PlayAudio(audioData);
            }
            catch (Exception ex)
            {
                AnnounceError($"Failed to speak: {ex.Message}");
            }
            finally
            {
                _isSpeaking = false;
            }
        }

        public Task StopAsync()
        {
            _isSpeaking = false;
            return Task.CompletedTask;
        }

        public async Task<string[]> GetAvailableVoicesAsync()
        {
            try
            {
                await EnsureInitializedAsync();

                if (_availableVoiceNames.Count == 0)
                {
                    return new[] { "en_US-lessac-medium" }; // Fallback
                }

                return _availableVoiceNames.OrderBy(v => v).ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get available voices: {ex.Message}");
                return new[] { "en_US-lessac-medium" }; // Fallback
            }
        }

        public async Task SelectVoiceAsync(string voiceName)
        {
            try
            {
                await EnsureInitializedAsync();

                if (_availableVoiceNames.Count == 0 || !_availableVoiceNames.Contains(voiceName))
                {
                    Console.WriteLine($"Voice model '{voiceName}' not found, using default");
                    _currentVoiceName = "en_US-lessac-medium";
                    return;
                }

                _currentVoiceName = voiceName;
                Console.WriteLine($"Selected voice: {voiceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to select voice: {ex.Message}");
            }
        }

        public void SetRate(int rate)
        {
            // Piper doesn't have built-in rate control like Windows TTS
            // Convert -10 to 10 range to 0.5 to 2.0 speed multiplier
            _currentRate = 1.0f + (rate / 20.0f);
            _currentRate = Math.Max(0.5f, Math.Min(2.0f, _currentRate));
        }

        public string GetProviderName() => "Piper TTS";

        /// <summary>
        /// Predownload a voice model to ensure it's available for synthesis.
        /// Call this after switching to Piper or changing voice selection.
        /// </summary>
        public async Task PredownloadVoiceAsync(string voiceName)
        {
            try
            {
                ReportProgress($"Preparing voice model: {voiceName}");
                var model = await LoadOrDownloadModelAsync(voiceName);
                if (model != null)
                {
                    ReportProgress($"Voice model ready: {voiceName}");
                }
                else
                {
                    ReportProgress($"Failed to prepare voice model: {voiceName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to predownload voice: {ex.Message}");
                ReportProgress($"Error preparing voice: {ex.Message}");
            }
        }

        private async Task<dynamic> LoadOrDownloadModelAsync(string voiceName)
        {
            // Create models directory in our persistent Piper location
            var modelsDir = Path.Combine(_piperDir, "models");
            if (!Directory.Exists(modelsDir))
            {
                Directory.CreateDirectory(modelsDir);
            }

            var modelPath = Path.Combine(modelsDir, voiceName);

            try
            {
                // First try to load from our persistent cache
                if (File.Exists(Path.Combine(modelPath, "model.json")))
                {
                    ReportProgress($"Loading cached model: {voiceName}");
                    return await VoiceModel.LoadModel(modelPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load cached model: {ex.Message}");
            }

            // Download if not cached
            try
            {
                ReportProgress($"Downloading model file for '{voiceName}'...");

                // Get model metadata from HuggingFace
                var modelList = await PiperDownloader.GetHuggingFaceModelList();
                if (modelList == null || !modelList.ContainsKey(voiceName))
                {
                    throw new Exception($"Model '{voiceName}' not found in HuggingFace repository");
                }

                var model = modelList[voiceName];
                ReportProgress($"Downloading '{voiceName}' model files...");

                // Create temp directory for download (PiperSharp creates nested structure)
                var tempDownloadPath = Path.Combine(modelsDir, $"_temp_{voiceName}");
                if (Directory.Exists(tempDownloadPath))
                {
                    Directory.Delete(tempDownloadPath, true);
                }

                // Download to temporary location
                await model.DownloadModel(tempDownloadPath);
                ReportProgress($"Model downloaded, organizing files...");

                // PiperSharp creates: tempDownloadPath/{voiceName}/model.json
                // Flatten it to: modelPath/model.json
                var nestedPath = Path.Combine(tempDownloadPath, voiceName);
                if (!Directory.Exists(nestedPath))
                {
                    throw new DirectoryNotFoundException($"Model files not found at expected location");
                }

                var modelJsonPath = Path.Combine(nestedPath, "model.json");
                if (!File.Exists(modelJsonPath))
                {
                    throw new FileNotFoundException($"model.json not found after download");
                }

                // Move files from nested structure to flat structure
                if (Directory.Exists(modelPath))
                {
                    Directory.Delete(modelPath, true);
                }
                Directory.Move(nestedPath, modelPath);

                // Clean up temp directory
                if (Directory.Exists(tempDownloadPath))
                {
                    Directory.Delete(tempDownloadPath, true);
                }

                ReportProgress($"Model ready, loading '{voiceName}'...");
                return await VoiceModel.LoadModel(modelPath);
            }
            catch (Exception ex)
            {
                AnnounceError($"Failed to download model {voiceName}");
                return null;
            }
        }

        private void PlayAudio(byte[] audioData)
        {
            try
            {
                var tempFile = Path.GetTempFileName();
                File.WriteAllBytes(tempFile, audioData);

                // Use System.Media.SoundPlayer to play the audio
                var player = new System.Media.SoundPlayer(tempFile);
                player.PlaySync();

                // Clean up
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to play audio: {ex.Message}");
            }
        }
    }
}
