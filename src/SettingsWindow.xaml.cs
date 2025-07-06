namespace NarratorHotkey;

using System;
using NarratorHotkey.Speech;
using System.Speech.Synthesis;
using System.Windows;

public partial class Settings
{
    private readonly AppSettings _settings;
    private bool _shouldSaveSettings;

    public Settings()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadSettings();
        LoadVoices();

        // Add real-time update handlers
        VoiceComboBox.SelectionChanged += SettingChanged;
        SpeechRateSlider.ValueChanged += SettingChanged;
    }

    private void SettingChanged(object sender, EventArgs e)
    {
        SaveSettings();
        SpeechManager.Instance.ApplySettings();
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        var selectedVoice = VoiceComboBox.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selectedVoice)) return;

        SpeechManager.Instance.Speak("This is a test of the selected voice and speech rate.");
    }

    private void LoadVoices()
    {
        using var synth = new SpeechSynthesizer();
        foreach (var voice in synth.GetInstalledVoices())
        {
            if (voice.Enabled)
            {
                VoiceComboBox.Items.Add(voice.VoiceInfo.Name);
            }
        }

        // Select the current voice
        VoiceComboBox.SelectedItem = _settings.SelectedVoice;
    }

    private void LoadSettings()
    {
        SpeechRateSlider.Value = _settings.SpeechRate;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        _shouldSaveSettings = true;
        Close();
    }

    private void SaveSettings()
    {
        _settings.SelectedVoice = VoiceComboBox.SelectedItem?.ToString() ?? "Microsoft James";
        _settings.SpeechRate = (int)SpeechRateSlider.Value;
        _settings.Save();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        DialogResult = _shouldSaveSettings;
    }
}