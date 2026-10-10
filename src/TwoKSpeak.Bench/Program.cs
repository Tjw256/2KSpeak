// Latency / VRAM spike for Parakeet TDT 0.6B v3 on CPU, CUDA and DirectML.
// Usage: 2KSpeak.Bench --device cpu|cuda|dml --model <dir> [--precision fp16|int8] [--runs N] <wav>...
using System.Diagnostics;
using System.Text.RegularExpressions;
using TwoKSpeak.Engine.Asr;
using TwoKSpeak.Engine.Audio;
using TwoKSpeak.Engine.Onnx;

var processStart = Process.GetCurrentProcess().StartTime;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var argList = args.ToList();
string Option(string name, string fallback)
{
    var i = argList.IndexOf(name);
    if (i < 0) return fallback;
    var value = argList[i + 1];
    argList.RemoveRange(i, 2);
    return value;
}

var device = Option("--device", "cpu") switch
{
    "cpu" => ComputeDevice.Cpu,
    "cuda" => ComputeDevice.Cuda,
    "dml" => ComputeDevice.DirectML,
    var other => throw new ArgumentException($"Unknown device '{other}'."),
};
var modelDir = Option("--model", "");
var precisionArg = Option("--precision", "");
var precision = precisionArg.Length == 0 ? null : precisionArg;
var runs = int.Parse(Option("--runs", "5"));
var decoderArg = Option("--decoder", "");
var bucket = int.Parse(Option("--bucket", "0"));
var vary = argList.Remove("--vary");
ComputeDevice? decoderDevice = decoderArg == "cpu" ? ComputeDevice.Cpu : null;
var wavs = argList;

if (device == ComputeDevice.Cuda)
{
    // CUDA/cuDNN DLLs are app-local, not installed system-wide.
    var cudaDir = Environment.GetEnvironmentVariable("TWOKSPEAK_CUDA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2KSpeak", "runtimes", "cuda");
    Environment.SetEnvironmentVariable("PATH", cudaDir + ";" + Environment.GetEnvironmentVariable("PATH"));
}

if (device == ComputeDevice.DirectML)
    Console.WriteLine($"DirectML adapter {DirectMlAdapter.Select(out var adapterName)}: {adapterName}");
var vramBefore = Vram.UsedMiB();
var sw = Stopwatch.StartNew();
using var recognizer = new ParakeetRecognizer(ParakeetModelFiles.FromDirectory(modelDir, precision), device, decoderDevice)
{
    FeatureBucketFrames = bucket,
};
var loadMs = sw.Elapsed.TotalMilliseconds;
var vramLoaded = Vram.UsedMiB();

var clips = wavs.Select(path => (Path: path, Samples: WavFile.ReadMono16(path, out _))).ToList();
sw.Restart();
var warm = recognizer.Transcribe(clips[0].Samples);
var firstMs = sw.Elapsed.TotalMilliseconds;
var readyMs = (DateTime.Now - processStart).TotalMilliseconds;
var vramPeak = Vram.UsedMiB();

Console.WriteLine($"device={device} decoder={decoderDevice?.ToString() ?? device.ToString()} precision={precision ?? "fp32"} model={Path.GetFileName(modelDir.TrimEnd('\\', '/'))}");
Console.WriteLine($"session load {loadMs:F0} ms | first transcribe {firstMs:F0} ms | process start -> first text {readyMs:F0} ms");
Console.WriteLine($"VRAM (this process): before {vramBefore} MiB, after load {vramLoaded} MiB, after first run {vramPeak} MiB");

if (vary)
{
    // Every dictated phrase has a new length; measure that rather than repeating one shape.
    var source = clips[^1].Samples;
    var timesVary = new List<double>();
    for (var seconds = 2.0; seconds <= 12.0; seconds += 0.37)
    {
        var length = Math.Min(source.Length, (int)(seconds * 16000));
        sw.Restart();
        recognizer.Transcribe(source.AsSpan(0, length));
        timesVary.Add(sw.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"varying lengths 2-12 s, bucket {bucket}: first pass mean {timesVary.Average():F0} ms, max {timesVary.Max():F0} ms");
    timesVary.Clear();
    var stages = new List<TranscriptionTimings>();
    for (var seconds = 2.0; seconds <= 12.0; seconds += 0.37)
    {
        var length = Math.Min(source.Length, (int)(seconds * 16000));
        sw.Restart();
        recognizer.Transcribe(source.AsSpan(0, length));
        timesVary.Add(sw.Elapsed.TotalMilliseconds);
        stages.Add(recognizer.LastTimings);
    }
    Console.WriteLine($"  stages mean: features {stages.Average(t => t.Features.TotalMilliseconds):F0}, encoder {stages.Average(t => t.Encoder.TotalMilliseconds):F0}, decoder {stages.Average(t => t.Decoder.TotalMilliseconds):F0} ms");
    Console.WriteLine($"varying lengths 2-12 s, bucket {bucket}: second pass mean {timesVary.Average():F0} ms, max {timesVary.Max():F0} ms");
    return;
}

foreach (var (path, samples) in clips)
{
    var times = new List<double>();
    var text = "";
    for (var i = 0; i < runs; i++)
    {
        sw.Restart();
        text = recognizer.Transcribe(samples);
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    var audioSeconds = samples.Length / 16000.0;
    var reference = Path.ChangeExtension(path, ".txt");
    var wer = File.Exists(reference) ? Wer.Compute(File.ReadAllText(reference), text) : double.NaN;
    Console.WriteLine();
    var t = recognizer.LastTimings;
    Console.WriteLine($"{Path.GetFileName(path)} ({audioSeconds:F1} s audio): median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms, WER {wer:P1} | last: features {t.Features.TotalMilliseconds:F1} encoder {t.Encoder.TotalMilliseconds:F1} decoder {t.Decoder.TotalMilliseconds:F1} ms");
    Console.WriteLine($"  {text}");
}

static class Vram
{
    // Dedicated VRAM charged to this process (WDDM per-process counter), so other GPU users don't skew it.
    public static long UsedMiB()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var prefix = $"pid_{Environment.ProcessId}_";
        var category = new PerformanceCounterCategory("GPU Process Memory");
        long bytes = 0;
        foreach (var instance in category.GetInstanceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
            bytes += counter.RawValue;
        }
        return bytes / (1024 * 1024);
    }
}

static class Wer
{
    // Word error rate after lower-casing and stripping punctuation, so it measures recognition, not formatting.
    public static double Compute(string reference, string hypothesis)
    {
        string[] Words(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var r = Words(reference);
        var h = Words(hypothesis);
        var d = new int[r.Length + 1, h.Length + 1];
        for (var i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= h.Length; j++) d[0, j] = j;
        for (var i = 1; i <= r.Length; i++)
        for (var j = 1; j <= h.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return r.Length == 0 ? 0 : (double)d[r.Length, h.Length] / r.Length;
    }
}
