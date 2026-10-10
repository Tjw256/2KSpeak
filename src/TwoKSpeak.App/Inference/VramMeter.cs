using System.Diagnostics;

namespace TwoKSpeak.App.Inference;

/// <summary>Dedicated VRAM charged to one process (the WDDM per-process counter), so other GPU users don't count.</summary>
public static class VramMeter
{
    private const string Category = "GPU Process Memory";

    /// <summary>Bytes in use, or null when the counter is unavailable (no WDDM GPU, counters disabled).</summary>
    public static long? DedicatedBytes(int processId)
    {
        try
        {
            var prefix = $"pid_{processId}_";
            long bytes = 0;
            foreach (var instance in new PerformanceCounterCategory(Category).GetInstanceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            {
                using var counter = new PerformanceCounter(Category, "Dedicated Usage", instance, readOnly: true);
                bytes += counter.RawValue;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
