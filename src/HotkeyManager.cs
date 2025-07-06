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
            // Set the window to the foreground
            SetForegroundWindow(hWnd);

            // Send the WM_COPY message
            SendMessage(hWnd, WM_COPY, 0, 0);

            // Wait briefly to ensure clipboard is updated
            Thread.Sleep(100);

            // Retrieve text from clipboard
            return Clipboard.GetText();
        }

        Console.WriteLine("No active window detected.");

        return String.Empty;
    }
}