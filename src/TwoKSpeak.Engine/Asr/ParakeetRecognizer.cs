using Microsoft.ML.OnnxRuntime;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine.Asr;

/// <summary>Files of one Parakeet TDT ONNX export (istupakov layout: preprocessor, encoder, decoder+joint).</summary>
public sealed record ParakeetModelFiles(string Preprocessor, string Encoder, string DecoderJoint, string Vocabulary)
{
    /// <summary>Resolves the files in a model directory, preferring the given precision suffix (e.g. "fp16", "int8").</summary>
    public static ParakeetModelFiles FromDirectory(string directory, string? precision)
    {
        string Pick(string stem)
        {
            var preferred = precision is null ? null : Path.Combine(directory, $"{stem}.{precision}.onnx");
            if (preferred is not null && File.Exists(preferred))
            {
                return preferred;
            }
            var plain = Path.Combine(directory, $"{stem}.onnx");
            return File.Exists(plain)
                ? plain
                : throw new FileNotFoundException($"Model file '{stem}' not found in '{directory}'.");
        }

        return new ParakeetModelFiles(
            Path.Combine(directory, "nemo128.onnx"),
            Pick("encoder-model"),
            Pick("decoder_joint-model"),
            Path.Combine(directory, "vocab.txt"));
    }
}

/// <param name="Text">Transcript of the new audio only.</param>
/// <param name="SeamPunctuation">
/// Punctuation the model placed between the context and the new audio ("" when none); null when the context
/// produced no words, so there was nothing to judge the seam from.
/// </param>
public readonly record struct ContextualTranscript(string Text, string? SeamPunctuation);

public readonly record struct TranscriptionTimings(TimeSpan Features, TimeSpan Encoder, TimeSpan Decoder);

/// <summary>
/// Offline Parakeet TDT recognizer: 16 kHz mono samples in, punctuated text out.
/// Feature extraction always runs on CPU (tiny); encoder and joint run on the chosen device.
/// </summary>
public sealed class ParakeetRecognizer : IDisposable
{
    private const int EncoderDim = 1024;
    private const int StateDim = 640;
    private const int StateLayers = 2;

    /// <summary>10 ms feature hop times the encoder's 8x subsampling, at 16 kHz.</summary>
    public const int SamplesPerEncoderFrame = 1280;

    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderJoint;
    private readonly Vocabulary _vocabulary;
    private readonly RunOptions _runOptions = new();

    /// <param name="decoderDevice">
    /// Device for the small prediction/joint network, which runs once per decoding step.
    /// Defaults to <paramref name="device"/>.
    /// </param>
    public ParakeetRecognizer(ParakeetModelFiles files, ComputeDevice device, ComputeDevice? decoderDevice = null)
    {
        _vocabulary = Vocabulary.Load(files.Vocabulary);
        using (var cpu = OnnxSessionFactory.CreateOptions(ComputeDevice.Cpu))
        {
            _preprocessor = new InferenceSession(files.Preprocessor, cpu);
        }
        using (var options = OnnxSessionFactory.CreateOptions(device))
        {
            _encoder = new InferenceSession(files.Encoder, options);
        }
        using (var options = OnnxSessionFactory.CreateOptions(decoderDevice ?? device))
        {
            _decoderJoint = new InferenceSession(files.DecoderJoint, options);
        }
        Device = device;
    }

    public ComputeDevice Device { get; }

    /// <summary>
    /// Pads encoder input to a multiple of this many feature frames (0 = off). GPU kernels are planned per input
    /// shape (~150 ms each time a new length appears), so a few fixed buckets keep every phrase on a planned shape.
    /// The true length is still passed, so padded frames are masked out of attention.
    /// </summary>
    public int FeatureBucketFrames { get; init; }

    /// <summary>Stage durations of the most recent <see cref="Transcribe"/> call.</summary>
    public TranscriptionTimings LastTimings { get; private set; }

    public string Transcribe(ReadOnlySpan<float> samples) => Transcribe(ReadOnlySpan<float>.Empty, samples).Text;

    /// <summary>
    /// Transcribes <paramref name="samples"/> with <paramref name="leftContext"/> prepended, so capitalisation and
    /// punctuation of the new audio are decided knowing what came before. Tokens emitted inside the context are
    /// dropped from the text; punctuation the model placed at the seam is reported separately.
    /// </summary>
    public ContextualTranscript Transcribe(ReadOnlySpan<float> leftContext, ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return new ContextualTranscript(string.Empty, null);
        }

        var audio = new float[leftContext.Length + samples.Length];
        leftContext.CopyTo(audio);
        samples.CopyTo(audio.AsSpan(leftContext.Length));
        var tokens = Recognize(audio);

        var firstNew = SplitIndex(tokens, leftContext.Length / SamplesPerEncoderFrame);

        // Punctuation at the end of the context or the start of the new audio sits on the seam between them.
        var seamStart = firstNew;
        while (seamStart > 0 && _vocabulary.IsPunctuation(tokens[seamStart - 1].Id))
        {
            seamStart--;
        }
        var textStart = firstNew;
        while (textStart < tokens.Count && _vocabulary.IsPunctuation(tokens[textStart].Id))
        {
            textStart++;
        }

        var contextHasWords = seamStart > 0;
        var boundary = contextHasWords
            ? _vocabulary.Detokenize(tokens[seamStart..textStart].Select(t => t.Id))
            : null;
        var text = _vocabulary.Detokenize(tokens[textStart..].Select(t => t.Id));
        return new ContextualTranscript(text, boundary);
    }

    /// <summary>
    /// Index of the first token that belongs to the new audio. The encoder sees the whole input, so a word's
    /// first piece can be emitted a frame or two before the seam; the split therefore snaps to a word start,
    /// preferring the one after the largest timing gap (the pause that separated the phrases).
    /// </summary>
    private int SplitIndex(List<TimedToken> tokens, int contextFrames)
    {
        const int EarlyFrames = 3;
        const int LateFrames = 2;
        var best = -1;
        var bestGap = int.MinValue;
        for (var i = 0; i < tokens.Count; i++)
        {
            var frame = tokens[i].Frame;
            if (frame < contextFrames - EarlyFrames || frame > contextFrames + LateFrames || !_vocabulary.IsWordStart(tokens[i].Id))
            {
                continue;
            }
            var gap = i == 0 ? int.MaxValue : frame - tokens[i - 1].Frame;
            if (gap > bestGap)
            {
                best = i;
                bestGap = gap;
            }
        }
        if (best >= 0)
        {
            return best;
        }

        var split = tokens.FindIndex(t => t.Frame >= contextFrames);
        if (split < 0)
        {
            return tokens.Count;
        }
        while (split > 0 && !_vocabulary.IsWordStart(tokens[split].Id) && !_vocabulary.IsPunctuation(tokens[split].Id))
        {
            split--;
        }
        return split;
    }

    private List<TimedToken> Recognize(float[] audio)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var (features, featureFrames) = ExtractFeatures(audio);
        var featuresDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var (encoded, encodedFrames) = Encode(features, featureFrames);
        var encodeDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var tokens = DecodeTokens(encoded, encodedFrames);
        LastTimings = new TranscriptionTimings(
            System.Diagnostics.Stopwatch.GetElapsedTime(start, featuresDone),
            System.Diagnostics.Stopwatch.GetElapsedTime(featuresDone, encodeDone),
            System.Diagnostics.Stopwatch.GetElapsedTime(encodeDone));
        return tokens;
    }

    private (float[] Features, long Frames) ExtractFeatures(float[] samples)
    {
        using var waveform = OrtValue.CreateTensorValueFromMemory(samples, [1, samples.Length]);
        using var lengths = OrtValue.CreateTensorValueFromMemory(new long[] { samples.Length }, [1]);
        using var outputs = _preprocessor.Run(
            _runOptions,
            new Dictionary<string, OrtValue> { ["waveforms"] = waveform, ["waveforms_lens"] = lengths },
            ["features", "features_lens"]);
        return (outputs[0].GetTensorDataAsSpan<float>().ToArray(), outputs[1].GetTensorDataAsSpan<long>()[0]);
    }

    /// <returns>Encoder output laid out [1, 1024, T] and the number of valid frames.</returns>
    private (float[] Encoded, int Frames) Encode(float[] features, long featureFrames)
    {
        var totalFrames = features.Length / 128;
        if (FeatureBucketFrames > 0 && totalFrames % FeatureBucketFrames != 0)
        {
            var padded = (totalFrames / FeatureBucketFrames + 1) * FeatureBucketFrames;
            var bucketed = new float[128 * padded];
            for (var bin = 0; bin < 128; bin++)
            {
                Array.Copy(features, bin * totalFrames, bucketed, bin * padded, totalFrames);
            }
            features = bucketed;
            totalFrames = padded;
        }
        using var signal = OrtValue.CreateTensorValueFromMemory(features, [1, 128, totalFrames]);
        using var length = OrtValue.CreateTensorValueFromMemory(new[] { featureFrames }, [1]);
        using var outputs = _encoder.Run(
            _runOptions,
            new Dictionary<string, OrtValue> { ["audio_signal"] = signal, ["length"] = length },
            ["outputs", "encoded_lengths"]);
        return (outputs[0].GetTensorDataAsSpan<float>().ToArray(), (int)outputs[1].GetTensorDataAsSpan<long>()[0]);
    }

    private List<TimedToken> DecodeTokens(float[] encoded, int validFrames)
    {
        var stride = encoded.Length / EncoderDim;
        var frame = new float[EncoderDim];
        var target = new int[1];
        var targetLength = new[] { 1 };

        JointResult Joint(int t, int previousToken, object state)
        {
            var (state1, state2) = ((float[], float[]))state;
            for (var d = 0; d < EncoderDim; d++)
            {
                frame[d] = encoded[d * stride + t];
            }
            target[0] = previousToken;

            using var frameValue = OrtValue.CreateTensorValueFromMemory(frame, [1, EncoderDim, 1]);
            using var targetValue = OrtValue.CreateTensorValueFromMemory(target, [1, 1]);
            using var lengthValue = OrtValue.CreateTensorValueFromMemory(targetLength, [1]);
            using var state1Value = OrtValue.CreateTensorValueFromMemory(state1, [StateLayers, 1, StateDim]);
            using var state2Value = OrtValue.CreateTensorValueFromMemory(state2, [StateLayers, 1, StateDim]);
            using var outputs = _decoderJoint.Run(
                _runOptions,
                new Dictionary<string, OrtValue>
                {
                    ["encoder_outputs"] = frameValue,
                    ["targets"] = targetValue,
                    ["target_length"] = lengthValue,
                    ["input_states_1"] = state1Value,
                    ["input_states_2"] = state2Value,
                },
                ["outputs", "output_states_1", "output_states_2"]);

            return new JointResult(
                outputs[0].GetTensorDataAsSpan<float>().ToArray(),
                (outputs[1].GetTensorDataAsSpan<float>().ToArray(), outputs[2].GetTensorDataAsSpan<float>().ToArray()));
        }

        var initial = (new float[StateLayers * StateDim], new float[StateLayers * StateDim]);
        return TdtGreedyDecoder.Decode(validFrames, _vocabulary.Count, _vocabulary.BlankId, initial, Joint);
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _decoderJoint.Dispose();
        _encoder.Dispose();
        _preprocessor.Dispose();
    }
}
