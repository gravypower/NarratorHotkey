using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarratorHotkey.Speech;

#if WINDOWS
using System.Windows.Forms;
#endif

namespace NarratorHotkey
{
    public static class Program
    {
        private const int Port = 49191;
        private static Mutex _appMutex;

        public static event Action OnSettingsChanged;

        public static void NotifySettingsChanged()
        {
            OnSettingsChanged?.Invoke();
        }

        [STAThread]
        public static void Main(string[] args)
        {
#if WINDOWS
            if (args.Length == 0)
            {
                _appMutex = new Mutex(true, "Global\\NarratorHotkey_Tray_Mutex", out bool createdNew);
                if (!createdNew)
                {
                    // Already running, open settings page
                    OpenBrowser($"http://127.0.0.1:{Port}");
                    return;
                }

                try
                {
                    // Launch WinForms Application on Windows
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var trayApp = new TrayApplication())
                    {
                        Application.Run();
                    }
                }
                finally
                {
                    try
                    {
                        _appMutex.ReleaseMutex();
                    }
                    catch { }
                    _appMutex.Dispose();
                }
                return;
            }
#endif

            // CLI / Linux entry point
            RunCliAsync(args).GetAwaiter().GetResult();
        }

        private static async Task RunCliAsync(string[] args)
        {
            if (args.Length == 0 || args[0] == "--read")
            {
                await TriggerReadAsync();
                return;
            }

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "--daemon":
                    await RunDaemonAsync();
                    break;
                case "--settings":
                case "--ui":
                    if (!await IsDaemonRunningAsync())
                    {
                        Console.WriteLine("Daemon not running. Starting background daemon...");
                        StartDaemonProcess();
                        for (int i = 0; i < 5; i++)
                        {
                            await Task.Delay(400);
                            if (await IsDaemonRunningAsync()) break;
                        }
                    }
                    OpenBrowser($"http://127.0.0.1:{Port}");
                    break;
                case "--stop":
                    await PostToDaemonAsync("/api/stop");
                    break;
                case "--pause":
                    await PostToDaemonAsync("/api/pause");
                    break;
                case "--resume":
                    await PostToDaemonAsync("/api/resume");
                    break;
                case "--toggle-pause":
                    await PostToDaemonAsync("/api/toggle-pause");
                    break;
                case "--set-provider":
                    // Everything after the flag, so "--set-provider Kokoro ONNX" works
                    // unquoted.
                    string provider = AppSettings.ResolveProviderName(string.Join(" ", args, 1, args.Length - 1));
                    if (provider == null)
                    {
                        // An unknown name used to be saved as it was, and the daemon then
                        // quietly spoke with a different provider.
                        Console.WriteLine($"Error: Please specify a provider: {string.Join(", ", AppSettings.ProviderNames)}.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    await SetSettingAsync(s => s.TTSProvider = provider);
                    Console.WriteLine($"TTS Provider set to {provider}");
                    break;
                case "--set-voice":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Please specify voice name.");
                        return;
                    }
                    await SetSettingAsync(s => {
                        var prov = s.TTSProvider;
                        if (prov == "Piper") s.PiperVoice = args[1];
                        else if (prov == "Kokoro ONNX") s.KokoroVoice = args[1];
                        else s.SelectedVoice = args[1];
                    });
                    Console.WriteLine($"Voice set to {args[1]}");
                    break;
                case "--set-rate":
                    if (args.Length < 2 || !int.TryParse(args[1], out int rate)
                        || rate < AppSettings.MinSpeechRate || rate > AppSettings.MaxSpeechRate)
                    {
                        Console.WriteLine($"Error: Please specify an integer rate ({AppSettings.MinSpeechRate} to {AppSettings.MaxSpeechRate}).");
                        Environment.ExitCode = 1;
                        return;
                    }
                    await SetSettingAsync(s => s.SpeechRate = rate);
                    Console.WriteLine($"Speech rate set to {rate}");
                    break;
                case "--list-voices":
                    await ListVoicesAsync();
                    break;
                case "--list-providers":
                    Console.WriteLine("Available TTS Providers:");
                    foreach (string name in AppSettings.ProviderNames)
                    {
                        Console.WriteLine($" - {name}");
                    }
                    break;
                case "--status":
                    ShowStatus();
                    break;
                case "--help":
                case "-h":
                case "/?":
                    ShowHelp();
                    break;
                default:
                    Console.WriteLine($"Unknown argument: {args[0]}");
                    ShowHelp();
                    Environment.ExitCode = 1;
                    break;
            }
        }

        private static async Task SetSettingAsync(Action<AppSettings> updateAction)
        {
            var settings = AppSettings.Load();
            updateAction(settings);
            settings.Save();

            // Awaited: the process exits as soon as this returns, which would otherwise
            // drop the request before it reached the daemon. A failure only means no
            // daemon is running, and it reads the file when it next starts.
            await PostToDaemonAsync("/api/reload");
        }

        private static void ShowStatus()
        {
            var settings = AppSettings.Load();
            Console.WriteLine("NarratorHotkey Status:");
            Console.WriteLine($"  TTS Provider: {settings.TTSProvider}");
            Console.WriteLine($"  Speech Rate:  {settings.SpeechRate}");
            Console.WriteLine($"  Kokoro Voice: {settings.KokoroVoice}");
            Console.WriteLine($"  Piper Voice:  {settings.PiperVoice}");
#if WINDOWS
            Console.WriteLine($"  Windows Voice: {settings.SelectedVoice}");
            Console.WriteLine($"  Hotkey:       {settings.HotkeyModifier}+{settings.HotkeyKey}");
            Console.WriteLine($"  Pause Hotkey: {settings.PauseHotkeyModifier}+{settings.PauseHotkeyKey}");
#endif
            Console.WriteLine($"  Progressive Chunking: {settings.EnableProgressiveChunking}");
        }

        private static void ShowHelp()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  NarratorHotkey                  Reads currently selected text.");
            Console.WriteLine("  NarratorHotkey --read           Reads currently selected text.");
            Console.WriteLine("  NarratorHotkey --daemon         Starts the background TTS daemon.");
            Console.WriteLine("  NarratorHotkey --settings       Opens the web configuration settings in your browser.");
            Console.WriteLine("  NarratorHotkey --stop           Stops speaking.");
            Console.WriteLine("  NarratorHotkey --pause          Holds the reading where it is.");
            Console.WriteLine("  NarratorHotkey --resume         Carries on from where it was paused.");
            Console.WriteLine("  NarratorHotkey --toggle-pause   Pauses if reading, resumes if paused.");
            Console.WriteLine("  NarratorHotkey --set-provider <P> Set TTS Provider (Kokoro ONNX, Piper).");
            Console.WriteLine("  NarratorHotkey --set-voice <V>  Set Voice for the current provider.");
            Console.WriteLine("  NarratorHotkey --set-rate <R>   Set Speech rate (-10 to 10).");
            Console.WriteLine("  NarratorHotkey --list-voices    Lists available voices for the current provider.");
            Console.WriteLine("  NarratorHotkey --list-providers Lists available TTS providers.");
            Console.WriteLine("  NarratorHotkey --status         Shows current configuration.");
            Console.WriteLine("  NarratorHotkey --help           Shows this help information.");
        }

        private static async Task ListVoicesAsync()
        {
            var settings = AppSettings.Load();
            Console.WriteLine($"Available voices for provider '{settings.TTSProvider}':");
            var voices = await SpeechManager.Instance.GetVoicesForProviderAsync(settings.TTSProvider);
            foreach (var voice in voices)
            {
                Console.WriteLine($" - {voice}");
            }
        }

        private static async Task TriggerReadAsync()
        {
            string selectedText = "";
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            
            if (isWindows)
            {
#if WINDOWS
                selectedText = await HotkeyManager.GetSelectedTextAsync();
#endif
            }
            else
            {
                selectedText = await GetLinuxSelectedTextAsync();
            }

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                selectedText = "No text selected.";
            }

            string json = JsonSerializer.Serialize(new { text = selectedText });
            HttpContent Body() => new StringContent(json, Encoding.UTF8, "application/json");

            var reply = await PostToDaemonAsync("/api/speak", Body());
            if (reply == DaemonReply.Unreachable)
            {
                Console.WriteLine("Daemon not running. Starting background daemon...");
                StartDaemonProcess();

                // Only "nothing is listening" is retried. A request that reached the
                // daemon may already be speaking, and sending it again would start the
                // reading over, or say it twice.
                for (int i = 0; i < 10 && reply == DaemonReply.Unreachable; i++)
                {
                    await Task.Delay(400);
                    reply = await PostToDaemonAsync("/api/speak", Body());
                }

                if (reply == DaemonReply.Unreachable)
                {
                    // Fallback: speak in-process
                    Console.WriteLine("Failed to connect to daemon. Speaking in-process (will have startup latency)...");
                    await SpeechManager.Instance.SpeakAsync(selectedText);
                    return;
                }
            }

            if (reply == DaemonReply.Failed)
            {
                Console.WriteLine("The daemon did not accept the text.");
                Environment.ExitCode = 1;
            }
        }

        private static void StartDaemonProcess()
        {
            string currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;
            
            string arguments = "--daemon";
            string fileName = currentExe;

            string exeName = Path.GetFileName(currentExe);
            if (exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || 
                exeName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    dllPath = Path.Combine(AppContext.BaseDirectory, "NarratorHotkey.dll");
                }

                if (File.Exists(dllPath))
                {
                    arguments = $"\"{dllPath}\" --daemon";
                }
            }

            Console.WriteLine($"Starting background daemon: {fileName} {arguments}");

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = isWindows,
                CreateNoWindow = true,
                WindowStyle = isWindows ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            });
        }

        private enum DaemonReply
        {
            /// <summary>The daemon took the request.</summary>
            Ok,
            /// <summary>Nothing is listening on the port.</summary>
            Unreachable,
            /// <summary>The daemon is there but refused the request or failed on it.</summary>
            Failed
        }

        private static async Task<DaemonReply> PostToDaemonAsync(string path, HttpContent content = null)
        {
            try
            {
                using var client = new HttpClient();
                // Generous: a daemon that has only just started answers its first
                // request after loading its settings and provider.
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.PostAsync($"http://127.0.0.1:{Port}{path}", content);
                return response.IsSuccessStatusCode ? DaemonReply.Ok : DaemonReply.Failed;
            }
            catch (HttpRequestException)
            {
                return DaemonReply.Unreachable;
            }
            catch (OperationCanceledException)
            {
                // Timed out: it connected, so the daemon has the request and is slow
                // to answer. Treating that as "not running" used to send the text again.
                return DaemonReply.Ok;
            }
            catch
            {
                return DaemonReply.Failed;
            }
        }

        private static async Task<bool> IsDaemonRunningAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(1);
                var response = await client.GetAsync($"http://127.0.0.1:{Port}/api/status");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static async Task RunDaemonAsync()
        {
            string mutexName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Global\\NarratorHotkey_Tray_Mutex"
                : "NarratorHotkey_Tray_Mutex";
            _appMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                Console.WriteLine("Daemon or Tray Application is already running.");
                return;
            }

            try
            {
                // Start Web server / Daemon API
                if (!WebGui.Start(Port))
                {
                    // Without the API nothing can reach this process, so staying up
                    // would only look like a working daemon. A failing exit code lets
                    // systemd report it and retry.
                    Console.WriteLine($"Daemon could not listen on port {Port}; exiting.");
                    Environment.ExitCode = 1;
                    return;
                }
                Console.WriteLine($"Daemon started. Web configuration dashboard and API running at http://127.0.0.1:{Port}...");

                // Keep the process alive until terminated
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    tcs.TrySetResult(true);
                };

                // systemd stops the service with SIGTERM. Left to the runtime that ends
                // the process where it stands, leaving the audio player running and its
                // temp file behind; cancelling it lets the finally block below run.
                using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    tcs.TrySetResult(true);
                });

                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    tcs.TrySetResult(true);
                };

                // Initialize speech manager. A provider that cannot be prepared now
                // (no network for a first download, say) is not a reason to exit: the
                // settings page is how the user would fix it.
                try
                {
                    await SpeechManager.Instance.ApplySettingsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not apply settings at startup: {ex.Message}");
                }

                await tcs.Task;

                await SpeechManager.Instance.StopAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Daemon error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            finally
            {
                WebGui.Stop();
                try
                {
                    _appMutex.ReleaseMutex();
                }
                catch { }
                _appMutex.Dispose();
            }
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
                    Process.Start("xdg-open", url);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open browser: {ex.Message}. Please navigate to {url} manually.");
            }
        }

        private static async Task<string> GetLinuxSelectedTextAsync()
        {
            // Only the tools that can work in this session are tried: wl-paste without
            // a Wayland display, or xclip without an X display, can only fail.
            bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            bool x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

            var attempts = new System.Collections.Generic.List<(string cmd, string args)>();

            // The primary selection (whatever is highlighted) first...
            if (wayland) attempts.Add(("wl-paste", "--primary --no-newline"));
            if (x11)
            {
                attempts.Add(("xclip", "-o -selection primary"));
                attempts.Add(("xsel", "-o -p"));
            }

            // ...then the clipboard.
            if (wayland) attempts.Add(("wl-paste", "--no-newline"));
            if (x11)
            {
                attempts.Add(("xclip", "-o -selection clipboard"));
                attempts.Add(("xsel", "-o -b"));
            }

            foreach (var (cmd, args) in attempts)
            {
                if (!Platform.CommandExists(cmd)) continue;

                string text = await RunCommandAsync(cmd, args);
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }

            return "";
        }

        private static async Task<string> RunCommandAsync(string cmd, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return "";

                // A clipboard tool waits on whichever application owns the selection,
                // and one that never answers would hang the hotkey for good.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    // Both pipes are drained together; reading one to the end while the
                    // other fills would stall the child.
                    var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                    string output = await stdout;
                    await stderr;
                    await process.WaitForExitAsync(timeout.Token);

                    if (process.ExitCode == 0)
                    {
                        return output;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"{cmd} did not answer in time; skipping it.");
                    try { process.Kill(entireProcessTree: true); }
                    catch { }
                }
            }
            catch
            {
                // Command probably not installed
            }
            return "";
        }
    }
}
