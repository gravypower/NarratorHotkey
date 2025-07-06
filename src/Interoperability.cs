using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NarratorHotkey;

public class Interoperability
{
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

    internal const uint WM_COPY = 0x0301;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    // Import necessary functions from user32.dll
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Import SendInput function from user32.dll
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Define the INPUT structure
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // Define the InputUnion structure
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    // Define the KEYBDINPUT structure
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk; // Virtual Key Code
        public ushort wScan; // Hardware Scan Code
        public uint dwFlags; // Flags
        public uint time; // Timestamp
        public IntPtr dwExtraInfo; // Additional data
    }

    // Constants for the SendInput function
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYDOWN = 0x0000;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // Constants for modifier keys and messages
    internal const uint MOD_CONTROL = 0x0002;
    internal const int WM_HOTKEY = 0x0312;

    // Virtual key codes
    internal const uint VK_2 = 0x32; // '2' key
}