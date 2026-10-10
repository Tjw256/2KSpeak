// Inference worker: loads the recognizer on the requested device and serves transcription requests from the
// tray app over a named pipe. It is a separate process so that exiting it returns all of CUDA's memory.
// Usage: 2KSpeak.Worker --pipe <name> --parent <pid> --device cuda|dml|cpu
using System.Diagnostics;
using System.IO.Pipes;
using TwoKSpeak.Engine;
using TwoKSpeak.Engine.Asr;
using TwoKSpeak.Engine.Ipc;
using TwoKSpeak.Engine.Onnx;

var options = ParseArgs(args);
var pipeName = options["--pipe"];
var device = options["--device"] switch
{
    "cuda" => ComputeDevice.Cuda,
    "dml" => ComputeDevice.DirectML,
    "cpu" => ComputeDevice.Cpu,
    var other => throw new ArgumentException($"Unknown device '{other}'."),
};
Directory.CreateDirectory(AppPaths.Logs);
var log = new StreamWriter(Path.Combine(AppPaths.Logs, "worker.log"), append: true) { AutoFlush = true };
void Log(string message) => log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}");

using var shutdown = new CancellationTokenSource();
WatchParent(int.Parse(options["--parent"]), shutdown);

if (device == ComputeDevice.Cuda)
{
    // CUDA and cuDNN are app-local downloads, not a system install.
    Environment.SetEnvironmentVariable("PATH", AppPaths.CudaRuntime + ";" + Environment.GetEnvironmentVariable("PATH"));
}

// Load while the client connects; the Ready request waits for this.
var loading = Task.Run(() =>
{
    var watch = Stopwatch.StartNew();
    var files = device != ComputeDevice.Cpu
        ? ParakeetModelFiles.FromDirectory(AppPaths.ParakeetFp16, "fp16")
        : ParakeetModelFiles.FromDirectory(AppPaths.ParakeetInt8, "int8");
    var recognizer = new ParakeetRecognizer(files, device)
    {
        // GPU kernels re-plan whenever the input length changes; 2 s buckets cut encoder time ~3x (docs/spike-results.md).
        FeatureBucketFrames = device != ComputeDevice.Cpu ? 200 : 0,
    };
    var loadMs = watch.ElapsedMilliseconds;
    // The first run initialises cuDNN/cuBLAS (~0.8 s); pay it now rather than on the first phrase.
    recognizer.Transcribe(new float[16000]);
    Log($"loaded {device} in {loadMs} ms, warm-up done at {watch.ElapsedMilliseconds} ms");
    return recognizer;
});

try
{
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.WaitForConnectionAsync(shutdown.Token);
    Log("client connected");

    while (!shutdown.IsCancellationRequested)
    {
        var request = await WorkerProtocol.ReadRequestAsync(pipe, shutdown.Token);
        if (request is null)
        {
            break;
        }
        WorkerResponse response;
        try
        {
            response = request switch
            {
                ReadyRequest => new ReadyResponse($"{(await loading).Device} ready"),
                TranscribeRequest t => Transcribe(await loading, t, Log),
                _ => new ErrorResponse($"Unsupported request {request.GetType().Name}."),
            };
        }
        catch (Exception ex)
        {
            Log($"request failed: {ex}");
            response = new ErrorResponse(ex.Message);
        }
        await WorkerProtocol.WriteResponseAsync(pipe, response, shutdown.Token);
    }
    Log("client disconnected, exiting");
}
catch (OperationCanceledException)
{
    Log("parent exited, exiting");
}
catch (Exception ex)
{
    Log($"fatal: {ex}");
    return 1;
}
finally
{
    if (loading.IsCompletedSuccessfully)
    {
        loading.Result.Dispose();
    }
}
return 0;

static TranscribeResponse Transcribe(ParakeetRecognizer recognizer, TranscribeRequest request, Action<string> log)
{
    var watch = Stopwatch.StartNew();
    var result = recognizer.Transcribe(request.Context, request.Samples);
    var t = recognizer.LastTimings;
    log($"transcribed {(request.Context.Length + request.Samples.Length) / 16000.0:F1} s in {watch.Elapsed.TotalMilliseconds:F0} ms " +
        $"(features {t.Features.TotalMilliseconds:F0}, encoder {t.Encoder.TotalMilliseconds:F0}, decoder {t.Decoder.TotalMilliseconds:F0})");
    return new TranscribeResponse(result.Text, result.SeamPunctuation, watch.Elapsed.TotalMilliseconds);
}

// The worker must never outlive the tray app, or it would keep VRAM allocated with nobody to release it.
static void WatchParent(int parentId, CancellationTokenSource shutdown)
{
    try
    {
        var parent = Process.GetProcessById(parentId);
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => shutdown.Cancel();
        if (parent.HasExited)
        {
            shutdown.Cancel();
        }
    }
    catch (ArgumentException)
    {
        shutdown.Cancel();
    }
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        result[args[i]] = args[i + 1];
    }
    foreach (var required in new[] { "--pipe", "--parent", "--device" })
    {
        if (!result.ContainsKey(required))
        {
            throw new ArgumentException($"Missing {required}.");
        }
    }
    return result;
}
