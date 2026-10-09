using System.Text.Json;
using System.Text.Json.Serialization;
using TwoKSpeak.Engine;

namespace TwoKSpeak.App.Settings;

public enum RecognitionDevice
{
    Gpu,
    Cpu,
}

/// <summary>User settings, stored as JSON in roaming AppData. Unknown or missing values fall back to defaults.</summary>
public sealed record AppSettings
{
    public RecognitionDevice Device { get; init; } = RecognitionDevice.Gpu;
    /// <summary>GPU mode only: minutes without dictation before the worker exits and VRAM is freed.</summary>
    public int IdleUnloadMinutes { get; init; } = 5;
    /// <summary>Pause that commits a phrase.</summary>
    public int PauseMs { get; init; } = 500;
    /// <summary>WinMM device index; -1 is the system default.</summary>
    public int MicrophoneDevice { get; init; } = -1;
    public bool RemoveFillers { get; init; } = true;
    /// <summary>Keep the model files mapped in RAM so loading to the GPU never waits on the disk.</summary>
    public bool KeepModelInRam { get; init; } = true;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(AppPaths.Settings)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken settings file must not stop dictation; keep it for inspection and start from defaults.
            Diagnostics.Log.Write($"settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.RoamingRoot);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
    }
}
