namespace NarratorHotkey;

using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using NarratorHotkey.Speech;
using System.Windows;

public partial class Settings
{
    private readonly AppSettings _settings;
    private bool _shouldSaveSettings;
    private bool _isLoading = true;
    private ITTSProvider _currentProvider;

    public Settings()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadSettings();
        InitializeProviders();

        // Add real-time update handlers
        VoiceComboBox.SelectionChanged += VoiceComboBox_SelectionChanged;
        SpeechRateSlider.ValueChanged += SettingChanged;
        ProviderComboBox.SelectionChanged += ProviderComboBox_SelectionChanged;

        // Defer voice loading until window is shown to keep UI responsive
        Loaded += (s, e) =>
        {
            _isLoading = false;
            LoadVoices();
        };
    }

    private void InitializeProviders()
    {
        ProviderComboBox.Items.Add("Windows");
        ProviderComboBox.Items.Add("Piper");
        ProviderComboBox.SelectedItem = _settings.TTSProvider;
    }

    private void SettingChanged(object sender, EventArgs e)
    {
        if (_isLoading) return;
        SaveSettings();
        SpeechManager.Instance.ApplySettings();
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        LoadVoices();
        SaveSettings();
        // Don't apply settings immediately - this would block while Piper initializes
        // Settings will be applied when user clicks Save
    }

    private void VoiceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        SaveSettings();

        // If using Piper, predownload the selected voice
        string selectedProvider = ProviderComboBox.SelectedItem?.ToString() ?? "Windows";
        if (selectedProvider == "Piper" && _currentProvider is PiperTTSProvider piperProvider)
        {
            var selectedVoice = VoiceComboBox.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(selectedVoice))
            {
                StatusText.Text = $"Downloading voice: {selectedVoice}";
                ProgressIndicator.Visibility = Visibility.Visible;

                Task.Run(async () =>
                {
                    try
                    {
                        await piperProvider.PredownloadVoiceAsync(selectedVoice);
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Ready";
                            ProgressIndicator.Visibility = Visibility.Collapsed;
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"Error: {ex.Message}";
                            ProgressIndicator.Visibility = Visibility.Collapsed;
                        });
                    }
                });
            }
        }
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        var selectedVoice = VoiceComboBox.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selectedVoice)) return;

        SpeechManager.Instance.Speak("This is a test of the selected voice and speech rate.");
    }

    private void LoadVoices()
    {
        VoiceComboBox.Items.Clear();

        string selectedProvider = ProviderComboBox.SelectedItem?.ToString() ?? "Windows";

        if (selectedProvider == "Windows")
        {
            LoadWindowsVoices();
        }
        else if (selectedProvider == "Piper")
        {
            LoadPiperVoices();
        }
    }

    private void LoadWindowsVoices()
    {
        try
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
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load Windows voices: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadPiperVoices()
    {
        VoiceComboBox.Items.Add("Loading Piper voices...");
        VoiceComboBox.IsEnabled = false;

        // Show progress indicators
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = "Initializing Piper TTS...";
            ProgressIndicator.Visibility = Visibility.Visible;
        });

        var speechManager = SpeechManager.Instance;
        var piperProvider = speechManager.GetProviderByName("Piper") as PiperTTSProvider;

        // Subscribe to progress events BEFORE starting background task (on UI thread)
        if (piperProvider != null)
        {
            piperProvider.ProgressChanged += OnPiperProgress;
            _currentProvider = piperProvider;
        }

        Task.Run(async () =>
        {
            try
            {
                var voices = await speechManager.GetVoicesForProviderAsync("Piper");

                string selectedVoice = null;
                Dispatcher.Invoke(() =>
                {
                    VoiceComboBox.Items.Clear();
                    foreach (var voice in voices)
                    {
                        VoiceComboBox.Items.Add(voice);
                    }

                    // Select the current Piper voice
                    VoiceComboBox.SelectedItem = _settings.PiperVoice;
                    if (VoiceComboBox.SelectedItem == null && voices.Length > 0)
                    {
                        VoiceComboBox.SelectedIndex = 0;
                    }

                    VoiceComboBox.IsEnabled = true;

                    // Capture the selected voice on the UI thread
                    selectedVoice = VoiceComboBox.SelectedItem?.ToString();
                });

                // Predownload the selected voice model
                if (!string.IsNullOrEmpty(selectedVoice) && _currentProvider is PiperTTSProvider piper)
                {
                    await piper.PredownloadVoiceAsync(selectedVoice);
                }

                Dispatcher.Invoke(() =>
                {
                    // Hide progress indicators and enable testing
                    StatusText.Text = "Ready - you can now test the voice";
                    ProgressIndicator.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Failed to load Piper voices: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    VoiceComboBox.Items.Clear();
                    VoiceComboBox.Items.Add("Error loading voices");
                    VoiceComboBox.IsEnabled = false;

                    // Hide progress indicators on error
                    StatusText.Text = "Error initializing Piper";
                    ProgressIndicator.Visibility = Visibility.Collapsed;
                });
            }
        });
    }

    private void OnPiperProgress(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }

    private void LoadSettings()
    {
        SpeechRateSlider.Value = _settings.SpeechRate;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        SpeechManager.Instance.ApplySettings();
        _shouldSaveSettings = true;
        Close();
    }

    private void SaveSettings()
    {
        _settings.TTSProvider = ProviderComboBox.SelectedItem?.ToString() ?? "Windows";
        _settings.SpeechRate = (int)SpeechRateSlider.Value;

        string selectedProvider = _settings.TTSProvider;
        string selectedVoice = VoiceComboBox.SelectedItem?.ToString();

        if (string.IsNullOrEmpty(selectedVoice))
        {
            selectedVoice = selectedProvider == "Windows" ? "Microsoft David Desktop" : "en_US-lessac-medium";
        }

        if (selectedProvider == "Windows")
        {
            _settings.SelectedVoice = selectedVoice;
        }
        else if (selectedProvider == "Piper")
        {
            _settings.PiperVoice = selectedVoice;
        }

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