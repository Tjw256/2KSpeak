using System.Diagnostics;
using System.IO.Pipes;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Ipc;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Owns the inference worker process: starts it on demand, sends requests one at a time, and in GPU mode
/// shuts it down after an idle period so all VRAM (weights and CUDA's own overhead) is returned.
/// A crashed worker is restarted on the next request.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(120);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly Func<AppSettings> _settings;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private RecognitionDevice _device;

    public WorkerClient(Func<AppSettings> settings)
    {
        _settings = settings;
        _idleTimer = new Timer(_ => _ = UnloadIfIdleAsync());
    }

    public event Action<WorkerState>? StateChanged;

    public WorkerState State { get; private set; } = WorkerState.Stopped;

    /// <summary>Starts the worker if needed, without waiting for it. Called when recording begins.</summary>
    public void Prewarm() => _ = PrewarmAsync();

    private async Task PrewarmAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureStartedAsync();
        }
        catch (WorkerException ex)
        {
            Log.Write($"worker prewarm failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranscribeResponse> TranscribeAsync(float[] context, float[] samples, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await SendAsync(new TranscribeRequest(context, samples), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Live preview: skipped (null) when the worker is busy or not ready, so it never delays real phrases.</summary>
    public async Task<TranscribeResponse?> TryPreviewAsync(float[] context, float[] samples, CancellationToken ct)
    {
        if (State != WorkerState.Ready || !await _gate.WaitAsync(0, ct))
        {
            return null;
        }
        try
        {
            return await SendAsync(new TranscribeRequest(context, samples), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <remarks>Caller holds <see cref="_gate"/>; every start, stop and request is serialised through it.</remarks>
    private async Task<TranscribeResponse> SendAsync(TranscribeRequest request, CancellationToken ct)
    {
        await EnsureStartedAsync();
        try
        {
            await WorkerProtocol.WriteRequestAsync(_pipe!, request, ct);
            var response = await WorkerProtocol.ReadResponseAsync(_pipe!, WorkerRequestKind.Transcribe, ct);
            ScheduleIdleUnload();
            return response switch
            {
                TranscribeResponse transcript => transcript,
                ErrorResponse error => throw new WorkerException(error.Message),
                _ => throw new WorkerException($"Unexpected response {response.GetType().Name}."),
            };
        }
        catch (IOException ex)
        {
            Log.Write($"worker connection lost: {ex.Message}");
            await StopAsync();
            throw new WorkerException("The recognition worker stopped unexpectedly.", ex);
        }
    }

    private async Task EnsureStartedAsync()
    {
        var device = _settings().Device;
        if (_process is { HasExited: false } && _pipe is { IsConnected: true } && _device == device)
        {
            return;
        }
        await StartAsync(device);
    }

    private async Task StartAsync(RecognitionDevice device)
    {
        await StopAsync();
        SetState(WorkerState.Loading);
        var watch = Stopwatch.StartNew();
        var pipeName = $"2KSpeak-worker-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var exe = Path.Combine(AppContext.BaseDirectory, "2KSpeak.Worker.exe");
        var start = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "--pipe", pipeName,
                "--parent", Environment.ProcessId.ToString(),
                "--device", device == RecognitionDevice.Gpu ? "cuda" : "cpu",
            },
        };

        try
        {
            _process = Process.Start(start) ?? throw new WorkerException("Could not start the recognition worker.");
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = new CancellationTokenSource(ConnectTimeout))
            {
                await _pipe.ConnectAsync(connect.Token);
            }
            using var load = new CancellationTokenSource(LoadTimeout);
            await WorkerProtocol.WriteRequestAsync(_pipe, new ReadyRequest(), load.Token);
            var response = await WorkerProtocol.ReadResponseAsync(_pipe, WorkerRequestKind.Ready, load.Token);
            if (response is ErrorResponse error)
            {
                throw new WorkerException($"The recognition model failed to load: {error.Message}");
            }
            _device = device;
            Log.Write($"worker ready ({device}) in {watch.ElapsedMilliseconds} ms");
            SetState(WorkerState.Ready);
            ScheduleIdleUnload();
        }
        catch (Exception ex)
        {
            Log.Write($"worker start failed: {ex.Message}");
            await StopAsync();
            SetState(WorkerState.Failed);
            throw ex as WorkerException ?? new WorkerException("The recognition worker failed to start.", ex);
        }
    }

    private void ScheduleIdleUnload()
    {
        var settings = _settings();
        // CPU mode holds only RAM, which the user has plenty of; keeping it avoids a reload on every use.
        var due = settings.Device == RecognitionDevice.Gpu
            ? TimeSpan.FromMinutes(Math.Max(1, settings.IdleUnloadMinutes))
            : Timeout.InfiniteTimeSpan;
        _idleTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private async Task UnloadIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            ScheduleIdleUnload();
            return;
        }
        try
        {
            Log.Write("worker idle, unloading");
            await StopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        var process = _process;
        var pipe = _pipe;
        _process = null;
        _pipe = null;
        if (pipe is not null)
        {
            await pipe.DisposeAsync(); // the worker exits when its client disconnects
        }
        if (process is not null)
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                process.Kill();
            }
            process.Dispose();
        }
        if (State != WorkerState.Failed)
        {
            SetState(WorkerState.Stopped);
        }
    }

    private void SetState(WorkerState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync();
        await StopAsync();
        _gate.Dispose();
    }
}

public enum WorkerState
{
    Stopped,
    Loading,
    Ready,
    Failed,
}

public sealed class WorkerException(string message, Exception? inner = null) : Exception(message, inner);
