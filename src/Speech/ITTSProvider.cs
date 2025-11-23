using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// Interface for text-to-speech providers. Allows pluggable TTS backends.
    /// </summary>
    public interface ITTSProvider
    {
        /// <summary>
        /// Speaks the given text.
        /// </summary>
        Task SpeakAsync(string text);

        /// <summary>
        /// Stops any ongoing speech synthesis.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Gets the list of available voices for this provider.
        /// </summary>
        Task<string[]> GetAvailableVoicesAsync();

        /// <summary>
        /// Selects a voice by name.
        /// </summary>
        Task SelectVoiceAsync(string voiceName);

        /// <summary>
        /// Sets the speech rate. Range typically -10 to 10.
        /// </summary>
        void SetRate(int rate);

        /// <summary>
        /// Gets the name of the provider (e.g., "Windows TTS", "Piper").
        /// </summary>
        string GetProviderName();
    }
}
