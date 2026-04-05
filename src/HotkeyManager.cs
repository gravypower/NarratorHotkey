namespace NarratorHotkey;

using System;
using System.Windows.Forms;
using static Interoperability;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

public class HotkeyManager
{
    private const int HOTKEY_ID = 1;
    private readonly IntPtr _windowHandle;
    private bool _isRegistered;

    public HotkeyManager(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        RegisterHotKey();
    }

    public void RegisterHotKey()
    {
        var settings = AppSettings.Load();
        uint modifier = Interoperability.GetModifierCode(settings.HotkeyModifier);
        uint key = Interoperability.GetKeyCode(settings.HotkeyKey);

        _isRegistered = Interoperability.RegisterHotKey(
            _windowHandle,
            HOTKEY_ID,
            modifier,
            key);

        if (!_isRegistered)
        {
            MessageBox.Show($"Could not register the hotkey ({settings.HotkeyModifier}+{settings.HotkeyKey}).",
                "Hotkey Registration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    public void UnregisterHotKey()
    {
        if (!_isRegistered) return;
        // Unregister the hotkey
        Interoperability.UnregisterHotKey(_windowHandle, HOTKEY_ID);
        _isRegistered = false;
    }

    public void ReloadHotKey()
    {
        UnregisterHotKey();
        RegisterHotKey();
    }

    public static string GetSelectedText()
    {
        // Get the handle of the currently active window
        var hWnd = GetForegroundWindow();

        if (hWnd != IntPtr.Zero)
        {
            try
            {
                // Method 1: Try UI Automation first (non-destructive)
                string uiaText = GetTextViaUIAutomation();
                if (!string.IsNullOrEmpty(uiaText))
                {
                    return uiaText;
                }

                // Method 2: Fallback to non-destructive clipboard approach
                Console.WriteLine("UI Automation didn't return text. Falling back to clipboard.");
                
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
                Thread.Sleep(100);

                // Send WM_COPY message to the window (more reliable than simulating keystrokes)
                SendMessage(hWnd, Interoperability.WM_COPY, 0, 0);

                // If WM_COPY didn't work, try keyboard simulation as fallback
                Thread.Sleep(200);

                // Simulate Ctrl+C to copy selected text (fallback method)
                keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(Interoperability.VK_C, 0, Interoperability.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(Interoperability.VK_C, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(Interoperability.VK_LCONTROL, 0, Interoperability.KEYEVENTF_KEYUP, IntPtr.Zero);

                // Wait for clipboard to be updated
                Thread.Sleep(200);

                string selectedText = string.Empty;

                // Retrieve text from clipboard
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var text = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(text))
                        {
                            selectedText = text;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to retrieve clipboard text: {ex.Message}");
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
                        if (!string.IsNullOrEmpty(selectedText))
                        {
                            return selectedText;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI Automation failed: {ex.Message}");
        }
        return string.Empty;
    }
}