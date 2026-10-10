using TwoKSpeak.Engine.Curator;
using TwoKSpeak.Engine.Onnx;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.Engine.Tests;

public class GpuTests
{
    private const long GiB = 1L << 30;

    private static GpuAdapter Adapter(int index, uint vendor, double dedicatedGiB, double sharedGiB = 16, string? name = null) =>
        new(index, name ?? $"adapter {index}", vendor, (long)(dedicatedGiB * GiB), (long)(sharedGiB * GiB));

    [Fact]
    public void NvidiaWithEnoughMemoryUsesCudaEvenNextToAnAmdIntegratedGpu()
    {
        // This development PC: RTX 5090 and the Ryzen 7 7700's integrated Radeon.
        var choice = Gpu.Choose([Adapter(0, Gpu.Nvidia, 32), Adapter(1, Gpu.Amd, 0.5)]);

        Assert.Equal((0, GpuBackend.Cuda, false), (choice!.Adapter.Index, choice.Backend, choice.MeasureFirst));
        Assert.Equal("cuda", choice.WorkerDevice);
    }

    [Theory]
    [InlineData(Gpu.Amd, 16)] // RX 9070 XT
    [InlineData(Gpu.Amd, 8)] // RX 6600 / 7600
    [InlineData(Gpu.Intel, 12)] // Arc B580
    public void AmdAndIntelCardsWithEnoughMemoryUseDirectMl(uint vendor, double dedicatedGiB)
    {
        var choice = Gpu.Choose([Adapter(0, vendor, dedicatedGiB)]);

        Assert.Equal((GpuBackend.DirectML, false), (choice!.Backend, choice.MeasureFirst));
        Assert.Equal("dml", choice.WorkerDevice);
    }

    [Fact]
    public void AmdIntegratedGpuIsUsedOnlyAfterASpeedTest()
    {
        var choice = Gpu.Choose([Adapter(0, Gpu.Amd, 0.5, sharedGiB: 15.5)]);

        Assert.Equal((GpuBackend.DirectML, true), (choice!.Backend, choice.MeasureFirst));
    }

    [Fact]
    public void LaptopWithASmallNvidiaCardTriesItsAmdIntegratedGpu()
    {
        var choice = Gpu.Choose([Adapter(0, Gpu.Nvidia, 4), Adapter(1, Gpu.Amd, 0.5)]);

        Assert.Equal((1, GpuBackend.DirectML, true), (choice!.Adapter.Index, choice.Backend, choice.MeasureFirst));
    }

    [Fact]
    public void DiscreteCardWinsOverAnIntegratedGpu()
    {
        var choice = Gpu.Choose([Adapter(0, Gpu.Amd, 0.5), Adapter(1, Gpu.Amd, 16)]);

        Assert.Equal((1, false), (choice!.Adapter.Index, choice.MeasureFirst));
    }

    [Theory]
    [InlineData(Gpu.Intel, 0.125, 16)] // Intel integrated graphics: not tested, stays on the CPU
    [InlineData(Gpu.Amd, 0.5, 3.5)] // AMD integrated GPU on an 8 GB laptop: no room for the GPU models
    [InlineData(Gpu.Nvidia, 4, 16)] // NVIDIA below 6 GB, as before
    [InlineData(0x1234u, 16, 16)] // unknown vendor
    public void OtherGpusStayOnTheCpu(uint vendor, double dedicatedGiB, double sharedGiB) =>
        Assert.Null(Gpu.Choose([Adapter(0, vendor, dedicatedGiB, sharedGiB)]));

    [Fact]
    public void NoAdaptersMeansCpu() => Assert.Null(Gpu.Choose([]));

    [Fact]
    public void DownloadsMatchTheBackend()
    {
        Assert.Contains("cuda", Components.Cleanup(GpuBackend.Cuda).Files[0].File.FileName);
        Assert.Contains("vulkan", Components.Cleanup(GpuBackend.DirectML).Files[0].File.FileName);
        Assert.Contains("win-cpu", Components.Cleanup(null).Files[0].File.FileName);
        Assert.Equal(Components.Cleanup(GpuBackend.Cuda).Files[1], Components.Cleanup(null).Files[1]);

        Assert.Contains(Components.GpuMode(GpuBackend.Cuda).Files, f => f.Directory == AppPaths.CudaRuntime);
        var directMl = Components.GpuMode(GpuBackend.DirectML);
        Assert.DoesNotContain(directMl.Files, f => f.Directory == AppPaths.CudaRuntime);
        Assert.InRange(directMl.Size, 1_200_000_000L, 1_300_000_000L);
        foreach (var gpuMode in new[] { Components.GpuMode(GpuBackend.Cuda), directMl })
        {
            Assert.Contains(gpuMode.Files, f => f.File.FileName == "encoder-model.fp16.onnx");
            Assert.Contains(gpuMode.Files, f => f.File.FileName == "decoder_joint-model.fp16.onnx");
        }
    }

    [Fact]
    public void VulkanDeviceIsMatchedByAdapterName()
    {
        const string listing = "Available devices:\r\n" +
            "  Vulkan0: NVIDIA GeForce RTX 5090 (32187 MiB, 31419 MiB free)\r\n" +
            "  Vulkan1: AMD Radeon(TM) Graphics (31881 MiB, 30287 MiB free)\r\n";

        Assert.Equal("Vulkan1", LlamaServer.ParseVulkanDevice(listing, "AMD Radeon(TM) Graphics"));
        Assert.Equal("Vulkan0", LlamaServer.ParseVulkanDevice(listing, "NVIDIA GeForce RTX 5090"));
        Assert.Null(LlamaServer.ParseVulkanDevice(listing, "AMD Radeon RX 9070 XT"));
    }
}
