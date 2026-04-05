namespace NarratorHotkey;

using System;
using System.Windows;
using System.Windows.Interop;
using Speech;



public partial class MainWindow
{
    private HotkeyManager _hotkeyManager;
        
    public MainWindow()
    {
        InitializeComponent();
    }

    public void InitializeHidden()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        
        HwndSource source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
            
        // Initialize hotkey manager after window handle is created
        _hotkeyManager = new HotkeyManager(helper.Handle);
    }

    public void ReloadHotkey()
    {
        _hotkeyManager?.ReloadHotKey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Interoperability.WM_HOTKEY)
        {
            if (SpeechManager.Instance.IsSpeaking)
            {
                SpeechManager.Instance.StopAsync();
            }
            else
            {
                var selectedText = HotkeyManager.GetSelectedText();
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    SpeechManager.Instance.Speak(selectedText);
                }
                else
                {
                    SpeechManager.Instance.Speak("No text selected.");
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeyManager?.UnregisterHotKey();
        base.OnClosed(e);
    }
}