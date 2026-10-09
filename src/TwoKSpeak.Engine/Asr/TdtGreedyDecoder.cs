namespace TwoKSpeak.Engine.Asr;

/// <summary>One prediction-network + joint evaluation for a single encoder frame.</summary>
/// <param name="Logits">Token logits (vocabulary incl. blank) followed by one logit per TDT duration.</param>
/// <param name="NextState">Opaque decoder state to use if the emitted token is kept.</param>
public readonly record struct JointResult(float[] Logits, object NextState);

/// <summary>A decoded token and the encoder frame at which it was emitted.</summary>
public readonly record struct TimedToken(int Id, int Frame);

/// <summary>
/// Greedy Token-and-Duration Transducer decoding. Each joint step predicts a token and how many
/// encoder frames to skip; blanks never update the prediction-network state.
/// </summary>
public static class TdtGreedyDecoder
{
    public static readonly int[] Durations = [0, 1, 2, 3, 4];
    private const int MaxSymbolsPerFrame = 10;

    /// <param name="frameCount">Number of valid encoder frames.</param>
    /// <param name="vocabSize">Vocabulary size including the blank token.</param>
    /// <param name="blankId">Index of the blank token.</param>
    /// <param name="initialState">Prediction-network state before any token.</param>
    /// <param name="joint">Evaluates the joint for (frame, previous token, state).</param>
    public static List<TimedToken> Decode(
        int frameCount,
        int vocabSize,
        int blankId,
        object initialState,
        Func<int, int, object, JointResult> joint)
    {
        var tokens = new List<TimedToken>();
        var state = initialState;
        var previous = blankId;
        var frame = 0;
        var emittedAtFrame = 0;

        while (frame < frameCount)
        {
            var result = joint(frame, previous, state);
            var token = ArgMax(result.Logits, 0, vocabSize);
            var skip = Durations[ArgMax(result.Logits, vocabSize, Durations.Length)];

            if (token != blankId)
            {
                state = result.NextState;
                previous = token;
                tokens.Add(new TimedToken(token, frame));
                emittedAtFrame++;
            }

            if (skip > 0)
            {
                frame += skip;
                emittedAtFrame = 0;
            }
            else if (token == blankId || emittedAtFrame >= MaxSymbolsPerFrame)
            {
                // A zero-duration blank (or a runaway frame) would loop forever; force progress.
                frame++;
                emittedAtFrame = 0;
            }
        }

        return tokens;
    }

    private static int ArgMax(float[] values, int start, int count)
    {
        var best = start;
        for (var i = start + 1; i < start + count; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }
        return best - start;
    }
}
