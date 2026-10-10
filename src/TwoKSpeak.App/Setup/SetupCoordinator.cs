using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.App.Setup;

public enum ComponentState
{
    Waiting,
    Working,
    Ready,
    Failed,
}

/// <param name="Done">Bytes of this component already downloaded (or verified).</param>
/// <param name="Error">Why it stopped, in words for the user; only when <see cref="State"/> is Failed.</param>
public sealed record ComponentStatus(Component Component, ComponentState State, InstallStage Stage, long Done, string? Error);

/// <summary>
/// Downloads what the settings need, one component at a time in the background: speech first so dictation works as
/// soon as possible, then cleanup, then GPU mode. Choosing something that isn't downloaded (the small cleanup model,
/// GPU speech) queues it. Network outages are waited out inside the installer; anything else stops the queue until
/// <see cref="Retry"/>.
/// </summary>
public sealed class SetupCoordinator
{
    private readonly ComponentInstaller _installer;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<ComponentId, Component> _components;
    private readonly HashSet<ComponentId> _ready = [];
    private readonly Lock _gate = new();
    private Task? _run;
    private InstallProgress? _progress;
    private (ComponentId Id, string Message)? _failure;
    private double _bytesPerSecond;
    private (DateTime Time, long Done)? _sample;

    /// <param name="gpuCapable">Picks the GPU build of llama.cpp; see <see cref="Components.Cleanup"/>.</param>
    public SetupCoordinator(ComponentInstaller installer, bool gpuCapable, Func<AppSettings> settings)
    {
        _installer = installer;
        _settings = settings;
        _components = new[] { Components.Speech, Components.Cleanup(gpuCapable), Components.CleanupSmall, Components.GpuMode }
            .ToDictionary(c => c.Id);
        foreach (var component in _components.Values.Where(installer.IsInstalled))
        {
            _ready.Add(component.Id);
        }
    }

    /// <summary>Progress or state changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A component finished installing. Raised on a background thread.</summary>
    public event Action<ComponentId>? Installed;

    /// <summary>The queue emptied after installing at least one component this session.</summary>
    public event Action? Finished;

    /// <summary>The queue stopped on an error the user has to see.</summary>
    public event Action<string>? Failed;

    public bool IsReady(ComponentId id)
    {
        lock (_gate)
        {
            return _ready.Contains(id);
        }
    }

    /// <summary>The components the current settings need, in download order.</summary>
    public IReadOnlyList<Component> Plan()
    {
        var settings = _settings();
        var plan = new List<Component> { _components[ComponentId.Speech] };
        if (settings.Cleanup is Cleanup.SmallModel or Cleanup.LargeModel)
        {
            plan.Add(_components[ComponentId.Cleanup]); // also carries llama.cpp, which the small model needs too
        }
        if (settings.Cleanup == Cleanup.SmallModel)
        {
            plan.Add(_components[ComponentId.CleanupSmall]);
        }
        if (settings.Device == RecognitionDevice.Gpu || settings.CleanupDevice == RecognitionDevice.Gpu)
        {
            plan.Add(_components[ComponentId.GpuMode]);
        }
        return plan;
    }

    public IReadOnlyList<ComponentStatus> Status()
    {
        lock (_gate)
        {
            return Plan().Select(c =>
            {
                if (_ready.Contains(c.Id))
                {
                    return new ComponentStatus(c, ComponentState.Ready, InstallStage.Done, c.Size, null);
                }
                if (_failure is { } failure && failure.Id == c.Id)
                {
                    return new ComponentStatus(c, ComponentState.Failed, InstallStage.Downloading, 0, failure.Message);
                }
                if (_progress is { } progress && progress.Id == c.Id)
                {
                    return new ComponentStatus(c, ComponentState.Working, progress.Stage, progress.Done, null);
                }
                return new ComponentStatus(c, ComponentState.Waiting, InstallStage.Downloading, 0, null);
            }).ToList();
        }
    }

    public bool IsComplete => Status().All(s => s.State == ComponentState.Ready);

    /// <summary>Estimated time to download everything still missing; null until the speed is known.</summary>
    public TimeSpan? Remaining()
    {
        var left = Status().Where(s => s.State != ComponentState.Ready).Sum(s => s.Component.Size - s.Done);
        lock (_gate)
        {
            return _bytesPerSecond > 0 && left > 0 ? TimeSpan.FromSeconds(left / _bytesPerSecond) : null;
        }
    }

    /// <summary>Starts downloading whatever the settings need and isn't there yet; does nothing if already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false } || _failure is not null)
            {
                return;
            }
            _run = Task.Run(RunAsync);
        }
    }

    public void Retry()
    {
        lock (_gate)
        {
            _failure = null;
        }
        Changed?.Invoke();
        Start();
    }

    private async Task RunAsync()
    {
        var installedAny = false;
        while (true)
        {
            Component? next;
            lock (_gate)
            {
                next = Plan().FirstOrDefault(c => !_ready.Contains(c.Id));
                _progress = next is null ? null : new InstallProgress(next.Id, InstallStage.Verifying, 0, next.Size);
            }
            if (next is null)
            {
                break;
            }
            Changed?.Invoke();
            Log.Write($"downloading {next.Id} ({next.Size / 1_000_000} MB)");
            try
            {
                await _installer.InstallAsync(next, new Reporter(this), CancellationToken.None);
            }
            catch (Exception ex)
            {
                var message = ex is InstallException ? ex.Message : $"Something went wrong while setting up {next.Title.ToLowerInvariant()}.";
                Log.Write($"download of {next.Id} stopped: {ex}");
                lock (_gate)
                {
                    _failure = (next.Id, message);
                    _progress = null;
                }
                Changed?.Invoke();
                Failed?.Invoke(message);
                return;
            }
            lock (_gate)
            {
                _ready.Add(next.Id);
                _progress = null;
            }
            installedAny = true;
            Log.Write($"{next.Id} installed");
            Installed?.Invoke(next.Id);
            Changed?.Invoke();
        }
        if (installedAny)
        {
            Finished?.Invoke();
        }
    }

    private void OnProgress(InstallProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
            // Smoothed download speed for the time estimate; restarts after a pause so it doesn't count the wait.
            var now = DateTime.UtcNow;
            if (progress.Stage != InstallStage.Downloading)
            {
                _sample = null;
            }
            else if (_sample is not { } sample || progress.Done < sample.Done)
            {
                _sample = (now, progress.Done);
            }
            else if ((now - sample.Time).TotalSeconds >= 1)
            {
                var speed = (progress.Done - sample.Done) / (now - sample.Time).TotalSeconds;
                _bytesPerSecond = _bytesPerSecond == 0 ? speed : 0.8 * _bytesPerSecond + 0.2 * speed;
                _sample = (now, progress.Done);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the UI thread's context).</summary>
    private sealed class Reporter(SetupCoordinator owner) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => owner.OnProgress(value);
    }
}
