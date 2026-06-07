using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NarratorHotkey
{
    public static class AudioPlayer
    {
        public static async Task PlayWavAsync(byte[] wavData, CancellationToken cancellationToken)
        {
            if (wavData == null || wavData.Length == 0) return;

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
                catch (Exception ex)
                {
                    Console.WriteLine($"Error playing audio on Windows fallback: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch { }
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
                    
                    // Try different audio playing commands in order of preference
                    string[] players = { "paplay", "pw-play", "aplay" };
                    string playerSelected = null;
                    
                    foreach (var player in players)
                    {
                        if (CommandExists(player))
                        {
                            playerSelected = player;
                            break;
                        }
                    }
                    
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
                catch (Exception ex)
                {
                    Console.WriteLine($"Error playing audio on Linux: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch { }
                }
            }
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
}
