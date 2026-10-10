using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine;

/// <summary>
/// On-disk locations. Large downloads (models, CUDA) live in LocalAppData so they never roam;
/// settings and history live in roaming AppData.
/// </summary>
public static class AppPaths
{
    /// <summary>Redirects both roots, so a first run can be tested without touching the real install or settings.</summary>
    private static readonly string? TestRoot = Environment.GetEnvironmentVariable("TWOKSPEAK_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    public static string LocalRoot { get; } = TestRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2KSpeak");

    public static string RoamingRoot { get; } = TestRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "2KSpeak")
        : Path.Combine(TestRoot, "roaming");

    public static string Models => Path.Combine(LocalRoot, "models");
    public static string ParakeetFp16 => Path.Combine(Models, "parakeet-tdt-0.6b-v3-fp16");
    public static string ParakeetInt8 => Path.Combine(Models, "parakeet-tdt-0.6b-v3");
    public static string SileroVad => Path.Combine(Models, "silero-vad", "silero_vad.onnx");
    public static string CuratorModels => Path.Combine(Models, "curator");
    public static string CudaRuntime => Path.Combine(LocalRoot, "runtimes", "cuda");
    /// <summary>Keep Vulkan separate so DLLs from an existing CUDA install cannot be loaded accidentally.</summary>
    public static string LlamaRuntime => Path.Combine(LocalRoot, "runtimes", GpuBackend.IsDirectMl ? "llama-vulkan" : "llama");
    public static string Logs => Path.Combine(LocalRoot, "logs");
    /// <summary>Partial and not-yet-extracted downloads; emptied as components finish.</summary>
    public static string Downloads => Path.Combine(LocalRoot, "downloads");
    public static string Settings => Path.Combine(RoamingRoot, "settings.json");
    public static string History => Path.Combine(RoamingRoot, "history.json");
}
