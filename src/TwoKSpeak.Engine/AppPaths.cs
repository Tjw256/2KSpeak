namespace TwoKSpeak.Engine;

/// <summary>
/// On-disk locations. Large downloads (models, CUDA) live in LocalAppData so they never roam;
/// settings and history live in roaming AppData.
/// </summary>
public static class AppPaths
{
    public static string LocalRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2KSpeak");

    public static string RoamingRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "2KSpeak");

    public static string Models => Path.Combine(LocalRoot, "models");
    public static string ParakeetFp16 => Path.Combine(Models, "parakeet-tdt-0.6b-v3-fp16");
    public static string ParakeetInt8 => Path.Combine(Models, "parakeet-tdt-0.6b-v3");
    public static string SileroVad => Path.Combine(Models, "silero-vad", "silero_vad.onnx");
    public static string CudaRuntime => Path.Combine(LocalRoot, "runtimes", "cuda");
    public static string Logs => Path.Combine(LocalRoot, "logs");
    public static string Settings => Path.Combine(RoamingRoot, "settings.json");
    public static string History => Path.Combine(RoamingRoot, "history.json");
}
