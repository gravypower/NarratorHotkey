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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
            
        // Initialize hotkey manager after window handle is created
        _hotkeyManager = new HotkeyManager(new WindowInteropHelper(this).Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Interoperability.WM_HOTKEY)
        {
            // If synthesizer is speaking, the Speak method will stop it
            // If it's not speaking, it will read the selected text
            var selectedText = HotkeyManager.GetSelectedText();
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                SpeechManager.Instance.Speak(selectedText);
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