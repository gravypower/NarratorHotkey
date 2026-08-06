using System.Threading.Tasks;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// Interface for text-to-speech providers. Allows pluggable TTS backends.
    /// </summary>
    public interface ITTSProvider
    {
        /// <summary>
        /// Gets whether the provider is currently speaking. Stays true while paused:
        /// the utterance is still in progress, it is just not making a sound.
        /// </summary>
        bool IsSpeaking { get; }

        /// <summary>
        /// Gets whether speech is currently held part way through.
        /// </summary>
        bool IsPaused { get; }

        /// <summary>
        /// Speaks the given text.
        /// </summary>
        Task SpeakAsync(string text);

        /// <summary>
        /// Stops any ongoing speech synthesis.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Holds speech where it is. Does nothing if nothing is being spoken.
        /// </summary>
        Task PauseAsync();

        /// <summary>
        /// Carries on from where <see cref="PauseAsync"/> left off.
        /// </summary>
        Task ResumeAsync();

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
