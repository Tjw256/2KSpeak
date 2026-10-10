using TwoKSpeak.App.Inference;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.App.Tests;

public class GpuCapabilityTests
{
    [Theory]
    [InlineData("PCI\\VEN_1002&DEV_7550")]
    [InlineData("pci\\ven_8086&dev_56a0")]
    public void AmdAndIntelAreEnabledOnlyWithDirectMl(string device) =>
        Assert.Equal(GpuBackend.IsDirectMl, GpuCapability.SupportsVendor(device));

    [Fact]
    public void NvidiaRemainsSupported() =>
        Assert.True(GpuCapability.SupportsVendor("PCI\\VEN_10DE&DEV_2B85"));

    [Theory]
    [InlineData("")]
    [InlineData("ROOT\\BasicRender")]
    [InlineData("PCI\\VEN_1234&DEV_1111")]
    public void UnknownAdaptersDoNotEnableGpuByDefault(string device) =>
        Assert.False(GpuCapability.SupportsVendor(device));
}
