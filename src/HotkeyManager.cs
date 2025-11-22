namespace NarratorHotkey;

using System;
using System.Windows.Forms;
using static Interoperability;
using System.Threading;

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

    private void RegisterHotKey()
    {
        _isRegistered = Interoperability.RegisterHotKey(
            _windowHandle,
            HOTKEY_ID,
            MOD_CONTROL,
            VK_2);

        if (!_isRegistered)
        {
            MessageBox.Show("Could not register the hotkey (Ctrl+2).",
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

    public static string GetSelectedText()
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

                // Clear clipboard first
                System.Windows.Forms.Clipboard.Clear();
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

                // Retrieve text from clipboard
                try
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to retrieve clipboard text: {ex.Message}");
                }

                return String.Empty;
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
}