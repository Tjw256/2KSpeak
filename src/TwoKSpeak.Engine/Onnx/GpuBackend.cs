namespace TwoKSpeak.Engine.Onnx;

/// <summary>Keep worker selection and first-run downloads consistent with the shipped native runtime.</summary>
public static class GpuBackend
{
    public static bool IsDirectMl
    {
        get
        {
#if ORT_DIRECTML
            return true;
#else
            return false;
#endif
        }
    }

    public static string WorkerDevice => IsDirectMl ? "dml" : "cuda";
}
