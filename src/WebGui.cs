using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NarratorHotkey.Speech;

namespace NarratorHotkey
{
    public static class WebGui
    {
        private static HttpListener _listener;
        private static bool _isRunning;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();

                Task.Run(async () =>
                {
                    while (_isRunning)
                    {
                        try
                        {
                            var context = await _listener.GetContextAsync();
                            _ = HandleRequestAsync(context);
                        }
                        catch
                        {
                            // Ignore exceptions when listener stops
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _isRunning = false;
                Console.WriteLine($"Failed to start HTTP listener on port {port}: {ex.Message}");
#if WINDOWS
                System.Windows.Forms.MessageBox.Show($"Failed to start HTTP listener on port {port}: {ex.Message}\n\nEnsure another instance is not running and the port is free.",
                    "Web GUI Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
#endif
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Enable CORS
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            try
            {
                string path = request.Url.AbsolutePath.ToLowerInvariant();
                if (path == "/" || path == "/index.html")
                {
                    await ServeHtmlAsync(response);
                }
                else if (path == "/api/status" && request.HttpMethod == "GET")
                {
                    await ServeStatusAsync(response);
                }
                else if (path == "/api/providers" && request.HttpMethod == "GET")
                {
                    await ServeProvidersAsync(response);
                }
                else if (path == "/api/voices" && request.HttpMethod == "GET")
                {
                    string provider = request.QueryString["provider"];
                    await ServeVoicesAsync(response, provider);
                }
                else if (path == "/api/settings" && request.HttpMethod == "POST")
                {
                    await UpdateSettingsAsync(request, response);
                }
                else if (path == "/api/speak" && request.HttpMethod == "POST")
                {
                    await HandleSpeakAsync(request, response);
                }
                else if (path == "/api/stop" && request.HttpMethod == "POST")
                {
                    await HandleStopAsync(response);
                }
                else if (path == "/api/clear-logs" && request.HttpMethod == "POST")
                {
                    await HandleClearLogsAsync(response);
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling web request: {ex.Message}");
                response.StatusCode = 500;
                response.Close();
            }
        }

        private static async Task ServeHtmlAsync(HttpListenerResponse response)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(HtmlContent);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static async Task ServeStatusAsync(HttpListenerResponse response)
        {
            var settings = AppSettings.Load();
            var history = SpeechManager.Instance.SpeechHistory;

            var data = new
            {
                settings = new
                {
                    selectedVoice = settings.SelectedVoice,
                    speechRate = settings.SpeechRate,
                    ttsProvider = settings.TTSProvider,
                    piperVoice = settings.PiperVoice,
                    windowsNaturalVoice = settings.WindowsNaturalVoice,
                    kokoroVoice = settings.KokoroVoice,
                    enableProgressiveChunking = settings.EnableProgressiveChunking,
                    hotkeyModifier = settings.HotkeyModifier,
                    hotkeyKey = settings.HotkeyKey
                },
                history = history,
                isSpeaking = SpeechManager.Instance.IsSpeaking
            };

            string json = JsonSerializer.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static async Task ServeProvidersAsync(HttpListenerResponse response)
        {
            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            string[] providers;
            
            if (isWindows)
            {
                providers = new[] { "Kokoro ONNX", "Piper", "Windows" };
            }
            else
            {
                providers = new[] { "Kokoro ONNX", "Piper" };
            }

            string json = JsonSerializer.Serialize(providers);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static async Task ServeVoicesAsync(HttpListenerResponse response, string provider)
        {
            if (string.IsNullOrEmpty(provider))
            {
                var settings = AppSettings.Load();
                provider = settings.TTSProvider;
            }

            string[] voices = await SpeechManager.Instance.GetVoicesForProviderAsync(provider);
            string json = JsonSerializer.Serialize(voices);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private class SettingsUpdateModel
        {
            public string ttsProvider { get; set; }
            public string voice { get; set; }
            public int speechRate { get; set; }
            public bool enableProgressiveChunking { get; set; }
            public string hotkeyModifier { get; set; }
            public string hotkeyKey { get; set; }
        }

        private static async Task UpdateSettingsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                string body = await reader.ReadToEndAsync();
                var model = JsonSerializer.Deserialize<SettingsUpdateModel>(body);

                if (model != null)
                {
                    var settings = AppSettings.Load();
                    if (!string.IsNullOrEmpty(model.ttsProvider))
                    {
                        settings.TTSProvider = model.ttsProvider;
                    }
                    if (!string.IsNullOrEmpty(model.voice))
                    {
                        if (settings.TTSProvider == "Piper") settings.PiperVoice = model.voice;
                        else if (settings.TTSProvider == "Kokoro ONNX") settings.KokoroVoice = model.voice;
                        else settings.SelectedVoice = model.voice;
                    }
                    settings.SpeechRate = model.speechRate;
                    settings.EnableProgressiveChunking = model.enableProgressiveChunking;
                    if (!string.IsNullOrEmpty(model.hotkeyModifier))
                    {
                        settings.HotkeyModifier = model.hotkeyModifier;
                    }
                    if (!string.IsNullOrEmpty(model.hotkeyKey))
                    {
                        settings.HotkeyKey = model.hotkeyKey;
                    }

                    settings.Save();

                    // Notify hotkey manager
                    Program.NotifySettingsChanged();
                    
                    // Reload SpeechManager settings
                    await SpeechManager.Instance.ApplySettingsAsync();
                }
            }

            response.StatusCode = 200;
            response.Close();
        }

        private class SpeakRequestModel
        {
            public string text { get; set; }
            public string ttsProvider { get; set; }
            public string voice { get; set; }
        }

        private static async Task HandleSpeakAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                string body = await reader.ReadToEndAsync();
                var model = JsonSerializer.Deserialize<SpeakRequestModel>(body);
                if (model != null && !string.IsNullOrWhiteSpace(model.text))
                {
                    if (SpeechManager.Instance.IsSpeaking)
                    {
                        await SpeechManager.Instance.StopAsync();
                    }

                    if (!string.IsNullOrEmpty(model.ttsProvider))
                    {
                        await SpeechManager.Instance.ApplyTemporarySettingsAsync(model.ttsProvider, model.voice);
                    }

                    _ = SpeechManager.Instance.SpeakAsync(model.text);
                }
            }
            response.StatusCode = 200;
            response.Close();
        }

        private static async Task HandleStopAsync(HttpListenerResponse response)
        {
            await SpeechManager.Instance.StopAsync();
            response.StatusCode = 200;
            response.Close();
        }

        private static Task HandleClearLogsAsync(HttpListenerResponse response)
        {
            SpeechManager.Instance.ClearSpeechLog();
            response.StatusCode = 200;
            response.Close();
            return Task.CompletedTask;
        }

        private const string HtmlContent = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Narrator Hotkey Settings</title>
    <link rel="icon" href="data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 100 100%22><text y=%22.9em%22 font-size=%2290%22>🔊</text></svg>">
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&display=swap');

        :root {
            --bg-dark: #07090e;
            --bg-panel: rgba(18, 20, 32, 0.6);
            --bg-card: rgba(30, 33, 50, 0.4);
            --primary: #8b5cf6;
            --primary-hover: #7c3aed;
            --primary-glow: rgba(139, 92, 246, 0.25);
            --secondary: #3b82f6;
            --text-main: #f3f4f6;
            --text-muted: #9ca3af;
            --border: rgba(255, 255, 255, 0.08);
            --border-hover: rgba(255, 255, 255, 0.15);
            --success: #10b981;
            --error: #ef4444;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }

        body {
            background-color: var(--bg-dark);
            color: var(--text-main);
            font-family: 'Outfit', sans-serif;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            background-image: 
                radial-gradient(at 10% 10%, rgba(139, 92, 246, 0.1) 0px, transparent 50%),
                radial-gradient(at 90% 90%, rgba(59, 130, 246, 0.1) 0px, transparent 50%);
            background-attachment: fixed;
            overflow-x: hidden;
            padding: 24px;
        }

        .container {
            width: 100%;
            max-width: 1100px;
            background: var(--bg-panel);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid var(--border);
            border-radius: 24px;
            box-shadow: 0 20px 50px rgba(0, 0, 0, 0.5);
            padding: 32px;
            animation: fadeIn 0.6s ease-out;
        }

        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(15px); }
            to { opacity: 1; transform: translateY(0); }
        }

        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 32px;
            border-bottom: 1px solid var(--border);
            padding-bottom: 20px;
        }

        .logo-area {
            display: flex;
            align-items: center;
            gap: 16px;
        }

        .logo-icon {
            font-size: 32px;
            background: linear-gradient(135deg, var(--primary), var(--secondary));
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            animation: pulseGlow 3s infinite;
        }

        @keyframes pulseGlow {
            0%, 100% { filter: drop-shadow(0 0 2px var(--primary)); }
            50% { filter: drop-shadow(0 0 8px var(--primary)); }
        }

        h1 {
            font-size: 26px;
            font-weight: 600;
            background: linear-gradient(135deg, #ffffff, #a5b4fc);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .status-badge {
            display: flex;
            align-items: center;
            gap: 8px;
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--border);
            padding: 6px 14px;
            border-radius: 99px;
            font-size: 14px;
            font-weight: 500;
        }

        .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background-color: var(--success);
            box-shadow: 0 0 8px var(--success);
        }

        .status-dot.speaking {
            background-color: var(--secondary);
            box-shadow: 0 0 8px var(--secondary);
            animation: blink 1s infinite alternate;
        }

        @keyframes blink {
            from { opacity: 0.4; }
            to { opacity: 1; }
        }

        .grid {
            display: grid;
            grid-template-columns: 1.1fr 0.9fr;
            gap: 32px;
        }

        @media (max-width: 900px) {
            .grid {
                grid-template-columns: 1fr;
            }
        }

        .section-title {
            font-size: 18px;
            font-weight: 600;
            margin-bottom: 20px;
            display: flex;
            align-items: center;
            gap: 10px;
            color: #c7d2fe;
        }

        .form-group {
            margin-bottom: 24px;
        }

        label {
            display: block;
            font-size: 14px;
            font-weight: 500;
            margin-bottom: 8px;
            color: var(--text-muted);
        }

        /* Providers Selection */
        .providers-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(130px, 1fr));
            gap: 12px;
            margin-bottom: 12px;
        }

        .provider-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 16px;
            text-align: center;
            cursor: pointer;
            transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 8px;
        }

        .provider-card:hover {
            border-color: var(--border-hover);
            transform: translateY(-2px);
        }

        .provider-card.active {
            background: rgba(139, 92, 246, 0.1);
            border-color: var(--primary);
            box-shadow: 0 0 15px var(--primary-glow);
        }

        .provider-icon {
            font-size: 20px;
        }

        .provider-name {
            font-size: 13px;
            font-weight: 500;
        }

        /* Select Dropdowns */
        select {
            width: 100%;
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 12px 16px;
            color: var(--text-main);
            font-family: inherit;
            font-size: 15px;
            outline: none;
            cursor: pointer;
            transition: all 0.3s;
        }

        select:focus {
            border-color: var(--primary);
            box-shadow: 0 0 10px var(--primary-glow);
        }

        /* Sliders */
        .slider-container {
            display: flex;
            align-items: center;
            gap: 16px;
        }

        input[type="range"] {
            flex: 1;
            -webkit-appearance: none;
            appearance: none;
            height: 6px;
            border-radius: 99px;
            background: var(--border);
            outline: none;
        }

        input[type="range"]::-webkit-slider-thumb {
            -webkit-appearance: none;
            appearance: none;
            width: 18px;
            height: 18px;
            border-radius: 50%;
            background: var(--primary);
            cursor: pointer;
            box-shadow: 0 0 10px rgba(139, 92, 246, 0.5);
            transition: transform 0.1s;
        }

        input[type="range"]::-webkit-slider-thumb:hover {
            transform: scale(1.2);
        }

        .slider-val {
            font-size: 15px;
            font-weight: 600;
            width: 32px;
            text-align: right;
            color: var(--primary);
        }

        /* Checkbox Switch */
        .switch-container {
            display: flex;
            align-items: center;
            justify-content: space-between;
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 16px;
            margin-top: 16px;
        }

        .switch {
            position: relative;
            display: inline-block;
            width: 44px;
            height: 24px;
        }

        .switch input {
            opacity: 0;
            width: 0;
            height: 0;
        }

        .slider {
            position: absolute;
            cursor: pointer;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background-color: var(--border);
            transition: .3s;
            border-radius: 24px;
        }

        .slider:before {
            position: absolute;
            content: "";
            height: 18px;
            width: 18px;
            left: 3px;
            bottom: 3px;
            background-color: white;
            transition: .3s;
            border-radius: 50%;
        }

        input:checked + .slider {
            background-color: var(--primary);
        }

        input:checked + .slider:before {
            transform: translateX(20px);
        }

        /* Buttons */
        .btn {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            padding: 12px 24px;
            border-radius: 12px;
            font-family: inherit;
            font-size: 15px;
            font-weight: 600;
            cursor: pointer;
            border: none;
            transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
            outline: none;
        }

        .btn-primary {
            background: linear-gradient(135deg, var(--primary), var(--primary-hover));
            color: white;
            box-shadow: 0 4px 15px rgba(139, 92, 246, 0.3);
        }

        .btn-primary:hover {
            transform: translateY(-1px);
            box-shadow: 0 6px 20px rgba(139, 92, 246, 0.4);
        }

        .btn-primary:active {
            transform: translateY(1px);
        }

        .btn-outline {
            background: transparent;
            border: 1px solid var(--border);
            color: var(--text-main);
        }

        .btn-outline:hover {
            background: rgba(255, 255, 255, 0.03);
            border-color: var(--border-hover);
        }

        .btn-error {
            background: rgba(239, 68, 68, 0.1);
            border: 1px solid rgba(239, 68, 68, 0.2);
            color: var(--error);
        }

        .btn-error:hover {
            background: rgba(239, 68, 68, 0.2);
        }

        .actions-bar {
            display: flex;
            justify-content: flex-end;
            gap: 12px;
            margin-top: 24px;
        }

        /* Tester Area */
        .tester-area {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 24px;
            margin-bottom: 28px;
        }

        textarea {
            width: 100%;
            height: 100px;
            background: rgba(10, 11, 18, 0.5);
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 12px;
            color: var(--text-main);
            font-family: inherit;
            font-size: 14px;
            outline: none;
            resize: none;
            margin-bottom: 16px;
            transition: all 0.3s;
        }

        textarea:focus {
            border-color: var(--primary);
            box-shadow: 0 0 8px var(--primary-glow);
        }

        .tester-buttons {
            display: flex;
            gap: 12px;
        }

        /* Speech History Logger */
        .history-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 24px;
            display: flex;
            flex-direction: column;
            max-height: 330px;
        }

        .history-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
        }

        .history-list {
            overflow-y: auto;
            flex: 1;
            padding-right: 4px;
        }

        .history-list::-webkit-scrollbar {
            width: 6px;
        }

        .history-list::-webkit-scrollbar-track {
            background: transparent;
        }

        .history-list::-webkit-scrollbar-thumb {
            background: var(--border);
            border-radius: 10px;
        }

        .history-item {
            padding: 12px;
            border-bottom: 1px solid rgba(255, 255, 255, 0.03);
            display: flex;
            flex-direction: column;
            gap: 4px;
        }

        .history-item:last-child {
            border-bottom: none;
        }

        .history-item-header {
            display: flex;
            justify-content: space-between;
            font-size: 12px;
            color: var(--text-muted);
        }

        .history-text {
            font-size: 14px;
            line-height: 1.4;
            color: var(--text-main);
            word-break: break-word;
        }

        .tag {
            font-size: 10px;
            font-weight: 600;
            padding: 2px 6px;
            border-radius: 4px;
            background: rgba(139, 92, 246, 0.15);
            color: #c7d2fe;
            text-transform: uppercase;
        }

        /* Toasts */
        .toast {
            position: fixed;
            bottom: 24px;
            right: 24px;
            background: #121420;
            border: 1px solid var(--success);
            padding: 16px 24px;
            border-radius: 12px;
            display: flex;
            align-items: center;
            gap: 12px;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
            transform: translateY(150%);
            transition: transform 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
            z-index: 1000;
        }

        .toast.show {
            transform: translateY(0);
        }

        .toast-icon {
            color: var(--success);
            font-size: 20px;
        }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="logo-area">
                <span class="logo-icon">🔊</span>
                <div>
                    <h1>Narrator Hotkey</h1>
                </div>
            </div>
            <div class="status-badge">
                <span id="statusDot" class="status-dot"></span>
                <span id="statusText">Daemon Ready</span>
            </div>
        </header>

        <div class="grid">
            <!-- Left Side: Config -->
            <div class="panel">
                <div class="section-title">⚙️ Settings</div>
                
                <div class="form-group">
                    <label>Text-to-Speech Engine</label>
                    <div id="providersList" class="providers-grid">
                        <!-- Loaded dynamically -->
                    </div>
                </div>

                <div class="form-group">
                    <label for="voiceSelect">Voice Model</label>
                    <select id="voiceSelect">
                        <!-- Loaded dynamically -->
                    </select>
                </div>

                <div class="form-group">
                    <label>Speaking Speed</label>
                    <div class="slider-container">
                        <input type="range" id="rateSlider" min="-10" max="10" value="0">
                        <span id="rateValue" class="slider-val">0</span>
                    </div>
                </div>

                <div class="form-group">
                    <div class="switch-container">
                        <div>
                            <div style="font-size: 15px; font-weight: 500;">Progressive Chunking</div>
                            <div style="font-size: 12px; color: var(--text-muted); margin-top: 2px;">Reduces latency by generating sentences in parts</div>
                        </div>
                        <label class="switch">
                            <input type="checkbox" id="chunkingSwitch">
                            <span class="slider"></span>
                        </label>
                    </div>
                </div>

                <div class="form-group">
                    <label>Windows Global Hotkey</label>
                    <div style="display: flex; gap: 12px; margin-bottom: 8px;">
                        <select id="hotkeyModSelect" style="flex: 1;">
                            <option value="Control">Control</option>
                            <option value="Alt">Alt</option>
                            <option value="Shift">Shift</option>
                            <option value="None">None</option>
                        </select>
                        <select id="hotkeyKeySelect" style="flex: 1;">
                            <!-- Populated dynamically in JS -->
                        </select>
                    </div>
                    <div style="font-size: 11px; color: var(--text-muted);">
                        Note: On Linux, configure global hotkeys via your desktop shortcut settings (mapping to this CLI).
                    </div>
                </div>

                <div class="actions-bar">
                    <button id="saveBtn" class="btn btn-primary">Save Configuration</button>
                </div>
            </div>

            <!-- Right Side: Tester & History -->
            <div>
                <div class="tester-area">
                    <div class="section-title">🗣️ Test Speech</div>
                    <textarea id="testText" placeholder="Type something to hear it...">This is a test of the Narrator Hotkey text to speech system.</textarea>
                    <div class="tester-buttons">
                        <button id="speakBtn" class="btn btn-primary" style="flex: 1;">🔊 Speak</button>
                        <button id="stopBtn" class="btn btn-outline">⏹️ Stop</button>
                    </div>
                </div>

                <div class="history-card">
                    <div class="history-header">
                        <div class="section-title" style="margin-bottom: 0;">📜 Speech Log</div>
                        <button id="clearLogsBtn" class="btn btn-error" style="padding: 6px 12px; font-size: 12px;">Clear</button>
                    </div>
                    <div id="historyList" class="history-list">
                        <!-- Logs loaded dynamically -->
                    </div>
                </div>
            </div>
        </div>
    </div>

    <!-- Toast Notification -->
    <div id="toast" class="toast">
        <span class="toast-icon">✓</span>
        <span id="toastMessage">Settings saved successfully</span>
    </div>

    <script>
        const API_BASE = '';

        // Dom Elements
        const providersList = document.getElementById('providersList');
        const voiceSelect = document.getElementById('voiceSelect');
        const rateSlider = document.getElementById('rateSlider');
        const rateValue = document.getElementById('rateValue');
        const chunkingSwitch = document.getElementById('chunkingSwitch');
        const saveBtn = document.getElementById('saveBtn');
        
        const testText = document.getElementById('testText');
        const speakBtn = document.getElementById('speakBtn');
        const stopBtn = document.getElementById('stopBtn');
        
        const historyList = document.getElementById('historyList');
        const clearLogsBtn = document.getElementById('clearLogsBtn');
        
        const statusDot = document.getElementById('statusDot');
        const statusText = document.getElementById('statusText');
        const toast = document.getElementById('toast');
        const hotkeyModSelect = document.getElementById('hotkeyModSelect');
        const hotkeyKeySelect = document.getElementById('hotkeyKeySelect');

        // Populate Hotkey Keys dropdown
        const keysList = [
            '1','2','3','4','5','6','7','8','9','0',
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
            'F1','F2','F3','F4','F5','F6','F7','F8','F9','F10','F11','F12'
        ];
        keysList.forEach(k => {
            const opt = document.createElement('option');
            opt.value = k;
            opt.innerText = k;
            hotkeyKeySelect.appendChild(opt);
        });

        let currentSettings = {};

        rateSlider.addEventListener('input', (e) => {
            rateValue.innerText = e.target.value > 0 ? `+${e.target.value}` : e.target.value;
        });

        // Poll status (speaking status and speech log) every 1 second
        async function pollStatus() {
            try {
                const res = await fetch(`${API_BASE}/api/status`);
                const data = await res.json();
                
                // Update UI status dot
                if (data.isSpeaking) {
                    statusDot.className = 'status-dot speaking';
                    statusText.innerText = 'Speaking...';
                } else {
                    statusDot.className = 'status-dot';
                    statusText.innerText = 'Daemon Ready';
                }

                // Render history
                renderHistory(data.history);
            } catch (err) {
                console.error('Failed to poll status:', err);
                statusDot.className = 'status-dot';
                statusDot.style.backgroundColor = 'var(--error)';
                statusDot.style.boxShadow = '0 0 8px var(--error)';
                statusText.innerText = 'Offline';
            }
        }

        // Load settings from server
        async function loadSettingsFromServer() {
            try {
                const res = await fetch(`${API_BASE}/api/status`);
                const data = await res.json();
                currentSettings = data.settings;
                
                // Set values
                rateSlider.value = currentSettings.speechRate;
                rateValue.innerText = currentSettings.speechRate > 0 ? `+${currentSettings.speechRate}` : currentSettings.speechRate;
                chunkingSwitch.checked = currentSettings.enableProgressiveChunking;

                hotkeyModSelect.value = currentSettings.hotkeyModifier;
                hotkeyKeySelect.value = currentSettings.hotkeyKey;

                // Update UI status dot
                if (data.isSpeaking) {
                    statusDot.className = 'status-dot speaking';
                    statusText.innerText = 'Speaking...';
                } else {
                    statusDot.className = 'status-dot';
                    statusText.innerText = 'Daemon Ready';
                }

                // Render history
                renderHistory(data.history);
                
                // Initialize providers lists if not loaded yet
                if (providersList.children.length === 0) {
                    await loadProviders();
                }
            } catch (err) {
                console.error('Failed to load settings:', err);
                statusDot.className = 'status-dot';
                statusDot.style.backgroundColor = 'var(--error)';
                statusDot.style.boxShadow = '0 0 8px var(--error)';
                statusText.innerText = 'Offline';
            }
        }

        async function loadProviders() {
            try {
                const res = await fetch(`${API_BASE}/api/providers`);
                const providers = await res.json();
                providersList.innerHTML = '';

                providers.forEach(prov => {
                    const card = document.createElement('div');
                    card.className = `provider-card ${currentSettings.ttsProvider === prov ? 'active' : ''}`;
                    card.setAttribute('data-provider', prov);
                    
                    let icon = '🤖';
                    if (prov.includes('Windows')) icon = '🪟';
                    else if (prov.includes('Piper')) icon = '🐸';
                    else if (prov.includes('Kokoro')) icon = '🌸';

                    card.innerHTML = `
                        <span class="provider-icon">${icon}</span>
                        <span class="provider-name">${prov}</span>
                    `;

                    card.addEventListener('click', () => {
                        document.querySelectorAll('.provider-card').forEach(c => c.classList.remove('active'));
                        card.classList.add('active');
                        currentSettings.ttsProvider = prov;
                        loadVoices(prov);
                    });

                    providersList.appendChild(card);
                });

                // Load voices for current provider
                await loadVoices(currentSettings.ttsProvider);
            } catch (err) {
                console.error('Failed to load providers:', err);
            }
        }

        async function loadVoices(provider) {
            try {
                const res = await fetch(`${API_BASE}/api/voices?provider=${encodeURIComponent(provider)}`);
                const voices = await res.json();
                
                voiceSelect.innerHTML = '';
                voices.forEach(voice => {
                    const opt = document.createElement('option');
                    opt.value = voice;
                    opt.innerText = voice;
                    voiceSelect.appendChild(opt);
                });

                // Set selected voice
                let currentVoice = currentSettings.selectedVoice;
                if (provider === 'Piper') currentVoice = currentSettings.piperVoice;
                else if (provider === 'Kokoro ONNX') currentVoice = currentSettings.kokoroVoice;

                if (voices.includes(currentVoice)) {
                    voiceSelect.value = currentVoice;
                }
            } catch (err) {
                console.error('Failed to load voices:', err);
            }
        }

        function renderHistory(logs) {
            historyList.innerHTML = '';
            if (!logs || logs.length === 0) {
                historyList.innerHTML = '<div style="text-align: center; color: var(--text-muted); padding: 20px; font-size: 14px;">No speech logs yet.</div>';
                return;
            }

            logs.forEach(log => {
                const item = document.createElement('div');
                item.className = 'history-item';
                
                const time = new Date(log.Timestamp).toLocaleTimeString();
                item.innerHTML = `
                    <div class="history-item-header">
                        <span>${time}</span>
                        <span class="tag">${log.Provider}</span>
                    </div>
                    <div class="history-text">${log.CleanedText}</div>
                `;
                historyList.appendChild(item);
            });
        }

        // Save settings handler
        saveBtn.addEventListener('click', async () => {
            const payload = {
                ttsProvider: currentSettings.ttsProvider,
                voice: voiceSelect.value,
                speechRate: parseInt(rateSlider.value),
                enableProgressiveChunking: chunkingSwitch.checked,
                hotkeyModifier: hotkeyModSelect.value,
                hotkeyKey: hotkeyKeySelect.value
            };

            try {
                saveBtn.disabled = true;
                saveBtn.innerText = 'Saving...';
                
                const res = await fetch(`${API_BASE}/api/settings`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                });
                
                if (res.ok) {
                    showToast('Configuration saved successfully');
                    await loadSettingsFromServer();
                } else {
                    showToast('Failed to save configuration', true);
                }
            } catch (err) {
                console.error('Save failed:', err);
                showToast('Network error saving configuration', true);
            } finally {
                saveBtn.disabled = false;
                saveBtn.innerText = 'Save Configuration';
            }
        });

        // Tester triggers
        speakBtn.addEventListener('click', async () => {
            const text = testText.value;
            if (!text.trim()) return;

            try {
                await fetch(`${API_BASE}/api/speak`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ 
                        text,
                        ttsProvider: currentSettings.ttsProvider,
                        voice: voiceSelect.value
                    })
                });
            } catch (err) {
                console.error('Speak request failed:', err);
            }
        });

        stopBtn.addEventListener('click', async () => {
            try {
                await fetch(`${API_BASE}/api/stop`, { method: 'POST' });
            } catch (err) {
                console.error('Stop request failed:', err);
            }
        });

        clearLogsBtn.addEventListener('click', async () => {
            try {
                const res = await fetch(`${API_BASE}/api/clear-logs`, { method: 'POST' });
                if (res.ok) {
                    pollStatus();
                }
            } catch (err) {
                console.error('Clear logs failed:', err);
            }
        });

        // Toast Helper
        function showToast(message, isError = false) {
            toast.className = 'toast show';
            document.getElementById('toastMessage').innerText = message;
            const icon = document.querySelector('.toast-icon');
            if (isError) {
                toast.style.borderColor = 'var(--error)';
                icon.innerText = '✗';
                icon.style.color = 'var(--error)';
            } else {
                toast.style.borderColor = 'var(--success)';
                icon.innerText = '✓';
                icon.style.color = 'var(--success)';
            }

            setTimeout(() => {
                toast.className = 'toast';
            }, 3000);
        }

        // Poll status every 1 second
        setInterval(pollStatus, 1000);

        // First load
        loadSettingsFromServer();
    </script>
</body>
</html>
""";
    }
}
