namespace NarratorHotkey;

using System;
using System.Windows.Forms;
using static Interoperability;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

public class HotkeyManager : IMessageFilter
{
    private const int HOTKEY_ID_READ = 1;
    private const int HOTKEY_ID_PAUSE = 2;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>
    /// One global hotkey: what it is bound to now, and what it does when pressed.
    /// </summary>
    private sealed class Binding
    {
        public Binding(int id, string description, Action action)
        {
            Id = id;
            Description = description;
            Action = action;
        }

        public int Id { get; }
        public string Description { get; }
        public Action Action { get; }

        public bool IsRegistered { get; set; }

        // What is currently registered, so a settings save that leaves the hotkey alone
        // does not tear down and re-register a working binding.
        public string RegisteredModifier { get; set; }
        public string RegisteredKey { get; set; }
    }

    private readonly Binding[] _bindings;

    // RegisterHotKey(IntPtr.Zero, ...) is thread-affine: the hotkey belongs to the
    // thread that registered it, and only that thread can unregister it. Everything
    // here must therefore run on the thread that owns the message loop.
    private readonly int _ownerThreadId;

    public HotkeyManager(Action onHotkeyTriggered, Action onPauseTriggered)
    {
        _bindings = new[]
        {
            new Binding(HOTKEY_ID_READ, "read", onHotkeyTriggered),
            new Binding(HOTKEY_ID_PAUSE, "pause/resume", onPauseTriggered)
        };
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Application.AddMessageFilter(this);
        RegisterHotKey();
    }

    private bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == Interoperability.WM_HOTKEY)
        {
            Console.WriteLine($"[HotkeyManager] Thread-level message received! Msg: {m.Msg:X4}, WParam: {m.WParam.ToInt64():X}, LParam: {m.LParam.ToInt64():X}");
            int id = m.WParam.ToInt32();
            foreach (var binding in _bindings)
            {
                if (binding.Id == id)
                {
                    binding.Action?.Invoke();
                    return true; // Eat the message so other components do not process it
                }
            }
        }
        return false;
    }

    public void RegisterHotKey()
    {
        if (!OnOwnerThread)
        {
            Console.WriteLine("[HotkeyManager] ERROR: RegisterHotKey called off the message-loop thread; ignoring.");
            return;
        }

        var settings = AppSettings.Load();
        foreach (var binding in _bindings)
        {
            if (binding.IsRegistered) continue;
            var (modifierName, keyName) = BindingFor(binding, settings);
            Register(binding, modifierName, keyName);
        }
    }

    private static (string modifier, string key) BindingFor(Binding binding, AppSettings settings)
    {
        return binding.Id == HOTKEY_ID_PAUSE
            ? (settings.PauseHotkeyModifier, settings.PauseHotkeyKey)
            : (settings.HotkeyModifier, settings.HotkeyKey);
    }

    private void Register(Binding binding, string modifierName, string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            // No key configured: the binding is simply off.
            Console.WriteLine($"[HotkeyManager] No key configured for the {binding.Description} hotkey; skipping registration.");
            return;
        }

        uint modifier = Interoperability.GetModifierCode(modifierName);
        uint key = Interoperability.GetKeyCode(keyName);

        binding.IsRegistered = Interoperability.RegisterHotKey(
            IntPtr.Zero,
            binding.Id,
            modifier,
            key);

        Console.WriteLine($"[HotkeyManager] Thread-level RegisterHotKey result for {binding.Description}: {binding.IsRegistered} (Modifier: {modifierName}, Key: {keyName})");

        if (binding.IsRegistered)
        {
            binding.RegisteredModifier = modifierName;
            binding.RegisteredKey = keyName;
            return;
        }

        binding.RegisteredModifier = null;
        binding.RegisteredKey = null;

        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Console.WriteLine($"[HotkeyManager] ERROR: Failed to register thread-level {binding.Description} hotkey ({modifierName}+{keyName}). Win32 error {error}.");

        string reason = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "Another application has already claimed it."
            : $"Windows reported error {error}.";

        MessageBox.Show($"Could not register the {binding.Description} hotkey ({modifierName}+{keyName}). {reason}",
            "Hotkey Registration Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    public void UnregisterHotKey()
    {
        if (!OnOwnerThread)
        {
            // Unregistering from another thread silently does nothing, which would
            // leave the hotkey held and make the next registration fail.
            Console.WriteLine("[HotkeyManager] ERROR: UnregisterHotKey called off the message-loop thread; ignoring.");
            return;
        }

        foreach (var binding in _bindings)
        {
            Unregister(binding);
        }

        try
        {
            Application.RemoveMessageFilter(this);
        }
        catch { }
    }

    private void Unregister(Binding binding)
    {
        if (!binding.IsRegistered) return;

        Interoperability.UnregisterHotKey(IntPtr.Zero, binding.Id);
        binding.IsRegistered = false;
        binding.RegisteredModifier = null;
        binding.RegisteredKey = null;
    }

    public void ReloadHotKey()
    {
        if (!OnOwnerThread)
        {
            Console.WriteLine("[HotkeyManager] ERROR: ReloadHotKey called off the message-loop thread; ignoring.");
            return;
        }

        var settings = AppSettings.Load();
        foreach (var binding in _bindings)
        {
            var (modifierName, keyName) = BindingFor(binding, settings);

            if (binding.IsRegistered &&
                string.Equals(modifierName, binding.RegisteredModifier, StringComparison.Ordinal) &&
                string.Equals(keyName, binding.RegisteredKey, StringComparison.Ordinal))
            {
                // Saving unrelated settings should not disturb a working binding.
                Console.WriteLine($"[HotkeyManager] The {binding.Description} hotkey is unchanged; keeping the existing registration.");
                continue;
            }

            Unregister(binding);
            Register(binding, modifierName, keyName);
        }
    }

    public static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static async Task<string> GetSelectedTextAsync()
    {
        // Try UI Automation with a timeout (e.g., 200ms) to ensure responsiveness
        string uiaText = string.Empty;
        try
        {
            var uiaTask = Task.Run(() => GetTextViaUIAutomation());
            var delayTask = Task.Delay(200);
            var completedTask = await Task.WhenAny(uiaTask, delayTask);
            if (completedTask == uiaTask)
            {
                uiaText = await uiaTask;
            }
            else
            {
                Console.WriteLine("UI Automation timed out. Falling back to clipboard.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI Automation task failed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(uiaText))
        {
            return uiaText;
        }

        // Fallback to clipboard approach, run on a dedicated STA thread to avoid blocking the main UI thread
        Console.WriteLine("UI Automation didn't return text. Falling back to clipboard.");
        return await RunOnStaThreadAsync(() => GetSelectedTextViaClipboard());
    }

    private static string GetSelectedTextViaClipboard()
    {
        // Get the handle of the currently active window
        var hWnd = GetForegroundWindow();

        if (hWnd != IntPtr.Zero)
        {
            try
            {
                // Set the window to the foreground
                SetForegroundWindow(hWnd);

                // Give the window focus
                Thread.Sleep(100);

                // **Backup the current clipboard state**
                IDataObject backupClipboard = null;
                try
                {
                    if (Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsAudio() || Clipboard.ContainsFileDropList())
                    {
                         backupClipboard = Clipboard.GetDataObject();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not backup clipboard: {ex.Message}");
                }

                // Clear clipboard first
                Clipboard.Clear();
                Thread.Sleep(50);

                // Send WM_COPY message to the window (more reliable than simulating keystrokes)
                SendMessage(hWnd, Interoperability.WM_COPY, 0, 0);

                // Check if WM_COPY worked immediately
                Thread.Sleep(50);
                bool wmCopyWorked = false;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        wmCopyWorked = true;
                    }
                }
                catch { }

                if (!wmCopyWorked)
                {
                    // Identify which modifiers are currently pressed physically/virtually
                    bool ctrlDown = (GetAsyncKeyState(Interoperability.VK_CONTROL) & 0x8000) != 0;
                    bool altDown = (GetAsyncKeyState(Interoperability.VK_MENU) & 0x8000) != 0;
                    bool shiftDown = (GetAsyncKeyState(Interoperability.VK_SHIFT) & 0x8000) != 0;
                    bool winDown = ((GetAsyncKeyState(Interoperability.VK_LWIN) & 0x8000) != 0) || 
                                   ((GetAsyncKeyState(Interoperability.VK_RWIN) & 0x8000) != 0);

                    // Release modifiers if they are down
                    if (ctrlDown) keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);
                    if (altDown) keybd_event(Interoperability.VK_LMENU, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);
                    if (shiftDown) keybd_event(Interoperability.VK_LSHIFT, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);
                    if (winDown) keybd_event(Interoperability.VK_LWIN, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);

                    // Now simulate Ctrl + C
                    keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                    Thread.Sleep(50);
                    keybd_event(Interoperability.VK_C, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                    Thread.Sleep(50);
                    keybd_event(Interoperability.VK_C, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);
                    Thread.Sleep(50);
                    keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);

                    // Restore modifiers if they were down
                    if (winDown) keybd_event(Interoperability.VK_LWIN, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                    if (shiftDown) keybd_event(Interoperability.VK_LSHIFT, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                    if (altDown) keybd_event(Interoperability.VK_LMENU, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                    if (ctrlDown) keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                }

                // Poll clipboard for up to 1000ms (20 * 50ms) to wait for target app to populate it
                string selectedText = string.Empty;
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            var text = Clipboard.GetText();
                            if (!string.IsNullOrEmpty(text))
                            {
                                selectedText = text;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to retrieve clipboard text on attempt {i}: {ex.Message}");
                    }
                    Thread.Sleep(50);
                }

                // **Restore the previous clipboard state**
                try
                {
                    if (backupClipboard != null)
                    {
                        Clipboard.SetDataObject(backupClipboard, true, 5, 100);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to restore clipboard backup: {ex.Message}");
                }

                return selectedText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting selected text: {ex.Message}");
                return String.Empty;
            }
        }

        Console.WriteLine("No active window detected.");
        return String.Empty;
    }

    private static string GetTextViaUIAutomation()
    {
        try
        {
            AutomationElement focusedElement = AutomationElement.FocusedElement;
            if (focusedElement != null)
            {
                object patternObj;
                if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out patternObj))
                {
                    TextPattern textPattern = (TextPattern)patternObj;
                    TextPatternRange[] textSelection = textPattern.GetSelection();
                    if (textSelection.Length > 0)
                    {
                        string selectedText = textSelection[0].GetText(-1);
                        if (!string.IsNullOrWhiteSpace(selectedText))
                        {
                            return selectedText;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI Automation error: {ex.Message}");
        }
        return string.Empty;
    }
}