using System.Text.RegularExpressions;

namespace TwoKSpeak.Engine.Dictation;

/// <summary>
/// Removes standalone hesitation sounds (English and Czech) from a phrase. Only sounds that are never
/// real words are listed; meaningful fillers such as "like" or "jako" are left to the curator LLM.
/// </summary>
public static partial class FillerFilter
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:u+m+|u+h+m*|e+r+m+|e+r+|h+m+|m{2,}|m+h+m+|e+h+m*|e{2,}|a{2,}h*)(?![\p{L}\p{N}])(?:,|…|\.{3})?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Filler();

    [GeneratedRegex(@"\s+([,.!?;:…])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,;:])(?:\s*[,;:])+")]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex(@"[,;:]\s*([.!?…])")]
    private static partial Regex SeparatorBeforeTerminal();

    public static string Apply(string phrase)
    {
        var startedUpper = phrase.Length > 0 && char.IsUpper(phrase[0]);
        var text = Filler().Replace(phrase, "");
        if (text.Length == phrase.Length)
        {
            return phrase;
        }

        text = SpaceBeforePunctuation().Replace(text, "$1");
        text = RepeatedSeparators().Replace(text, "$1");
        text = SeparatorBeforeTerminal().Replace(text, "$1");
        text = text.Trim().TrimStart(',', ';', ':', '.', '…').TrimStart();
        if (!text.Any(char.IsLetterOrDigit))
        {
            return "";
        }

        // "Um, so…" → "So…": keep the sentence capitalised when its first word was a filler.
        if (startedUpper && text.Length > 0 && char.IsLower(text[0]))
        {
            text = char.ToUpper(text[0]) + text[1..];
        }
        return text;
    }
}
