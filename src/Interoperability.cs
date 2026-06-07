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

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // Virtual key codes
    internal const byte VK_SHIFT = 0x10;
    internal const byte VK_CONTROL = 0x11;
    internal const byte VK_MENU = 0x12; // Alt key
    internal const byte VK_C = 0x43; // 'C' key
    internal const byte VK_LWIN = 0x5B;
    internal const byte VK_RWIN = 0x5C;
    internal const byte VK_LSHIFT = 0xA0;
    internal const byte VK_RSHIFT = 0xA1;
    internal const byte VK_LCONTROL = 0xA2;
    internal const byte VK_RCONTROL = 0xA3;
    internal const byte VK_LMENU = 0xA4;
    internal const byte VK_RMENU = 0xA5;

    // Keyboard event flags
    internal const uint KEYEVENTF_KEYDOWN = 0x0000;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // Constants for modifier keys and messages
    internal const uint MOD_NONE = 0x0000;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    internal const int WM_HOTKEY = 0x0312;
    internal const uint VK_2 = 0x32; // '2' key
    
    public static uint GetModifierCode(string modifier)
    {
        return modifier switch
        {
            "Control" => MOD_CONTROL,
            "Alt" => MOD_ALT,
            "Shift" => MOD_SHIFT,
            _ => MOD_NONE
        };
    }

    public static uint GetKeyCode(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpper(key[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return (uint)c;
            }
        }
        
        return key switch
        {
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => VK_2 // Default to '2' if unrecognized
        };
    }
}