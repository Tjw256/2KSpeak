using System.Diagnostics;
using System.IO.Pipes;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Ipc;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Owns the inference worker process: starts it on demand, sends requests one at a time, and in GPU mode
/// shuts it down after an idle period so all VRAM (weights and CUDA's own overhead) is returned.
/// A crashed worker is restarted on the next request. If the GPU can't load or run the model (typically
/// because another program filled the VRAM), the worker continues on the CPU until it next unloads, and the
/// GPU is tried again on the following start. Until GPU mode is downloaded, GPU requests run on the CPU.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(120);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly Func<AppSettings> _settings;
    private readonly Func<ComponentId, bool> _isInstalled;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    /// <summary>The device asked for (settings and downloads allowing) when the running worker was started.</summary>
    private RecognitionDevice _requested;

    public WorkerClient(Func<AppSettings> settings, Func<ComponentId, bool> isInstalled)
    {
        _settings = settings;
        _isInstalled = isInstalled;
        _idleTimer = new Timer(_ => _ = UnloadIfIdleAsync());
    }

    public event Action<WorkerState>? StateChanged;

    public WorkerState State { get; private set; } = WorkerState.Stopped;

    /// <summary>Process id of the running worker, for the VRAM readout; null when none is running.</summary>
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    /// <summary>The device the running worker uses; differs from the setting after a GPU fallback.</summary>
    public RecognitionDevice? ActiveDevice { get; private set; }

    /// <summary>
    /// Applies changed settings: a different device stops the worker now (freeing its memory) instead of on the
    /// next phrase; a different idle timeout is rescheduled.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is not null && _requested != Requested())
            {
                await StopAsync();
            }
            else if (_process is not null)
            {
                ScheduleIdleUnload();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

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
        var response = await ExchangeAsync(request, ct);
        if (response is ErrorResponse gpuError && ActiveDevice == RecognitionDevice.Gpu)
        {
            // Most likely CUDA ran out of memory mid-session; finish the dictation on the CPU instead of losing it.
            Log.Write($"GPU transcription failed, continuing on CPU: {gpuError.Message}");
            await StartAsync(_requested, gpuFailed: true);
            response = await ExchangeAsync(request, ct);
        }
        return response switch
        {
            TranscribeResponse transcript => transcript,
            ErrorResponse error => throw new WorkerException(error.Message),
            _ => throw new WorkerException($"Unexpected response {response.GetType().Name}."),
        };
    }

    private async Task<WorkerResponse> ExchangeAsync(TranscribeRequest request, CancellationToken ct)
    {
        try
        {
            await WorkerProtocol.WriteRequestAsync(_pipe!, request, ct);
            var response = await WorkerProtocol.ReadResponseAsync(_pipe!, WorkerRequestKind.Transcribe, ct);
            ScheduleIdleUnload();
            return response;
        }
        catch (IOException ex)
        {
            Log.Write($"worker connection lost: {ex.Message}");
            await StopAsync();
            throw new WorkerException("The recognition worker stopped unexpectedly.", ex);
        }
    }

    /// <summary>The settings' device, or the CPU while GPU mode is still downloading.</summary>
    private RecognitionDevice Requested() =>
        _settings().Device == RecognitionDevice.Gpu && _isInstalled(ComponentId.GpuMode) ? RecognitionDevice.Gpu : RecognitionDevice.Cpu;

    private async Task EnsureStartedAsync()
    {
        var requested = Requested();
        if (_process is { HasExited: false } && _pipe is { IsConnected: true } && _requested == requested)
        {
            return;
        }
        await StartAsync(requested, gpuFailed: false);
    }

    private async Task StartAsync(RecognitionDevice requested, bool gpuFailed)
    {
        await StopAsync();
        SetState(WorkerState.Loading);
        try
        {
            if (requested == RecognitionDevice.Gpu && !gpuFailed)
            {
                try
                {
                    await LaunchAsync(RecognitionDevice.Gpu);
                }
                catch (Exception ex)
                {
                    Log.Write($"GPU unavailable, falling back to CPU: {ex.Message}");
                    await StopAsync();
                    SetState(WorkerState.Loading);
                    await LaunchAsync(RecognitionDevice.Cpu);
                }
            }
            else
            {
                await LaunchAsync(RecognitionDevice.Cpu);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"worker start failed: {ex.Message}");
            await StopAsync();
            SetState(WorkerState.Failed);
            throw ex as WorkerException ?? new WorkerException("The recognition worker failed to start.", ex);
        }
        _requested = requested;
        SetState(WorkerState.Ready);
        ScheduleIdleUnload();
    }

    private async Task LaunchAsync(RecognitionDevice device)
    {
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
        ActiveDevice = device;
        Log.Write($"worker ready ({device}) in {watch.ElapsedMilliseconds} ms");
    }

    private void ScheduleIdleUnload()
    {
        var settings = _settings();
        // CPU mode holds only RAM, which the user has plenty of; keeping it avoids a reload on every use.
        var due = settings.Device == RecognitionDevice.Gpu && settings.IdleUnloadMinutes > 0
            ? TimeSpan.FromMinutes(settings.IdleUnloadMinutes)
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
        ActiveDevice = null;
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
