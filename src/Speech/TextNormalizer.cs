using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NarratorHotkey.Speech
{
    /// <summary>
    /// Text parsing shared by the TTS providers: pronouncing file names and paths,
    /// and deciding which full stops actually end a sentence. A dot inside
    /// "coverage-waivers.txt" is a full stop, but it neither ends a sentence nor
    /// belongs in the token handed to the phonemizer. It is dropped rather than
    /// spoken - announcing "dot" mid-sentence reads as a full stop to a listener.
    /// </summary>
    public static class TextNormalizer
    {
        // A file extension: 1-8 characters, all lower case or all upper case, with at
        // least one letter (so ".7z" counts). Insisting on a single case is what keeps
        // "build.The" - a full stop whose following space was eaten - from looking
        // like a file name.
        private const string Extension = @"(?:[a-z0-9]{0,3}[a-z][a-z0-9]{0,3}|[A-Z0-9]{0,3}[A-Z][A-Z0-9]{0,3})";

        // One path segment or file stem. Dots are excluded so every dotted part of a
        // name is matched separately.
        private const string Segment = @"[\w@%+~-]";

        /// <summary>
        /// A file name, optionally preceded by a path (C:\dir\, /dir/, dir/sub/).
        /// The stem needs at least two characters, which keeps initialisms such as
        /// "e.g." and version numbers such as "3.3.5a" out of this rule.
        /// </summary>
        private static readonly Regex FileReference = new Regex(
            @"(?<![\w.])" +                             // not part of a larger token
            @"(?:[A-Za-z]:[\\/]|[\\/])?" +              // drive or leading separator
            @"(?:" + Segment + @"+[\\/])*" +            // directories
            Segment + @"{2,}(?:\." + Segment + @"+)*?" + // stem, plus any inner parts
            @"\." + Extension +                          // the extension
            @"(?!\.?\w)",                                // and nothing more of the token
            RegexOptions.Compiled);

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        // Words that take a full stop without ending the sentence.
        private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "st", "sr", "jr", "vs", "etc", "eg", "ie",
            "fig", "figs", "no", "nos", "al", "inc", "ltd", "co", "corp", "dept", "est",
            "approx", "cf", "vol", "pp", "jan", "feb", "mar", "apr", "jun", "jul", "aug",
            "sep", "sept", "oct", "nov", "dec"
        };

        // A candidate sentence break: sentence punctuation (plus any closing quote or
        // bracket), whitespace, then a word. Whether the full stop really ends the
        // sentence is decided by EndsSentence.
        private static readonly Regex SentenceBoundary = new Regex(
            "(?<=[.!?][\"'\u201d\u2019)\\]]?)\\s+(?=[\"'\u201c\u2018(\\[]?[A-Za-z0-9])",
            RegexOptions.Compiled);

        /// <summary>
        /// Breaks file names and paths into words the phonemizer can say, instead of
        /// one unpronounceable token: "src/Speech/SpeechManager.cs" becomes
        /// "src slash Speech slash SpeechManager cs". The dot separating stem from
        /// extension becomes a word break and is not spoken.
        /// </summary>
        public static string NormalizeFileReferences(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            return FileReference.Replace(text, match =>
            {
                string spoken = match.Value
                    .Replace('\\', '/')
                    .Replace("/", " slash ")
                    .Replace(":", " colon ")
                    .Replace(".", " ");

                // The match already sits on token boundaries, so no padding is needed -
                // padding would only push a space in front of a following comma or stop.
                return Whitespace.Replace(spoken, " ").Trim();
            });
        }

        /// <summary>
        /// Splits text on sentence endings only. A full stop that belongs to a file
        /// name, an abbreviation or an initial is left where it is.
        /// </summary>
        public static List<string> SplitSentences(string text)
        {
            var sentences = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return sentences;

            int start = 0;
            foreach (Match boundary in SentenceBoundary.Matches(text))
            {
                if (!EndsSentence(text, boundary.Index))
                    continue;

                string sentence = text.Substring(start, boundary.Index - start).Trim();
                if (sentence.Length > 0)
                    sentences.Add(sentence);

                start = boundary.Index + boundary.Length;
            }

            string tail = text.Substring(start).Trim();
            if (tail.Length > 0)
                sentences.Add(tail);

            return sentences;
        }

        /// <summary>
        /// Splits text into chunks the synthesiser can work through progressively:
        /// line by line, then sentence by sentence, with very short pieces merged back
        /// together so each inference has enough to say.
        /// </summary>
        public static List<string> ChunkText(string text, int minChunkLength = 60)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return chunks;

            string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                chunks.AddRange(SplitSentences(line));
            }

            // Combine very short chunks to avoid excessive overhead
            var mergedChunks = new List<string>();
            string currentChunk = "";

            foreach (var chunk in chunks)
            {
                // 60 characters is arbitrary; short enough to combine "Hi." "How are you?"
                if (currentChunk.Length > 0 && currentChunk.Length + chunk.Length < minChunkLength)
                {
                    currentChunk += " " + chunk;
                }
                else
                {
                    if (currentChunk.Length > 0)
                    {
                        mergedChunks.Add(currentChunk);
                    }
                    currentChunk = chunk;
                }
            }
            if (currentChunk.Length > 0)
            {
                mergedChunks.Add(currentChunk);
            }

            return mergedChunks;
        }

        /// <summary>
        /// Decides whether the punctuation immediately before <paramref name="index"/>
        /// closes a sentence.
        /// </summary>
        private static bool EndsSentence(string text, int index)
        {
            int i = index - 1;
            while (i >= 0 && (text[i] == '"' || text[i] == '\'' || text[i] == '\u201d' ||
                              text[i] == '\u2019' || text[i] == ')' || text[i] == ']'))
            {
                i--;
            }

            if (i < 0)
                return false;

            // '!' and '?' are never anything but a sentence ending.
            if (text[i] != '.')
                return true;

            int wordEnd = i;
            int wordStart = wordEnd;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '.'))
            {
                wordStart--;
            }

            string word = text.Substring(wordStart, wordEnd - wordStart);
            if (word.Length == 0)
                return true;

            // "e.g." or "readme.md" - an inner dot means this stop is part of the token.
            if (word.Contains('.'))
                return false;

            // "J. R. R. Tolkien" - a lone letter is an initial, not a sentence.
            if (word.Length == 1 && char.IsLetter(word[0]))
                return false;

            return !Abbreviations.Contains(word);
        }
    }
}
