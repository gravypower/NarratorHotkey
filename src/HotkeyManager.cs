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
    private const int HOTKEY_ID = 1;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private bool _isRegistered;
    private readonly Action _onHotkeyTriggered;

    // RegisterHotKey(IntPtr.Zero, ...) is thread-affine: the hotkey belongs to the
    // thread that registered it, and only that thread can unregister it. Everything
    // here must therefore run on the thread that owns the message loop.
    private readonly int _ownerThreadId;

    // What is currently registered, so a settings save that leaves the hotkey alone
    // does not tear down and re-register a working binding.
    private string _registeredModifier;
    private string _registeredKey;

    public HotkeyManager(Action onHotkeyTriggered)
    {
        _onHotkeyTriggered = onHotkeyTriggered;
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
            if (m.WParam.ToInt32() == HOTKEY_ID)
            {
                _onHotkeyTriggered?.Invoke();
                return true; // Eat the message so other components do not process it
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
        uint modifier = Interoperability.GetModifierCode(settings.HotkeyModifier);
        uint key = Interoperability.GetKeyCode(settings.HotkeyKey);

        _isRegistered = Interoperability.RegisterHotKey(
            IntPtr.Zero,
            HOTKEY_ID,
            modifier,
            key);

        Console.WriteLine($"[HotkeyManager] Thread-level RegisterHotKey result: {_isRegistered} (Modifier: {settings.HotkeyModifier}, Key: {settings.HotkeyKey})");

        if (_isRegistered)
        {
            _registeredModifier = settings.HotkeyModifier;
            _registeredKey = settings.HotkeyKey;
            return;
        }

        _registeredModifier = null;
        _registeredKey = null;

        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Console.WriteLine($"[HotkeyManager] ERROR: Failed to register thread-level hotkey ({settings.HotkeyModifier}+{settings.HotkeyKey}). Win32 error {error}.");

        string reason = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "Another application has already claimed it."
            : $"Windows reported error {error}.";

        MessageBox.Show($"Could not register the hotkey ({settings.HotkeyModifier}+{settings.HotkeyKey}). {reason}",
            "Hotkey Registration Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    public void UnregisterHotKey()
    {
        if (!_isRegistered) return;

        if (!OnOwnerThread)
        {
            // Unregistering from another thread silently does nothing, which would
            // leave the hotkey held and make the next registration fail.
            Console.WriteLine("[HotkeyManager] ERROR: UnregisterHotKey called off the message-loop thread; ignoring.");
            return;
        }

        Interoperability.UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
        _isRegistered = false;
        _registeredModifier = null;
        _registeredKey = null;
        try
        {
            Application.RemoveMessageFilter(this);
        }
        catch { }
    }

    public void ReloadHotKey()
    {
        if (!OnOwnerThread)
        {
            Console.WriteLine("[HotkeyManager] ERROR: ReloadHotKey called off the message-loop thread; ignoring.");
            return;
        }

        var settings = AppSettings.Load();
        if (_isRegistered &&
            string.Equals(settings.HotkeyModifier, _registeredModifier, StringComparison.Ordinal) &&
            string.Equals(settings.HotkeyKey, _registeredKey, StringComparison.Ordinal))
        {
            // Saving unrelated settings should not disturb a working binding.
            Console.WriteLine("[HotkeyManager] Hotkey unchanged; keeping the existing registration.");
            return;
        }

        UnregisterHotKey();
        try
        {
            Application.AddMessageFilter(this);
        }
        catch { }
        RegisterHotKey();
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