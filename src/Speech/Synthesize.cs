using System;
using System.Linq;
using System.Speech.Synthesis;
namespace NarratorHotkey.Speech;

public class Synthesize
{
    public Synthesize()
    {
        using var synth = new SpeechSynthesizer();
        
        // Output information about all of the installed voices.   
        Console.WriteLine("Installed voices:");
        foreach (var voice in synth.GetInstalledVoices())
        {
            var info = voice.VoiceInfo;
            var audioFormats = info.SupportedAudioFormats.Aggregate("", (current, fmt) => current + $"{fmt.EncodingFormat.ToString()}\n");

            Console.WriteLine(" Name:          " + info.Name);
            Console.WriteLine(" Culture:       " + info.Culture);
            Console.WriteLine(" Age:           " + info.Age);
            Console.WriteLine(" Gender:        " + info.Gender);
            Console.WriteLine(" Description:   " + info.Description);
            Console.WriteLine(" ID:            " + info.Id);
            Console.WriteLine(" Enabled:       " + voice.Enabled);
            if (info.SupportedAudioFormats.Count != 0)
            {
                Console.WriteLine(" Audio formats: " + audioFormats);
            }
            else
            {
                Console.WriteLine(" No supported audio formats found");
            }
                
            foreach (var key in info.AdditionalInfo.Keys)
            {
                Console.WriteLine($" {key}: {info.AdditionalInfo[key]}");
            }
        }
    }
    
    public static void ReadText(string text)
    {
        SpeechManager.Instance.Speak(text);
    }
}