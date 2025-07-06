using System.Speech.Synthesis;

namespace NarratorHotkey.Speech
{
    public class SpeechManager
    {
        private static SpeechManager? _instance;
        private readonly SpeechSynthesizer _synthesizer;
        private readonly AppSettings _settings;

        public static SpeechManager Instance => _instance ??= new SpeechManager();

        private SpeechManager()
        {
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _settings = AppSettings.Load();
            ApplySettings();
        }

        public void ApplySettings()
        {
            _settings.Reload(); // Add this method to AppSettings
            _synthesizer.SelectVoice(_settings.SelectedVoice);
            _synthesizer.Rate = _settings.SpeechRate;
        }

        public void Speak(string text)
        {
            _synthesizer.SpeakAsync(text);
        }

        public void Stop()
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
    }
}
