using TwoKSpeak.Engine.Asr;
using TwoKSpeak.Engine.Audio;
using TwoKSpeak.Engine.Dictation;
using TwoKSpeak.Engine.Onnx;
using TwoKSpeak.Engine.Vad;

namespace TwoKSpeak.Engine.Tests;

/// <summary>
/// Offline run of the dictation pipeline on real speech: Silero VAD → phrase segmenter → recognizer with
/// left context → assembler. Skipped when the models are not downloaded.
/// </summary>
public class DictationPipelineTests(ITestOutputHelper output)
{
    private static readonly string ModelRoot = Environment.GetEnvironmentVariable("TWOKSPEAK_MODELS_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2KSpeak", "models");

    private static string VadPath => Path.Combine(ModelRoot, "silero-vad", "silero_vad.onnx");
    private static string AsrDir => Path.Combine(ModelRoot, "parakeet-tdt-0.6b-v3");

    private static float[] Fixture(string name) =>
        WavFile.ReadMono16(Path.Combine(AppContext.BaseDirectory, "fixtures", name), out _);

    private static void SkipWithoutModels() =>
        Assert.SkipUnless(File.Exists(VadPath) && File.Exists(Path.Combine(AsrDir, "encoder-model.int8.onnx")),
            $"Models not found under {ModelRoot}.");

    [Fact]
    public void VadSeparatesSpeechFromSilence()
    {
        SkipWithoutModels();
        using var vad = new SileroVad(VadPath);
        var speech = Fixture("en-short.wav");

        var silenceMax = Enumerable.Range(0, 30).Max(_ => vad.Process(new float[SileroVad.WindowSamples]));
        vad.Reset();
        var speechWindows = Enumerable.Range(0, speech.Length / SileroVad.WindowSamples)
            .Select(i => vad.Process(speech.AsSpan(i * SileroVad.WindowSamples, SileroVad.WindowSamples)))
            .Count(p => p >= 0.5f);

        Assert.True(silenceMax < 0.1f, $"Silence scored {silenceMax}.");
        Assert.True(speechWindows > speech.Length / SileroVad.WindowSamples / 2, $"Only {speechWindows} speech windows.");
    }

    [Theory]
    [InlineData("en-short.wav")]
    [InlineData("cs-short.wav")]
    [InlineData("en-long.wav")]
    public void PhraseByPhraseMatchesOneShotTranscription(string fixture)
    {
        SkipWithoutModels();
        using var vad = new SileroVad(VadPath);
        using var recognizer = new ParakeetRecognizer(ParakeetModelFiles.FromDirectory(AsrDir, "int8"), ComputeDevice.Cpu);
        var audio = Fixture(fixture);
        // Short pause so the TTS sentence gaps actually split phrases.
        var segmenter = new PhraseSegmenter(new SegmenterSettings { PauseMs = 250 });
        var assembler = new PhraseAssembler();

        var phrases = new List<PhraseCompleted>();
        for (var i = 0; i + SileroVad.WindowSamples <= audio.Length; i += SileroVad.WindowSamples)
        {
            var window = audio.AsSpan(i, SileroVad.WindowSamples);
            phrases.AddRange(segmenter.Add(window, vad.Process(window)).OfType<PhraseCompleted>());
        }
        phrases.AddRange(segmenter.Finish().OfType<PhraseCompleted>());

        var typed = "";
        foreach (var phrase in phrases)
        {
            var result = recognizer.Transcribe(phrase.Context, phrase.Samples);
            output.WriteLine($"[{phrase.Samples.Length / 16000.0:F1}s] '{result.Text}' seam='{result.SeamPunctuation}'");
            typed += assembler.Add(result.Text, result.SeamPunctuation);
        }
        typed += assembler.Finish();
        var oneShot = recognizer.Transcribe(audio);
        output.WriteLine($"typed:    {typed}");
        output.WriteLine($"one-shot: {oneShot}");

        Assert.True(phrases.Count >= 2, $"Expected the clip to split, got {phrases.Count} phrase(s).");
        // Words only, and allow a little drift: number formatting ("pět" vs "5") can differ between passes.
        var reference = Words(oneShot);
        var hypothesis = Words(typed);
        Assert.True(EditDistance(reference, hypothesis) <= Math.Max(1, reference.Length / 20),
            $"Phrase-by-phrase output drifted from the one-shot transcription.");
        Assert.Equal(char.IsUpper(oneShot[0]), char.IsUpper(typed[0]));
    }

    private static string[] Words(string text) => text.ToLowerInvariant()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Trim('.', ',', '?', '!', ';', ':'))
        .ToArray();

    private static int EditDistance(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
