namespace NarratorHotkey;

using System;
using System.Runtime.InteropServices;

public static class 
    Interoperability
{
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

    internal const uint WM_COPY = 0x0301;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    
    // Import necessary functions from user32.dll
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    // Virtual key codes
    internal const byte VK_C = 0x43; // 'C' key
    internal const byte VK_LCONTROL = 0xA2; // Left Control key

    // Keyboard event flags
    internal const uint KEYEVENTF_KEYDOWN = 0x0000;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // Constants for modifier keys and messages
    internal const uint MOD_CONTROL = 0x0002;
    internal const int WM_HOTKEY = 0x0312;
    internal const uint VK_2 = 0x32; // '2' key
    


}