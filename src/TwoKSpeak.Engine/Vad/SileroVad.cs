using Microsoft.ML.OnnxRuntime;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine.Vad;

/// <summary>
/// Silero VAD v5 at 16 kHz on CPU: one speech probability per 512-sample (32 ms) window.
/// The model is stateful across windows; call <see cref="Reset"/> between recordings.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int WindowSamples = 512;
    private const int ContextSamples = 64;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[ContextSamples + WindowSamples];
    private readonly long[] _sampleRate = [16000];
    private float[] _state = new float[2 * 128];

    public SileroVad(string modelPath)
    {
        using var options = OnnxSessionFactory.CreateOptions(ComputeDevice.Cpu);
        options.IntraOpNumThreads = 1;
        _session = new InferenceSession(modelPath, options);
    }

    public void Reset()
    {
        Array.Clear(_input);
        _state = new float[2 * 128];
    }

    public float Process(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSamples)
        {
            throw new ArgumentException($"Expected {WindowSamples} samples.", nameof(window));
        }

        // v5 expects the last 64 samples of the previous window in front of the new one.
        _input.AsSpan(WindowSamples, ContextSamples).CopyTo(_input);
        window.CopyTo(_input.AsSpan(ContextSamples));

        using var input = OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]);
        using var state = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        using var sampleRate = OrtValue.CreateTensorValueFromMemory(_sampleRate, []);
        using var outputs = _session.Run(
            _runOptions,
            new Dictionary<string, OrtValue> { ["input"] = input, ["state"] = state, ["sr"] = sampleRate },
            ["output", "stateN"]);

        _state = outputs[1].GetTensorDataAsSpan<float>().ToArray();
        return outputs[0].GetTensorDataAsSpan<float>()[0];
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _session.Dispose();
    }
}
