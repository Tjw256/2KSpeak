using System.Text.RegularExpressions;

namespace TwoKSpeak.Engine.Curator;

/// <summary>
/// The curator may only delete words. Its output is accepted when its words, in order, are a subsequence of the
/// input's words (case and punctuation ignored); anything added, reworded or answered fails and the raw text is used.
/// </summary>
public static partial class DeletionGuard
{
    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Word();

    public static bool IsDeletionOnly(string input, string output)
    {
        var kept = Words(output);
        if (kept.Count == 0)
        {
            return false; // an empty answer is a failure, not "delete everything"
        }
        var source = Words(input);
        var next = 0;
        foreach (var word in kept)
        {
            while (next < source.Count && source[next] != word)
            {
                next++;
            }
            if (next == source.Count)
            {
                return false;
            }
            next++;
        }
        return true;
    }

    private static List<string> Words(string text) =>
        Word().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();
}
