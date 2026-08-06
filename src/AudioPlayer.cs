using System;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NarratorHotkey
{
    /// <summary>
    /// The pause state of one utterance. Speech is played one chunk at a time, so the
    /// pause has to outlive the clip that happened to be playing when the user asked
    /// for it: without a state shared across chunks, playback would resume by itself
    /// as soon as the current sentence ended.
    /// </summary>
    public sealed class PlaybackSession
    {
        private readonly object _gate = new object();
        private IPausableClip _clip;
        private bool _paused;

        public bool IsPaused
        {
            get { lock (_gate) { return _paused; } }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_paused) return;
                _paused = true;
                TryPauseClip();
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (!_paused) return;
                _paused = false;
                TryResumeClip();
            }
        }

        /// <summary>Pauses if playing, resumes if paused. Returns the new paused state.</summary>
        public bool Toggle()
        {
            lock (_gate)
            {
                if (_paused)
                {
                    _paused = false;
                    TryResumeClip();
                }
                else
                {
                    _paused = true;
                    TryPauseClip();
                }
                return _paused;
            }
        }

        /// <summary>
        /// Hands the clip about to be played to the session. A clip attached while the
        /// session is paused starts silent rather than talking over the pause.
        /// </summary>
        internal void Attach(IPausableClip clip)
        {
            if (clip == null) return;
            lock (_gate)
            {
                _clip = clip;
                if (_paused)
                {
                    try { clip.Pause(); }
                    catch (Exception ex) { Console.WriteLine($"Failed to pause new audio clip: {ex.Message}"); }
                }
            }
        }

        internal void Detach(IPausableClip clip)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_clip, clip))
                {
                    _clip = null;
                }
            }
        }

        private void TryPauseClip()
        {
            try { _clip?.Pause(); }
            catch (Exception ex) { Console.WriteLine($"Failed to pause audio: {ex.Message}"); }
        }

        private void TryResumeClip()
        {
            try { _clip?.Resume(); }
            catch (Exception ex) { Console.WriteLine($"Failed to resume audio: {ex.Message}"); }
        }
    }

    /// <summary>
    /// A single sound that can be suspended part way through. System.Media.SoundPlayer
    /// can only start and stop, which is why playback does not go through it any more.
    /// </summary>
    internal interface IPausableClip : IDisposable
    {
        /// <summary>Begins playback, unless the clip was paused before it started.</summary>
        void Start();
        void Pause();
        void Resume();
        void Stop();

        /// <summary>True once the sound has played to its end.</summary>
        bool IsFinished { get; }
    }

    public static class AudioPlayer
    {
        private const int PollIntervalMs = 40;

        public static Task PlayWavAsync(byte[] wavData, CancellationToken cancellationToken)
        {
            return PlayWavAsync(wavData, cancellationToken, null);
        }

        /// <summary>
        /// Plays a WAV and returns when it has finished, been stopped, or the token is
        /// cancelled. Time spent paused does not count towards the sound's length, so a
        /// paused clip keeps this call pending until it is resumed or stopped.
        /// </summary>
        public static async Task PlayWavAsync(byte[] wavData, CancellationToken cancellationToken, PlaybackSession session)
        {
            if (wavData == null || wavData.Length == 0) return;
            if (cancellationToken.IsCancellationRequested) return;

            IPausableClip clip = await CreateClipAsync(wavData, cancellationToken);
            if (clip == null)
            {
                // No pausable backend on this machine; play it the old way so the user
                // still hears the text, just without being able to pause it. A clip that
                // failed only because it was stopped is not worth replaying.
                if (!cancellationToken.IsCancellationRequested)
                {
                    await PlayWithoutPauseAsync(wavData, cancellationToken);
                }
                return;
            }

            try
            {
                using (cancellationToken.Register(() =>
                {
                    try { clip.Stop(); }
                    catch { }
                }))
                {
                    session?.Attach(clip);
                    clip.Start();

                    while (!clip.IsFinished)
                    {
                        await Task.Delay(PollIntervalMs, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by the user.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing audio: {ex.Message}");
            }
            finally
            {
                session?.Detach(clip);
                clip.Dispose();
            }
        }

        private static async Task<IPausableClip> CreateClipAsync(byte[] wavData, CancellationToken cancellationToken)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"narrator_tts_{Guid.NewGuid():N}.wav");
            try
            {
                await File.WriteAllBytesAsync(tempFile, wavData, cancellationToken);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return MciWaveClip.TryOpen(tempFile);
                }

                string player = FindLinuxPlayer();
                if (player == null)
                {
                    Console.WriteLine("Warning: No audio player command (paplay, pw-play, aplay) found on system.");
                    TryDeleteFile(tempFile);
                    return null;
                }

                return new ProcessWaveClip(player, tempFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to prepare pausable audio clip: {ex.Message}");
                TryDeleteFile(tempFile);
                return null;
            }
        }

        /// <summary>
        /// The original playback path, kept as a fallback for when the pausable backend
        /// cannot be opened.
        /// </summary>
        private static async Task PlayWithoutPauseAsync(byte[] wavData, CancellationToken cancellationToken)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                try
                {
                    using var ms = new MemoryStream(wavData);
                    using var player = new System.Media.SoundPlayer(ms);
                    using (cancellationToken.Register(() =>
                    {
                        try
                        {
                            player.Stop();
                        }
                        catch { }
                    }))
                    {
                        player.Play();
                        int durationMs = GetWavDurationMs(wavData);
                        await Task.Delay(durationMs + 100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped by the user.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error playing audio on Windows: {ex.Message}");
                }
#else
                // Fallback for cross-platform target running on Windows (e.g. dotnet run or dotnet bin/Debug/net10.0/NarratorHotkey.dll on Windows)
                string tempFile = Path.Combine(Path.GetTempPath(), $"narrator_tts_{Guid.NewGuid()}.wav");
                try
                {
                    await File.WriteAllBytesAsync(tempFile, wavData, cancellationToken);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"(New-Object System.Media.SoundPlayer '{tempFile.Replace("'", "''")}').PlaySync()\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        using (cancellationToken.Register(() =>
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                }
                            }
                            catch { }
                        }))
                        {
                            await process.WaitForExitAsync(cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped by the user.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error playing audio on Windows fallback: {ex.Message}");
                }
                finally
                {
                    TryDeleteFile(tempFile);
                }
#endif
            }
            else
            {
                // Linux / macOS audio playback using system command line players
                string tempFile = Path.Combine(Path.GetTempPath(), $"narrator_tts_{Guid.NewGuid()}.wav");
                try
                {
                    await File.WriteAllBytesAsync(tempFile, wavData, cancellationToken);

                    string playerSelected = FindLinuxPlayer();
                    if (playerSelected == null)
                    {
                        Console.WriteLine("Warning: No audio player command (paplay, pw-play, aplay) found on system.");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = playerSelected,
                        Arguments = $"\"{tempFile}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        using (cancellationToken.Register(() =>
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                }
                            }
                            catch { }
                        }))
                        {
                            await process.WaitForExitAsync(cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped by the user.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error playing audio on Linux: {ex.Message}");
                }
                finally
                {
                    TryDeleteFile(tempFile);
                }
            }
        }

        private static string FindLinuxPlayer()
        {
            // Try different audio playing commands in order of preference
            string[] players = { "paplay", "pw-play", "aplay" };
            foreach (var player in players)
            {
                if (CommandExists(player))
                {
                    return player;
                }
            }
            return null;
        }

        private static bool CommandExists(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        internal static void TryDeleteFile(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        public static int GetWavDurationMs(byte[] wavData)
        {
            try
            {
                if (wavData == null || wavData.Length < 44) return 0;
                int byteRate = BitConverter.ToInt32(wavData, 28);
                if (byteRate <= 0) return 3000;
                return (int)(((wavData.Length - 44) * 1000L) / byteRate);
            }
            catch
            {
                return 3000;
            }
        }
    }

    /// <summary>
    /// Runs every MCI command on one long-lived thread. An MCI device belongs to the
    /// thread that opened it: commands sent from any other thread fail with error 263,
    /// "the specified device is not open". Speech is played from pool threads and
    /// paused from the message loop or an HTTP handler, so without a fixed thread to
    /// own the devices, pause and resume would quietly do nothing.
    /// </summary>
    internal static class MciDispatcher
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static readonly Thread Worker;

        static MciDispatcher()
        {
            Worker = new Thread(Pump)
            {
                IsBackground = true,
                Name = "NarratorHotkey MCI"
            };
            Worker.Start();
        }

        private static void Pump()
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MCI worker error: {ex.Message}");
                }
            }
        }

        internal static T Run<T>(Func<T> work)
        {
            if (Thread.CurrentThread == Worker)
            {
                return work();
            }

            T result = default;
            Exception error = null;
            using var done = new ManualResetEventSlim(false);

            Queue.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            done.Wait();
            if (error != null)
            {
                throw new InvalidOperationException("MCI command failed.", error);
            }
            return result;
        }
    }

    /// <summary>
    /// Windows playback through MCI (winmm). MCI is used in place of SoundPlayer
    /// because it is the one built-in player that can pause and resume a WAV part way
    /// through without pulling in an audio library.
    /// </summary>
    internal sealed class MciWaveClip : IPausableClip
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr hwndCallback);

        // MCI reports "stopped" for a moment after play is issued and before the device
        // actually starts, so a stopped device is only believed once playback has had
        // time to begin.
        private const int StartGraceMs = 250;

        private static int _aliasCounter;

        private readonly object _gate = new object();
        private readonly string _alias;
        private readonly string _file;
        private readonly Stopwatch _sincePlay = new Stopwatch();

        private bool _startRequested;
        private bool _playIssued;
        private bool _paused;
        private bool _stopped;
        private bool _disposed;

        private MciWaveClip(string alias, string file)
        {
            _alias = alias;
            _file = file;
        }

        /// <summary>
        /// Opens the file with MCI. Returns null if MCI refuses it, which leaves the
        /// caller free to fall back to a player that cannot pause.
        /// </summary>
        internal static MciWaveClip TryOpen(string file)
        {
            string alias = $"narrator{Environment.ProcessId}_{Interlocked.Increment(ref _aliasCounter)}";
            int result = Send($"open \"{file}\" type waveaudio alias {alias}");
            if (result != 0)
            {
                Console.WriteLine($"MCI could not open the audio clip (error {result}); falling back to non-pausable playback.");
                AudioPlayer.TryDeleteFile(file);
                return null;
            }

            var clip = new MciWaveClip(alias, file);
            Send($"set {alias} time format milliseconds");
            return clip;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || _startRequested) return;
                _startRequested = true;
                if (!_paused)
                {
                    IssuePlay();
                }
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || _paused) return;
                _paused = true;
                if (_playIssued)
                {
                    Send($"pause {_alias}");
                }
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || !_paused) return;
                _paused = false;
                if (!_startRequested) return;

                if (_playIssued)
                {
                    // "resume" carries on from the pause position; some drivers do not
                    // implement it, in which case playing from the current position has
                    // the same effect.
                    if (Send($"resume {_alias}") != 0)
                    {
                        Send($"play {_alias} from {Query($"status {_alias} position")}");
                    }
                }
                else
                {
                    IssuePlay();
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed || _stopped) return;
                _stopped = true;
                Send($"stop {_alias}");
            }
        }

        public bool IsFinished
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed || _stopped) return true;
                    if (!_playIssued || _paused) return false;

                    string mode = Query($"status {_alias} mode");
                    if (mode == "playing" || mode == "paused" || mode == "seeking")
                    {
                        return false;
                    }

                    return _sincePlay.ElapsedMilliseconds >= StartGraceMs;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Send($"close {_alias}");
            }
            AudioPlayer.TryDeleteFile(_file);
        }

        private void IssuePlay()
        {
            _playIssued = true;
            _sincePlay.Restart();
            int result = Send($"play {_alias}");
            if (result != 0)
            {
                Console.WriteLine($"MCI failed to start playback (error {result}).");
                _stopped = true;
            }
        }

        private static int Send(string command)
        {
            try
            {
                return MciDispatcher.Run(() => mciSendString(command, null, 0, IntPtr.Zero));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MCI command failed ('{command}'): {ex.Message}");
                return -1;
            }
        }

        private static string Query(string command)
        {
            try
            {
                return MciDispatcher.Run(() =>
                {
                    var buffer = new StringBuilder(128);
                    if (mciSendString(command, buffer, buffer.Capacity, IntPtr.Zero) != 0)
                    {
                        return string.Empty;
                    }
                    return buffer.ToString().Trim();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MCI query failed ('{command}'): {ex.Message}");
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Linux and macOS playback through the system player. The player is suspended and
    /// continued with SIGSTOP/SIGCONT, which pauses the sound where it stands.
    /// </summary>
    internal sealed class ProcessWaveClip : IPausableClip
    {
        private readonly object _gate = new object();
        private readonly string _player;
        private readonly string _file;

        private Process _process;
        private bool _startRequested;
        private bool _paused;
        private bool _stopped;
        private bool _disposed;

        internal ProcessWaveClip(string player, string file)
        {
            _player = player;
            _file = file;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || _startRequested) return;
                _startRequested = true;

                var psi = new ProcessStartInfo
                {
                    FileName = _player,
                    Arguments = $"\"{_file}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = Process.Start(psi);

                if (_paused)
                {
                    Signal("-STOP");
                }
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || _paused) return;
                _paused = true;
                Signal("-STOP");
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_disposed || _stopped || !_paused) return;
                _paused = false;
                Signal("-CONT");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed || _stopped) return;
                _stopped = true;
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch { }
            }
        }

        public bool IsFinished
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed || _stopped) return true;
                    if (!_startRequested || _paused) return false;
                    try
                    {
                        return _process == null || _process.HasExited;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        // A suspended process ignores everything except SIGKILL, so let
                        // it run again before killing it.
                        Signal("-CONT");
                        _process.Kill();
                    }
                }
                catch { }
                _process?.Dispose();
                _process = null;
            }
            AudioPlayer.TryDeleteFile(_file);
        }

        private void Signal(string signal)
        {
            try
            {
                if (_process == null || _process.HasExited) return;
                using var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"{signal} {_process.Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                kill?.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send {signal} to the audio player: {ex.Message}");
            }
        }
    }
}
