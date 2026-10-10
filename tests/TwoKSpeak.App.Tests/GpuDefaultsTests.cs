using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.App.Tests;

public class GpuDefaultsTests
{
    private static readonly GpuChoice Nvidia = new(new(0, "NVIDIA GeForce RTX 5090", Gpu.Nvidia, 32L << 30, 16L << 30), GpuBackend.Cuda, false);
    private static readonly GpuChoice AmdCard = new(new(0, "AMD Radeon RX 9070 XT", Gpu.Amd, 16L << 30, 16L << 30), GpuBackend.DirectML, false);
    private static readonly GpuChoice AmdIntegrated = new(new(0, "AMD Radeon 780M Graphics", Gpu.Amd, 512L << 20, 16L << 30), GpuBackend.DirectML, true);

    private static readonly AppSettings OnCpu = new() { Device = RecognitionDevice.Cpu, CleanupDevice = RecognitionDevice.Cpu };

    [Fact]
    public void FirstRunUsesAGpuWithEnoughMemoryForBoth()
    {
        var settings = new AppSettings().WithGpuDefaults(AmdCard, firstRun: true);

        Assert.Equal((RecognitionDevice.Gpu, RecognitionDevice.Gpu, false), (settings.Device, settings.CleanupDevice, settings.GpuSpeedTestPending));
        Assert.Equal("AMD Radeon RX 9070 XT", settings.GpuChosenFor);
    }

    [Fact]
    public void FirstRunOnAnIntegratedGpuStartsOnTheCpuUntilTheSpeedTest()
    {
        var settings = new AppSettings().WithGpuDefaults(AmdIntegrated, firstRun: true);

        Assert.Equal((RecognitionDevice.Cpu, RecognitionDevice.Cpu, true), (settings.Device, settings.CleanupDevice, settings.GpuSpeedTestPending));
    }

    [Fact]
    public void FirstRunWithoutAGpuUsesTheCpu()
    {
        var settings = new AppSettings().WithGpuDefaults(null, firstRun: true);

        Assert.Equal((RecognitionDevice.Cpu, RecognitionDevice.Cpu, false), (settings.Device, settings.CleanupDevice, settings.GpuSpeedTestPending));
    }

    [Fact]
    public void AmdCardLeftOnTheCpuByAnEarlierVersionMovesToTheGpuOnce()
    {
        var upgraded = OnCpu.WithGpuDefaults(AmdCard, firstRun: false);
        Assert.Equal(RecognitionDevice.Gpu, upgraded.Device);

        // Later, the user's choice stands.
        var chosen = upgraded with { Device = RecognitionDevice.Cpu };
        Assert.Same(chosen, chosen.WithGpuDefaults(AmdCard, firstRun: false));
    }

    [Fact]
    public void NvidiaUsersKeepTheirDevices()
    {
        Assert.Same(OnCpu, OnCpu.WithGpuDefaults(Nvidia, firstRun: false));
    }

    [Fact]
    public void SettingsFromBeforeCleanupGetACleanupDevice()
    {
        var old = new AppSettings { Device = RecognitionDevice.Cpu, CleanupDevice = null, GpuChosenFor = "AMD Radeon RX 9070 XT" };

        Assert.Equal(RecognitionDevice.Gpu, old.WithGpuDefaults(AmdCard, firstRun: false).CleanupDevice);
    }

    [Fact]
    public void FasterIntegratedGpuTakesOverBothModels()
    {
        var pending = OnCpu with { GpuSpeedTestPending = true };

        var settings = pending.WithSpeedTestResult(gpuFaster: true);

        Assert.Equal((RecognitionDevice.Gpu, RecognitionDevice.Gpu, false), (settings.Device, settings.CleanupDevice, settings.GpuSpeedTestPending));
    }

    [Fact]
    public void SlowerIntegratedGpuOrAUserChoiceKeepsTheDevices()
    {
        var pending = OnCpu with { GpuSpeedTestPending = true };
        Assert.Equal(RecognitionDevice.Cpu, pending.WithSpeedTestResult(gpuFaster: false).Device);

        var chosen = pending with { CleanupDevice = RecognitionDevice.Gpu };
        var settings = chosen.WithSpeedTestResult(gpuFaster: true);
        Assert.Equal((RecognitionDevice.Cpu, RecognitionDevice.Gpu, false), (settings.Device, settings.CleanupDevice, settings.GpuSpeedTestPending));
    }

    [Fact]
    public void WorkerDeviceFollowsTheGpu()
    {
        Assert.Equal("dml", WorkerClient.DeviceArgument(AmdCard, RecognitionDevice.Gpu));
        Assert.Equal("cuda", WorkerClient.DeviceArgument(Nvidia, RecognitionDevice.Gpu));
        Assert.Equal("cuda", WorkerClient.DeviceArgument(null, RecognitionDevice.Gpu)); // chosen by hand; falls back to the CPU
        Assert.Equal("cpu", WorkerClient.DeviceArgument(AmdCard, RecognitionDevice.Cpu));
    }

    [Fact]
    public void CpuAndCudaUseTheMainWorker()
    {
        var main = Path.Combine(AppContext.BaseDirectory, "2KSpeak.Worker.exe");
        Assert.Equal(main, WorkerClient.ExePath(AmdCard, RecognitionDevice.Cpu));
        Assert.Equal(main, WorkerClient.ExePath(Nvidia, RecognitionDevice.Gpu));
    }
}
