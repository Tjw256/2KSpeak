using TwoKSpeak.App.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace TwoKSpeak.App.Setup;

/// <summary>
/// Checks GitHub releases shortly after start and then daily, and downloads a new version silently. It is applied the
/// next time 2KSpeak starts (Velopack does that on launch), or right away from the tray panel's "Restart".
/// Does nothing in a development build, which Velopack didn't install.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string Repository = "https://github.com/Tjw256/2KSpeak";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager _manager = new(new GithubSource(Repository, null, false));
    private readonly Timer _timer;
    private int _checking;
    private VelopackAsset? _ready;

    public Updater()
    {
        _timer = new Timer(_ => _ = CheckAsync());
    }

    /// <summary>A downloaded update is waiting for a restart. Raised on a background thread.</summary>
    public event Action? Ready;

    public string? ReadyVersion => _ready?.Version.ToString();

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            Log.Write("not installed by the installer; updates off");
            return;
        }
        _ready = _manager.UpdatePendingRestart;
        _timer.Change(FirstCheck, CheckInterval);
    }

    private async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null || update.TargetFullRelease.Version.ToString() == ReadyVersion)
            {
                return;
            }
            Log.Write($"downloading update {update.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            Log.Write($"update {_ready.Version} ready");
            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable: try again at the next check.
            Log.Write($"update check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Exits, installs the downloaded version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_ready is { } ready)
        {
            Log.Write($"restarting into {ready.Version}");
            _manager.ApplyUpdatesAndRestart(ready);
        }
    }

    public void Dispose() => _timer.Dispose();
}
