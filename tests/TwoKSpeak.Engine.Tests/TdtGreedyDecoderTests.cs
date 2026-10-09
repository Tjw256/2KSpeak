using TwoKSpeak.Engine.Asr;

namespace TwoKSpeak.Engine.Tests;

public class TdtGreedyDecoderTests
{
    private const int Vocab = 4; // tokens 0..2, blank = 3
    private const int Blank = 3;

    /// <summary>Builds joint logits that pick <paramref name="token"/> with duration index <paramref name="duration"/>.</summary>
    private static float[] Logits(int token, int duration)
    {
        var logits = new float[Vocab + TdtGreedyDecoder.Durations.Length];
        logits[token] = 10;
        logits[Vocab + duration] = 10;
        return logits;
    }

    [Fact]
    public void EmitsTokensAndSkipsFramesByPredictedDuration()
    {
        var visited = new List<int>();
        var script = new Dictionary<int, float[]>
        {
            [0] = Logits(token: 1, duration: 2),
            [2] = Logits(token: 2, duration: 1),
            [3] = Logits(token: Blank, duration: 1),
        };

        var tokens = TdtGreedyDecoder.Decode(4, Vocab, Blank, initialState: 0, (frame, _, state) =>
        {
            visited.Add(frame);
            return new JointResult(script[frame], state);
        });

        Assert.Equal([1, 2], tokens);
        Assert.Equal([0, 2, 3], visited);
    }

    [Fact]
    public void BlankWithZeroDurationStillAdvancesOneFrame()
    {
        var calls = 0;
        var tokens = TdtGreedyDecoder.Decode(3, Vocab, Blank, initialState: 0, (_, _, state) =>
        {
            calls++;
            return new JointResult(Logits(Blank, duration: 0), state);
        });

        Assert.Empty(tokens);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ZeroDurationTokensAreCappedPerFrame()
    {
        var tokens = TdtGreedyDecoder.Decode(1, Vocab, Blank, initialState: 0,
            (_, _, state) => new JointResult(Logits(token: 0, duration: 0), state));

        Assert.Equal(10, tokens.Count);
    }

    [Fact]
    public void StateAndPreviousTokenOnlyChangeAfterNonBlank()
    {
        var seen = new List<(int Previous, int State)>();
        var script = new[]
        {
            Logits(token: 2, duration: 0),     // frame 0: emit 2, stay
            Logits(token: Blank, duration: 1), // frame 0: blank, advance
            Logits(token: Blank, duration: 1), // frame 1: blank, advance
        };
        var step = 0;

        TdtGreedyDecoder.Decode(2, Vocab, Blank, initialState: 100, (_, previous, state) =>
        {
            seen.Add((previous, (int)state));
            var next = (int)state + 1;
            return new JointResult(script[step++], next);
        });

        Assert.Equal([(Blank, 100), (2, 101), (2, 101)], seen);
    }
}
