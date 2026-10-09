namespace TwoKSpeak.Engine.Dictation;

/// <summary>
/// Turns separately transcribed phrases into one continuous text, typed incrementally.
/// A phrase's trailing punctuation is held back until the next phrase shows whether the pause was a
/// sentence break ("I was thinking. That…" vs "I was thinking that…"), so nothing typed is ever retracted.
/// </summary>
public sealed class PhraseAssembler
{
    private const string TrailingMarks = ".,!?;:…";
    private string _held = "";
    private bool _typedAnything;

    /// <param name="phrase">Transcript of the new phrase.</param>
    /// <param name="seamPunctuation">
    /// Punctuation the recognizer placed between the previous phrase and this one when it saw both
    /// ("" for none), or null when it could not tell.
    /// </param>
    /// <returns>Text to type now.</returns>
    public string Add(string phrase, string? seamPunctuation)
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0)
        {
            return "";
        }

        var output = "";
        if (_typedAnything)
        {
            output = ResolveHeld(phrase, seamPunctuation) + " ";
        }

        var body = phrase.TrimEnd(TrailingMarks.ToCharArray());
        _held = phrase[body.Length..];
        if (body.Length == 0)
        {
            // A phrase that is only punctuation adds nothing to type.
            return output.TrimEnd();
        }

        _typedAnything = true;
        return output + body;
    }

    /// <summary>Text to type when the recording ends: the last phrase's held punctuation.</summary>
    public string Finish()
    {
        var held = _held;
        _held = "";
        return _typedAnything ? held : "";
    }

    private string ResolveHeld(string nextPhrase, string? seamPunctuation)
    {
        var held = _held;
        _held = "";
        if (seamPunctuation is not null)
        {
            // The recognizer saw both sides of the pause; trust its punctuation over the isolated guess.
            return seamPunctuation;
        }

        // No context verdict: a sentence-ending mark survives only if the next phrase starts a sentence.
        var endsSentence = held.IndexOfAny(['.', '!', '?', '…']) >= 0;
        var nextStartsLower = char.IsLower(nextPhrase[0]);
        return endsSentence && nextStartsLower ? "" : held;
    }
}
