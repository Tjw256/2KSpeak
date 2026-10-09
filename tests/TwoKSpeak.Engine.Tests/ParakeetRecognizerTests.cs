using TwoKSpeak.Engine.Asr;
using TwoKSpeak.Engine.Audio;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine.Tests;

/// <summary>
/// End-to-end checks against the real int8 export on CPU. Skipped when the model is not present
/// (CI does not download 700 MB); set TWOKSPEAK_MODEL_DIR to point at another copy.
/// </summary>
public class ParakeetRecognizerTests
{
    private static readonly string ModelDir = Environment.GetEnvironmentVariable("TWOKSPEAK_MODEL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2KSpeak", "models", "parakeet-tdt-0.6b-v3");

    private static ParakeetRecognizer CreateOrSkip()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(ModelDir, "encoder-model.int8.onnx")), $"Parakeet model not found in {ModelDir}.");
        return new ParakeetRecognizer(ParakeetModelFiles.FromDirectory(ModelDir, "int8"), ComputeDevice.Cpu);
    }

    private static float[] Fixture(string name) =>
        WavFile.ReadMono16(Path.Combine(AppContext.BaseDirectory, "fixtures", name), out _);

    [Fact]
    public void TranscribesEnglish()
    {
        using var recognizer = CreateOrSkip();

        var text = recognizer.Transcribe(Fixture("en-short.wav"));

        Assert.StartsWith("Hello, this is a quick test of the dictation app.", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in the evening", text);
    }

    [Fact]
    public void TranscribesCzechWithDiacritics()
    {
        using var recognizer = CreateOrSkip();

        var text = recognizer.Transcribe(Fixture("cs-short.wav"));

        Assert.StartsWith("Ahoj, tohle je rychlý test diktovací aplikace.", text);
        Assert.Contains("večer", text);
    }

    [Fact]
    public void EmptyInputReturnsEmptyText()
    {
        using var recognizer = CreateOrSkip();

        Assert.Equal(string.Empty, recognizer.Transcribe(ReadOnlySpan<float>.Empty));
    }
}
