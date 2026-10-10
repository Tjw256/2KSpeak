namespace TwoKSpeak.Engine.Curator;

public enum CuratorModel
{
    /// <summary>Qwen3.5-0.8B Q8_0: faster on the CPU, cleans less (8/10 in the spike).</summary>
    Small,
    /// <summary>Qwen3.5-2B Q4_K_M: 10/10 in the spike.</summary>
    Large,
}

public static class CuratorModels
{
    public static string FileName(CuratorModel model) => model switch
    {
        CuratorModel.Small => "Qwen3.5-0.8B-Q8_0.gguf",
        _ => "Qwen3.5-2B-Q4_K_M.gguf",
    };

    public static string PathOf(CuratorModel model) => Path.Combine(AppPaths.CuratorModels, FileName(model));
}
