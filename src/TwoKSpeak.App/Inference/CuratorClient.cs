using System.Diagnostics;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine;
using TwoKSpeak.Engine.Curator;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Owns the cleanup model's llama-server: started when a hold begins, stopped after the idle timeout in GPU mode
/// (like the speech worker), restarted on a model or device change. Every failure path returns null so the caller
/// types the uncleaned text; cleanup must never cost the user their words. Until its download finishes, cleanup is
/// skipped (the word filter still runs), and GPU requests run on the CPU until GPU mode is downloaded.
/// </summary>
public sealed class CuratorClient : IAsyncDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<ComponentId, bool> _isInstalled;
    private readonly Lock _gate = new();
    private readonly Timer _idleTimer;
    private Task<LlamaServer>? _server;
    private (CuratorModel Model, RecognitionDevice Device)? _running;
    private string? _lastMissing;

    public CuratorClient(Func<AppSettings> settings, Func<ComponentId, bool> isInstalled)
    {
        _settings = settings;
        _isInstalled = isInstalled;
        _idleTimer = new Timer(_ => _ = StopAsync("idle"));
    }

    /// <summary>Raised when the server starts or stops (for the tray status and VRAM readout).</summary>
    public event Action? StateChanged;

    public RecognitionDevice? ActiveDevice { get; private set; }

    public int? ProcessId => _server is { IsCompletedSuccessfully: true } task && !task.Result.HasExited ? task.Result.ProcessId : null;

    /// <summary>The model the settings ask for, if it and llama.cpp are downloaded.</summary>
    private CuratorModel? ModelFor(AppSettings settings) => settings.Cleanup switch
    {
        Cleanup.SmallModel when _isInstalled(ComponentId.Cleanup) && _isInstalled(ComponentId.CleanupSmall) => CuratorModel.Small,
        Cleanup.LargeModel when _isInstalled(ComponentId.Cleanup) => CuratorModel.Large,
        _ => null,
    };

    private RecognitionDevice DeviceFor(AppSettings settings) =>
        settings.CleanupDevice == RecognitionDevice.Gpu && _isInstalled(ComponentId.GpuMode) ? RecognitionDevice.Gpu : RecognitionDevice.Cpu;

    /// <summary>
    /// Starts loading in the background when a hold begins, so the model is ready by the first phrase. Called again
    /// for every phrase so a long hold doesn't reach the idle timeout before the cleanup at release.
    /// </summary>
    public void Prewarm()
    {
        if (ModelFor(_settings()) is not null)
        {
            _ = EnsureStartedAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ScheduleIdleStop();
        }
    }

    /// <summary>Cleaned text, or null when cleanup is off, unavailable, too slow, or the answer wasn't deletion-only.</summary>
    public async Task<string?> CleanAsync(string text, TimeSpan timeout, CancellationToken ct)
    {
        if (ModelFor(_settings()) is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // Waiting for the load is bounded by the deadline but doesn't cancel it: the next phrase can use it.
            var server = await EnsureStartedAsync().WaitAsync(deadline.Token);
            var lead = text[..(text.Length - text.TrimStart().Length)];
            var cleaned = new List<string>();
            foreach (var chunk in CuratorPrompt.Chunks(text))
            {
                var answer = await server.CompleteAsync(CuratorPrompt.Build(chunk), deadline.Token);
                if (!DeletionGuard.IsDeletionOnly(chunk, answer))
                {
                    Log.Write($"cleanup rejected: the model changed more than deletions ({chunk.Length} chars)");
                    cleaned.Add(chunk);
                    continue;
                }
                cleaned.Add(answer);
            }
            ScheduleIdleStop();
            var result = lead + string.Join(" ", cleaned);
            Log.Write($"cleanup {text.Length} -> {result.Length} chars in {watch.ElapsedMilliseconds} ms");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write($"cleanup skipped: no answer within {timeout.TotalSeconds:F1} s");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes the server being stopped mid-request; the raw text is still typed.
            Log.Write($"cleanup unavailable: {ex.Message}");
            await StopAsync("error");
            return null;
        }
    }

    /// <summary>Stops a server whose model or device no longer matches the settings; the next hold starts the right one.</summary>
    public Task ApplySettingsAsync()
    {
        var settings = _settings();
        var wanted = ModelFor(settings);
        lock (_gate)
        {
            if (_running is { } running && (wanted != running.Model || DeviceFor(settings) != running.Device))
            {
                return StopAsync("settings changed");
            }
        }
        ScheduleIdleStop();
        return Task.CompletedTask;
    }

    private Task<LlamaServer> EnsureStartedAsync()
    {
        var settings = _settings();
        var model = ModelFor(settings) ?? throw new CuratorException("Cleanup model is off.");
        var device = DeviceFor(settings);
        lock (_gate)
        {
            if (_server is { } existing && _running == (model, device) && !(existing.IsCompletedSuccessfully && existing.Result.HasExited))
            {
                return existing;
            }
            var previous = _server;
            _running = (model, device);
            _server = StartAsync(previous, model, device);
            return _server;
        }
    }

    private async Task<LlamaServer> StartAsync(Task<LlamaServer>? previous, CuratorModel model, RecognitionDevice device)
    {
        await DisposeServerAsync(previous);
        var path = CuratorModels.PathOf(model);
        if (!File.Exists(path))
        {
            if (_lastMissing != path)
            {
                Log.Write($"cleanup model not downloaded: {Path.GetFileName(path)}");
                _lastMissing = path;
            }
            throw new CuratorException($"{Path.GetFileName(path)} is not downloaded.");
        }

        var watch = Stopwatch.StartNew();
        LlamaServer server;
        if (device == RecognitionDevice.Gpu)
        {
            try
            {
                server = await LlamaServer.StartAsync(AppPaths.LlamaRuntime, path, gpu: true, CancellationToken.None);
            }
            catch (CuratorException ex)
            {
                Log.Write($"cleanup GPU unavailable, using CPU: {ex.Message}");
                device = RecognitionDevice.Cpu;
                server = await LlamaServer.StartAsync(AppPaths.LlamaRuntime, path, gpu: false, CancellationToken.None);
            }
        }
        else
        {
            server = await LlamaServer.StartAsync(AppPaths.LlamaRuntime, path, gpu: false, CancellationToken.None);
        }
        ActiveDevice = device;
        Log.Write($"cleanup model {model} ready ({device}) in {watch.ElapsedMilliseconds} ms");
        StateChanged?.Invoke();
        ScheduleIdleStop();
        return server;
    }

    private void ScheduleIdleStop()
    {
        var settings = _settings();
        // Same rule as the speech worker: GPU memory is returned after the idle timeout; CPU mode only holds RAM.
        var due = ActiveDevice == RecognitionDevice.Gpu && settings.IdleUnloadMinutes > 0
            ? TimeSpan.FromMinutes(settings.IdleUnloadMinutes)
            : Timeout.InfiniteTimeSpan;
        _idleTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private Task StopAsync(string reason)
    {
        Task<LlamaServer>? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
            _running = null;
        }
        if (server is null)
        {
            return Task.CompletedTask;
        }
        Log.Write($"cleanup model stopped ({reason})");
        ActiveDevice = null;
        StateChanged?.Invoke();
        return DisposeServerAsync(server);
    }

    private static async Task DisposeServerAsync(Task<LlamaServer>? server)
    {
        if (server is null)
        {
            return;
        }
        try
        {
            await (await server).DisposeAsync();
        }
        catch (CuratorException)
        {
            // It never started; nothing to stop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync();
        await StopAsync("exit");
    }
}
