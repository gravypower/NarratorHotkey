using System;

namespace NarratorHotkey;

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

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        var selectedVoice = VoiceComboBox.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selectedVoice))
        {
            MessageBox.Show("Please select a voice first.", "No Voice Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();
            synth.SelectVoice(selectedVoice);
            synth.Rate = (int)SpeechRateSlider.Value;
            synth.Speak("This is a test of the selected voice and speech rate."); // Changed to Speak from SpeakAsync
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error testing voice: {ex.Message}", "Voice Test Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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