using TwoKSpeak.Engine.Dictation;

namespace TwoKSpeak.Engine.Tests;

public class PhraseSegmenterTests
{
    private const int Window = 512; // 32 ms

    private static readonly SegmenterSettings Settings = new()
    {
        PauseMs = 320,       // 10 windows
        PadMs = 64,          // 2 windows
        MinSpeechMs = 96,    // 3 windows
        MaxPhraseMs = 3_200, // 100 windows
        ContextMs = 320,
        PreviewIntervalMs = 160,
    };

    /// <summary>Feeds windows whose samples carry their own window index, so slices can be checked exactly.</summary>
    private static List<SegmenterEvent> Feed(PhraseSegmenter segmenter, ref int index, float probability, int windows)
    {
        var events = new List<SegmenterEvent>();
        for (var i = 0; i < windows; i++, index++)
        {
            var samples = Enumerable.Repeat((float)index, Window).ToArray();
            events.AddRange(segmenter.Add(samples, probability));
        }
        return events;
    }

    private static int FirstWindow(float[] samples) => (int)samples[0];
    private static int Windows(float[] samples) => samples.Length / Window;

    [Fact]
    public void CommitsPhraseAfterPauseWithPaddingAndContext()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;

        Feed(segmenter, ref index, 0.0f, 20);
        Feed(segmenter, ref index, 0.9f, 15);
        var events = Feed(segmenter, ref index, 0.0f, 12);

        var phrase = Assert.Single(events.OfType<PhraseCompleted>());
        Assert.Equal(18, FirstWindow(phrase.Samples));          // 2 windows of pre-pad before speech at 20
        Assert.Equal(15 + 2 + 2, Windows(phrase.Samples));      // speech plus pad on both sides
        Assert.Equal(8, FirstWindow(phrase.Context));           // 10 windows of context before the phrase
        Assert.Equal(10, Windows(phrase.Context));
    }

    [Fact]
    public void IgnoresBlipsShorterThanMinimumSpeech()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;

        Feed(segmenter, ref index, 0.9f, 2);
        var events = Feed(segmenter, ref index, 0.0f, 12);

        Assert.Empty(events.OfType<PhraseCompleted>());
    }

    [Fact]
    public void OffersPreviewsWhileSpeaking()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;

        var events = Feed(segmenter, ref index, 0.9f, 16);

        Assert.Equal(3, events.OfType<PhrasePreview>().Count());
        Assert.Empty(events.OfType<PhraseCompleted>());
    }

    [Fact]
    public void FinishFlushesPhraseInProgress()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;

        Feed(segmenter, ref index, 0.9f, 8);
        var phrase = Assert.Single(segmenter.Finish().OfType<PhraseCompleted>());

        Assert.Equal(8, Windows(phrase.Samples));
        Assert.Empty(segmenter.Finish());
    }

    [Fact]
    public void CutsLongPhrasesAtTheQuietestPointWithoutLosingAudio()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;
        var events = new List<SegmenterEvent>();

        events.AddRange(Feed(segmenter, ref index, 0.9f, 85));
        events.AddRange(Feed(segmenter, ref index, 0.4f, 1));   // window 85: quietest, but still speech
        events.AddRange(Feed(segmenter, ref index, 0.9f, 30));
        events.AddRange(segmenter.Finish());

        var phrases = events.OfType<PhraseCompleted>().ToList();
        Assert.Equal(2, phrases.Count);
        Assert.Equal(86, Windows(phrases[0].Samples));          // cut right after the quiet window
        Assert.Equal(86, FirstWindow(phrases[1].Samples));      // second phrase continues exactly there
        Assert.Equal(116, Windows(phrases[0].Samples) + Windows(phrases[1].Samples));
    }

    [Fact]
    public void LongRecordingsKeepMemoryBounded()
    {
        var segmenter = new PhraseSegmenter(Settings);
        var index = 0;

        for (var i = 0; i < 50; i++)
        {
            Feed(segmenter, ref index, 0.9f, 20);
            var events = Feed(segmenter, ref index, 0.0f, 40);
            var phrase = Assert.Single(events.OfType<PhraseCompleted>());
            Assert.Equal(i * 60 - 2 + (i == 0 ? 2 : 0), FirstWindow(phrase.Samples));
        }
    }
}
