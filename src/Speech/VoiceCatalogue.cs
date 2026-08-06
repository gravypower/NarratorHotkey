using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// What the settings page needs to show about one voice.
    /// </summary>
    public class VoiceDescription
    {
        /// <summary>The name the provider knows the voice by, e.g. "en_US-lessac-high".</summary>
        public string Id { get; set; }

        /// <summary>The speaker on their own, e.g. "Lessac".</summary>
        public string Name { get; set; }

        /// <summary>The heading the voice is listed under, e.g. "English (United States)".</summary>
        public string Group { get; set; }

        /// <summary>
        /// Short labels shown beside the name: the model quality for Piper, the gender
        /// for Kokoro. Also searchable.
        /// </summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Terms worth matching on but not worth showing, such as the "en_US" a voice
        /// is listed under as "English (United States)".
        /// </summary>
        public string Keywords { get; set; }

        /// <summary>
        /// False when choosing the voice would download a model first. Only Piper
        /// fetches a model per voice, so everything else reports true.
        /// </summary>
        public bool Downloaded { get; set; }
    }

    /// <summary>
    /// Turns the flat voice names a provider reports into something a person can scan:
    /// a speaker, the language it speaks, and how big the download is. Piper alone
    /// offers about 170 voices, which is unusable as one long list.
    /// </summary>
    public static class VoiceCatalogue
    {
        private const string UngroupedHeading = "Other";

        private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = "Arabic",     ["bg"] = "Bulgarian",  ["bn"] = "Bengali",    ["ca"] = "Catalan",
            ["cs"] = "Czech",      ["cy"] = "Welsh",      ["da"] = "Danish",     ["de"] = "German",
            ["el"] = "Greek",      ["en"] = "English",    ["es"] = "Spanish",    ["eu"] = "Basque",
            ["fa"] = "Persian",    ["fi"] = "Finnish",    ["fr"] = "French",     ["he"] = "Hebrew",
            ["hi"] = "Hindi",      ["hu"] = "Hungarian",  ["hy"] = "Armenian",   ["id"] = "Indonesian",
            ["is"] = "Icelandic",  ["it"] = "Italian",    ["ka"] = "Georgian",   ["kk"] = "Kazakh",
            ["ko"] = "Korean",     ["ku"] = "Kurdish",    ["lb"] = "Luxembourgish",
            ["lv"] = "Latvian",    ["ml"] = "Malayalam",  ["mr"] = "Marathi",    ["ne"] = "Nepali",
            ["nl"] = "Dutch",      ["no"] = "Norwegian",  ["pl"] = "Polish",     ["pt"] = "Portuguese",
            ["ro"] = "Romanian",   ["ru"] = "Russian",    ["sk"] = "Slovak",     ["sl"] = "Slovenian",
            ["sq"] = "Albanian",   ["sr"] = "Serbian",    ["sv"] = "Swedish",    ["sw"] = "Swahili",
            ["te"] = "Telugu",     ["tr"] = "Turkish",    ["uk"] = "Ukrainian",  ["ur"] = "Urdu",
            ["vi"] = "Vietnamese", ["zh"] = "Chinese"
        };

        private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AL"] = "Albania",    ["AM"] = "Armenia",    ["AR"] = "Argentina",  ["AT"] = "Austria",
            ["BD"] = "Bangladesh", ["BE"] = "Belgium",    ["BG"] = "Bulgaria",   ["BR"] = "Brazil",
            ["CD"] = "Congo",      ["CH"] = "Switzerland",["CN"] = "China",      ["CZ"] = "Czechia",
            ["DE"] = "Germany",    ["DK"] = "Denmark",    ["ES"] = "Spain",      ["FI"] = "Finland",
            ["FR"] = "France",     ["GB"] = "United Kingdom",                    ["GE"] = "Georgia",
            ["GR"] = "Greece",     ["HU"] = "Hungary",    ["ID"] = "Indonesia",  ["IL"] = "Israel",
            ["IN"] = "India",      ["IR"] = "Iran",       ["IS"] = "Iceland",    ["IT"] = "Italy",
            ["JO"] = "Jordan",     ["KR"] = "South Korea",["KZ"] = "Kazakhstan", ["LU"] = "Luxembourg",
            ["LV"] = "Latvia",     ["MX"] = "Mexico",     ["NL"] = "Netherlands",["NO"] = "Norway",
            ["NP"] = "Nepal",      ["PK"] = "Pakistan",   ["PL"] = "Poland",     ["PT"] = "Portugal",
            ["RO"] = "Romania",    ["RS"] = "Serbia",     ["RU"] = "Russia",     ["SE"] = "Sweden",
            ["SI"] = "Slovenia",   ["SK"] = "Slovakia",   ["TR"] = "Turkey",     ["UA"] = "Ukraine",
            ["US"] = "United States",                     ["VN"] = "Vietnam"
        };

        // Kokoro packs the accent and the gender into the first two letters of the
        // voice name: "af_heart" is an American female, "bm_lewis" a British male.
        private static readonly Dictionary<string, (string Group, string Gender)> KokoroPrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = ("English (United States)", "Female"),
            ["am"] = ("English (United States)", "Male"),
            ["bf"] = ("English (United Kingdom)", "Female"),
            ["bm"] = ("English (United Kingdom)", "Male")
        };

        public static List<VoiceDescription> Describe(string providerName, IEnumerable<string> voiceIds)
        {
            var described = new List<VoiceDescription>();
            if (voiceIds == null)
            {
                return described;
            }

            // Only Piper downloads a model per voice, and only it can say which of them
            // are already on disk.
            var piper = providerName == "Piper"
                ? SpeechManager.Instance.GetProviderByName("Piper") as PiperTTSProvider
                : null;

            foreach (string id in voiceIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                VoiceDescription voice = providerName switch
                {
                    "Piper" => DescribePiper(id),
                    "Kokoro ONNX" => DescribeKokoro(id),
                    _ => DescribeInstalled(id)
                };

                voice.Downloaded = piper == null || piper.IsVoiceDownloaded(id);
                described.Add(voice);
            }

            return described;
        }

        /// <summary>
        /// Piper names a voice "locale-speaker-quality", e.g. "en_GB-jenny_dioco-medium".
        /// The speaker itself may contain underscores, so only the outer dashes are
        /// structural.
        /// </summary>
        private static VoiceDescription DescribePiper(string id)
        {
            string[] parts = id.Split('-');
            string locale = parts.Length > 0 ? parts[0] : string.Empty;
            string speaker = parts.Length > 1 ? parts[1] : id;
            string quality = parts.Length > 2 ? parts[parts.Length - 1] : string.Empty;

            return new VoiceDescription
            {
                Id = id,
                Name = Humanise(speaker),
                Group = DescribeLocale(locale),
                Tags = string.IsNullOrEmpty(quality) ? Array.Empty<string>() : new[] { quality.Replace('_', ' ') },
                Keywords = locale
            };
        }

        private static VoiceDescription DescribeKokoro(string id)
        {
            int split = id.IndexOf('_');
            string prefix = split > 0 ? id.Substring(0, split) : string.Empty;
            string speaker = split > 0 ? id.Substring(split + 1) : id;

            if (!KokoroPrefixes.TryGetValue(prefix, out var accent))
            {
                accent = (UngroupedHeading, string.Empty);
            }

            return new VoiceDescription
            {
                Id = id,
                Name = Humanise(speaker),
                Group = accent.Group,
                Tags = string.IsNullOrEmpty(accent.Gender) ? Array.Empty<string>() : new[] { accent.Gender },
                Keywords = string.Empty
            };
        }

        private static VoiceDescription DescribeInstalled(string id)
        {
            return new VoiceDescription
            {
                Id = id,
                Name = id,
                Group = "Installed on this PC",
                Keywords = string.Empty
            };
        }

        /// <summary>
        /// "en_US" becomes "English (United States)". Anything unrecognised is left as
        /// it is rather than hidden, so a voice never goes missing from the list.
        /// </summary>
        private static string DescribeLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale))
            {
                return UngroupedHeading;
            }

            string[] parts = locale.Split('_');
            string language = parts[0];
            string region = parts.Length > 1 ? parts[1] : string.Empty;

            string languageName = Languages.TryGetValue(language, out var known) ? known : language;
            if (string.IsNullOrEmpty(region))
            {
                return languageName;
            }

            string regionName = Regions.TryGetValue(region, out var knownRegion) ? knownRegion : region;
            return $"{languageName} ({regionName})";
        }

        private static string Humanise(string speaker)
        {
            if (string.IsNullOrWhiteSpace(speaker))
            {
                return speaker ?? string.Empty;
            }

            var words = speaker
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 1
                    ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]) + word.Substring(1));

            return string.Join(" ", words);
        }
    }
}
