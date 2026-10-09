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

    /// <summary>Stage durations of the most recent <see cref="Transcribe"/> call.</summary>
    public TranscriptionTimings LastTimings { get; private set; }

    public string Transcribe(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return string.Empty;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var (features, featureFrames) = ExtractFeatures(samples);
        var featuresDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var (encoded, encodedFrames) = Encode(features, featureFrames);
        var encodeDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var tokens = DecodeTokens(encoded, encodedFrames);
        LastTimings = new TranscriptionTimings(
            System.Diagnostics.Stopwatch.GetElapsedTime(start, featuresDone),
            System.Diagnostics.Stopwatch.GetElapsedTime(featuresDone, encodeDone),
            System.Diagnostics.Stopwatch.GetElapsedTime(encodeDone));
        return _vocabulary.Detokenize(tokens);
    }

    private (float[] Features, long Frames) ExtractFeatures(ReadOnlySpan<float> samples)
    {
        using var waveform = OrtValue.CreateTensorValueFromMemory(samples.ToArray(), [1, samples.Length]);
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
        using var signal = OrtValue.CreateTensorValueFromMemory(features, [1, 128, totalFrames]);
        using var length = OrtValue.CreateTensorValueFromMemory(new[] { featureFrames }, [1]);
        using var outputs = _encoder.Run(
            _runOptions,
            new Dictionary<string, OrtValue> { ["audio_signal"] = signal, ["length"] = length },
            ["outputs", "encoded_lengths"]);
        return (outputs[0].GetTensorDataAsSpan<float>().ToArray(), (int)outputs[1].GetTensorDataAsSpan<long>()[0]);
    }

    private List<int> DecodeTokens(float[] encoded, int validFrames)
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
