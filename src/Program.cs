using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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
                    OpenBrowser($"http://127.0.0.1:{Port}");
                    break;
                case "--stop":
                    await SendCommandToDaemonAsync("STOP");
                    break;
                case "--set-provider":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Please specify provider name.");
                        return;
                    }
                    SetSetting(s => s.TTSProvider = args[1]);
                    Console.WriteLine($"TTS Provider set to {args[1]}");
                    break;
                case "--set-voice":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Please specify voice name.");
                        return;
                    }
                    SetSetting(s => {
                        var prov = s.TTSProvider;
                        if (prov == "Piper") s.PiperVoice = args[1];
                        else if (prov == "Kokoro ONNX") s.KokoroVoice = args[1];
                        else s.SelectedVoice = args[1];
                    });
                    Console.WriteLine($"Voice set to {args[1]}");
                    break;
                case "--set-rate":
                    if (args.Length < 2 || !int.TryParse(args[1], out int rate))
                    {
                        Console.WriteLine("Error: Please specify an integer rate (-10 to 10).");
                        return;
                    }
                    SetSetting(s => s.SpeechRate = rate);
                    Console.WriteLine($"Speech rate set to {rate}");
                    break;
                case "--list-voices":
                    await ListVoicesAsync();
                    break;
                case "--list-providers":
                    Console.WriteLine("Available TTS Providers:");
                    Console.WriteLine(" - Kokoro ONNX");
                    Console.WriteLine(" - Piper");
#if WINDOWS
                    Console.WriteLine(" - Windows");
#endif
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
                    break;
            }
        }

        private static void SetSetting(Action<AppSettings> updateAction)
        {
            var settings = AppSettings.Load();
            updateAction(settings);
            settings.Save();
            
            // Notify daemon if running
            _ = SendCommandToDaemonAsync("RELOAD_SETTINGS");
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
            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            
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

            // Try to send to daemon
            bool success = await SendCommandToDaemonAsync("SPEAK:" + selectedText);
            if (!success)
            {
                // Daemon not running, start it
                Console.WriteLine("Daemon not running. Starting background daemon...");
                StartDaemonProcess();
                
                // Try to connect up to 5 times
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(400);
                    success = await SendCommandToDaemonAsync("SPEAK:" + selectedText);
                    if (success) break;
                }

                if (!success)
                {
                    // Fallback: speak in-process
                    Console.WriteLine("Failed to connect to daemon. Speaking in-process (will have startup latency)...");
                    await SpeechManager.Instance.SpeakAsync(selectedText);
                }
            }
        }

        private static void StartDaemonProcess()
        {
            string currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;
            
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = "--daemon",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        private static async Task<bool> SendCommandToDaemonAsync(string command)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(2);
                HttpResponseMessage response;
                
                if (command == "STOP")
                {
                    response = await client.PostAsync($"http://127.0.0.1:{Port}/api/stop", null);
                }
                else if (command == "RELOAD_SETTINGS")
                {
                    response = await client.PostAsync($"http://127.0.0.1:{Port}/api/settings", null);
                }
                else if (command.StartsWith("SPEAK:"))
                {
                    string text = command.Substring(6);
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { text = text }), 
                        Encoding.UTF8, 
                        "application/json"
                    );
                    response = await client.PostAsync($"http://127.0.0.1:{Port}/api/speak", content);
                }
                else
                {
                    return false;
                }
                
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static async Task RunDaemonAsync()
        {
            _appMutex = new Mutex(true, "Global\\NarratorHotkey_Tray_Mutex", out bool createdNew);
            if (!createdNew)
            {
                Console.WriteLine("Daemon or Tray Application is already running.");
                return;
            }

            try
            {
                // Start Web server / Daemon API
                WebGui.Start(Port);
                Console.WriteLine($"Daemon started. Web configuration dashboard and API running at http://127.0.0.1:{Port}...");
                
                // Initialize speech manager
                await SpeechManager.Instance.ApplySettingsAsync();

                // Keep the process alive until terminated
                var tcs = new TaskCompletionSource<bool>();
                
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    tcs.TrySetResult(true);
                };
                
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    tcs.TrySetResult(true);
                };

                await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Daemon error: {ex.Message}");
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
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
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
            // Try wl-paste (Wayland) primary selection
            string text = await RunCommandAsync("wl-paste", "--primary");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

            // Try xclip (X11) primary selection
            text = await RunCommandAsync("xclip", "-o -selection primary");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

            // Try xsel (X11) primary selection
            text = await RunCommandAsync("xsel", "-o -p");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

            // Fallback to clipboard: wl-paste
            text = await RunCommandAsync("wl-paste", "");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

            // Fallback to clipboard: xclip
            text = await RunCommandAsync("xclip", "-o -selection clipboard");
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

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

                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    return output;
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
