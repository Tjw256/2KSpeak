using System.Text.Json;
using System.Text.Json.Serialization;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Input;

namespace TwoKSpeak.App.Settings;

public enum RecognitionDevice
{
    Gpu,
    Cpu,
}

/// <summary>How much tidying the text gets before it is typed.</summary>
public enum Cleanup
{
    /// <summary>Exactly what was recognised.</summary>
    Off,
    /// <summary>Hesitation sounds (um, ehm, eee) removed by the word filter.</summary>
    Filter,
    /// <summary>Filter, then Qwen3.5-0.8B deletes fillers and self-corrections.</summary>
    SmallModel,
    /// <summary>Filter, then Qwen3.5-2B deletes fillers and self-corrections.</summary>
    LargeModel,
}

public enum TypeWhen
{
    /// <summary>Each phrase is typed after a short pause while the keys are still held.</summary>
    Speaking,
    /// <summary>Everything is typed at once when the keys are released.</summary>
    Released,
}

/// <summary>User settings, stored as JSON in roaming AppData. Unknown or missing values fall back to defaults.</summary>
public sealed record AppSettings
{
    public Modifiers Hotkey { get; init; } = Input.Hotkey.Default;
    public RecognitionDevice Device { get; init; } = RecognitionDevice.Gpu;
    /// <summary>GPU mode only: minutes without dictation before the worker exits and VRAM is freed; 0 keeps it loaded.</summary>
    public int IdleUnloadMinutes { get; init; } = 5;
    /// <summary>Pause that commits a phrase.</summary>
    public int PauseMs { get; init; } = 500;
    /// <summary>Microphone product name as Windows reports it; null is the system default.</summary>
    public string? Microphone { get; init; }
    public Cleanup Cleanup { get; init; } = Cleanup.LargeModel;
    /// <summary>Where the cleanup model runs; null until the first start picks one from the GPU's capability.</summary>
    public RecognitionDevice? CleanupDevice { get; init; }
    public TypeWhen TypeWhen { get; init; } = TypeWhen.Released;
    /// <summary>Keep the model files mapped in RAM so loading to the GPU never waits on the disk.</summary>
    public bool KeepModelInRam { get; init; } = true;
    /// <summary>Keep the last transcripts on disk so the tray history survives a restart.</summary>
    public bool SaveHistory { get; init; } = true;
    public bool StartWithWindows { get; init; } = true;

    public const int PauseMinMs = 250;
    public const int PauseMaxMs = 1500;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Clamps values a hand-edited file could put out of range.</summary>
    public AppSettings Normalized() => this with
    {
        Hotkey = Input.Hotkey.Problem(Hotkey) is null ? Hotkey : Input.Hotkey.Default,
        IdleUnloadMinutes = Math.Clamp(IdleUnloadMinutes, 0, 24 * 60),
        PauseMs = Math.Clamp(PauseMs, PauseMinMs, PauseMaxMs),
    };
}

/// <summary>Current settings plus persistence. Every change is saved immediately and announced once.</summary>
public sealed class SettingsStore(string path)
{
    public AppSettings Current { get; private set; } = Load(path);

    /// <summary>Raised on the calling thread with (previous, current).</summary>
    public event Action<AppSettings, AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var previous = Current;
        var next = change(previous).Normalized();
        if (next == previous)
        {
            return;
        }
        Current = next;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(next, AppSettings.Json));
        }
        catch (IOException ex)
        {
            // The change still applies for this session; only persistence failed.
            Log.Write($"settings not saved: {ex.Message}");
        }
        Changed?.Invoke(previous, next);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), AppSettings.Json) ?? new AppSettings()).Normalized()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken settings file must not stop dictation; leave it for inspection and start from defaults.
            Log.Write($"settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }
}
