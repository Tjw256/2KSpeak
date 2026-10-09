namespace TwoKSpeak.Engine.Dictation;

public sealed record SegmenterSettings
{
    public const int SampleRate = 16000;

    public float SpeechThreshold { get; init; } = 0.5f;
    /// <summary>Probability below which speech counts as stopped (hysteresis under the start threshold).</summary>
    public float SilenceThreshold { get; init; } = 0.35f;
    /// <summary>Pause that ends a phrase and commits it.</summary>
    public int PauseMs { get; init; } = 500;
    /// <summary>Audio kept before speech onset and after its end, so word edges are not clipped.</summary>
    public int PadMs { get; init; } = 200;
    /// <summary>Phrases with less speech than this are treated as noise.</summary>
    public int MinSpeechMs { get; init; } = 250;
    /// <summary>Long monologues are cut here, at the quietest point of the last few seconds.</summary>
    public int MaxPhraseMs { get; init; } = 20_000;
    /// <summary>Preceding audio handed to the recognizer with each phrase.</summary>
    public int ContextMs { get; init; } = 3_000;
    /// <summary>How often a live preview of the phrase in progress is offered.</summary>
    public int PreviewIntervalMs { get; init; } = 500;
}

/// <summary>Audio for the recognizer: the phrase itself plus the audio that preceded it.</summary>
public abstract record SegmenterEvent(float[] Samples, float[] Context);

/// <summary>A finished phrase, to be transcribed and typed.</summary>
public sealed record PhraseCompleted(float[] Samples, float[] Context) : SegmenterEvent(Samples, Context);

/// <summary>The phrase in progress so far, for the live preview only.</summary>
public sealed record PhrasePreview(float[] Samples, float[] Context) : SegmenterEvent(Samples, Context);

/// <summary>
/// Splits a recording into phrases from per-window speech probabilities. Pure logic: feed it fixed-size
/// windows and their VAD probability, act on the events it returns. Memory stays bounded however long
/// the recording runs, because audio older than the context window is discarded.
/// </summary>
public sealed class PhraseSegmenter
{
    private readonly SegmenterSettings _settings;
    private readonly List<float> _audio = [];
    private readonly List<(long End, float Probability)> _windows = [];
    private long _offset; // absolute sample index of _audio[0]
    private long _position; // absolute sample index after the last window
    private long _lastPhraseEnd;
    private long? _phraseStart;
    private long _lastSpeechEnd;
    private long _speechSamples;
    private long _lastPreview;

    public PhraseSegmenter(SegmenterSettings settings)
    {
        _settings = settings;
    }

    private static long Samples(int ms) => (long)ms * SegmenterSettings.SampleRate / 1000;

    public IReadOnlyList<SegmenterEvent> Add(ReadOnlySpan<float> window, float speechProbability)
    {
        var events = new List<SegmenterEvent>();
        var windowStart = _position;
        _audio.AddRange(window);
        _position += window.Length;
        _windows.Add((_position, speechProbability));

        if (_phraseStart is null)
        {
            if (speechProbability >= _settings.SpeechThreshold)
            {
                _phraseStart = Math.Max(Math.Max(windowStart - Samples(_settings.PadMs), _lastPhraseEnd), _offset);
                _lastSpeechEnd = _position;
                _speechSamples = window.Length;
                _lastPreview = _position;
            }
        }
        else
        {
            if (speechProbability >= _settings.SilenceThreshold)
            {
                _lastSpeechEnd = _position;
                _speechSamples += window.Length;
            }

            if (_position - _lastSpeechEnd >= Samples(_settings.PauseMs))
            {
                Close(Math.Min(_lastSpeechEnd + Samples(_settings.PadMs), _position), events);
            }
            else if (_position - _phraseStart.Value >= Samples(_settings.MaxPhraseMs))
            {
                var cut = QuietestPoint(_phraseStart.Value);
                Close(cut, events);
                _phraseStart = cut;
                _speechSamples = _position - cut;
                _lastPreview = _position;
            }
            else if (_position - _lastPreview >= Samples(_settings.PreviewIntervalMs))
            {
                _lastPreview = _position;
                events.Add(new PhrasePreview(Slice(_phraseStart.Value, _position), Context(_phraseStart.Value)));
            }
        }

        Trim();
        return events;
    }

    /// <summary>Ends the recording; returns the phrase in progress, if it holds enough speech.</summary>
    public IReadOnlyList<SegmenterEvent> Finish()
    {
        var events = new List<SegmenterEvent>();
        if (_phraseStart is not null)
        {
            Close(_position, events);
        }
        return events;
    }

    private void Close(long end, List<SegmenterEvent> events)
    {
        var start = _phraseStart!.Value;
        if (_speechSamples >= Samples(_settings.MinSpeechMs))
        {
            events.Add(new PhraseCompleted(Slice(start, end), Context(start)));
        }
        _lastPhraseEnd = end;
        _phraseStart = null;
    }

    /// <summary>End of the lowest-probability window in the last quarter of the phrase.</summary>
    private long QuietestPoint(long phraseStart)
    {
        var searchFrom = _position - (_position - phraseStart) / 4;
        var best = _position;
        var bestProbability = float.MaxValue;
        foreach (var (end, probability) in _windows)
        {
            if (end > searchFrom && end < _position && probability < bestProbability)
            {
                best = end;
                bestProbability = probability;
            }
        }
        return best;
    }

    private float[] Context(long phraseStart)
    {
        var from = Math.Max(phraseStart - Samples(_settings.ContextMs), _offset);
        return Slice(from, phraseStart);
    }

    private float[] Slice(long from, long to) =>
        _audio.GetRange((int)(from - _offset), (int)(to - from)).ToArray();

    private void Trim()
    {
        var keepFrom = (_phraseStart ?? _position) - Samples(_settings.ContextMs + _settings.PadMs);
        var drop = (int)Math.Clamp(keepFrom - _offset, 0, _audio.Count);
        if (drop >= SegmenterSettings.SampleRate)
        {
            _audio.RemoveRange(0, drop);
            _offset += drop;
            _windows.RemoveAll(w => w.End <= _offset);
        }
    }
}
