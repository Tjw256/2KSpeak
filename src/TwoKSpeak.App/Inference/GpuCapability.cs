using Microsoft.Win32;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Whether this PC has a GPU that can hold the cleanup model next to the speech model: an NVIDIA card (the
/// default build uses CUDA); DirectML builds also support AMD and Intel. Dedicated memory leaves room for other programs.
/// </summary>
public static class GpuCapability
{
    /// <summary>Speech model ~2 GB + 2B curator ~1.8 GB, with headroom.</summary>
    public const long MinimumBytes = 6L * 1024 * 1024 * 1024;

    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static bool CanRunCurator()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            if (adapters is null)
            {
                return false;
            }
            foreach (var name in adapters.GetSubKeyNames())
            {
                using var adapter = adapters.OpenSubKey(name);
                var device = adapter?.GetValue("MatchingDeviceId") as string ?? "";
                // The driver records the real dedicated memory here; WMI's AdapterRAM caps at 4 GB.
                var bytes = adapter?.GetValue("HardwareInformation.qwMemorySize") is long size ? size : 0;
                if (SupportsVendor(device) && bytes >= MinimumBytes)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: assume no, so the default stays on the CPU.
        }
        return false;
    }

    internal static bool SupportsVendor(string device) =>
        device.Contains("ven_10de", StringComparison.OrdinalIgnoreCase) ||
        (GpuBackend.IsDirectMl && (device.Contains("ven_1002", StringComparison.OrdinalIgnoreCase) ||
                                   device.Contains("ven_8086", StringComparison.OrdinalIgnoreCase)));
}
