using TwoKSpeak.Engine.Onnx;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.Engine.Tests;

public class GpuBackendTests
{
    [Fact]
    public void WorkerAndDownloadsMatchTheNativeRuntimeFlavor()
    {
#if ORT_DIRECTML
        Assert.True(GpuBackend.IsDirectMl);
        Assert.Equal("dml", GpuBackend.WorkerDevice);
        Assert.Contains("vulkan", Components.Cleanup(true).Files[0].File.FileName);
        Assert.Equal("llama-vulkan", Path.GetFileName(AppPaths.LlamaRuntime));
        Assert.DoesNotContain(Components.GpuMode.Files, f => f.Directory == AppPaths.CudaRuntime);
        Assert.InRange(Components.GpuMode.Size, 1_200_000_000L, 1_300_000_000L);
#else
        Assert.False(GpuBackend.IsDirectMl);
        Assert.Equal("cuda", GpuBackend.WorkerDevice);
        Assert.Contains("cuda", Components.Cleanup(true).Files[0].File.FileName);
        Assert.Equal("llama", Path.GetFileName(AppPaths.LlamaRuntime));
        Assert.Contains(Components.GpuMode.Files, f => f.Directory == AppPaths.CudaRuntime);
#endif
        Assert.Contains(Components.GpuMode.Files, f => f.File.FileName == "encoder-model.fp16.onnx");
        Assert.Contains(Components.GpuMode.Files, f => f.File.FileName == "decoder_joint-model.fp16.onnx");
    }

    [Fact]
    public void CpuCleanupNeverRequiresAGpuRuntime()
    {
        Assert.Contains("win-cpu", Components.Cleanup(false).Files[0].File.FileName);
        Assert.Equal(Components.Cleanup(true).Files[1], Components.Cleanup(false).Files[1]);
    }
}
