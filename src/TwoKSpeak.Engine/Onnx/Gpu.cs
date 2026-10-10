using System.Runtime.InteropServices;

namespace TwoKSpeak.Engine.Onnx;

/// <summary>NVIDIA cards run on CUDA; AMD and Intel on DirectML (speech) and Vulkan (cleanup).</summary>
public enum GpuBackend
{
    Cuda,
    DirectML,
}

/// <param name="Index">Position in DXGI's enumeration, which is also DirectML's device id.</param>
/// <param name="SharedBytes">System memory the GPU may borrow; what an integrated GPU mostly runs from.</param>
public sealed record GpuAdapter(int Index, string Name, uint VendorId, long DedicatedBytes, long SharedBytes);

/// <param name="MeasureFirst">
/// An integrated GPU (or a card with little memory of its own): a fast one beats the CPU, a weak one is several times
/// slower, so it is used only after a speed test shows it is faster.
/// </param>
public sealed record GpuChoice(GpuAdapter Adapter, GpuBackend Backend, bool MeasureFirst)
{
    public string WorkerDevice => Backend == GpuBackend.DirectML ? "dml" : "cuda";
}

/// <summary>Picks the GPU 2KSpeak uses. The app and the worker run the same rule, so they agree on the adapter.</summary>
public static class Gpu
{
    public const uint Nvidia = 0x10de;
    public const uint Amd = 0x1002;
    public const uint Intel = 0x8086;

    /// <summary>Speech model ~2 GB + 2B curator ~1.8 GB, with headroom.</summary>
    public const long MinimumBytes = 6L * 1024 * 1024 * 1024;

    private static readonly Lazy<GpuChoice?> Detection = new(() =>
    {
        var adapters = Adapters();
        // Manual pick for multi-GPU PCs, and for testing another adapter's path: the DXGI index.
        return Environment.GetEnvironmentVariable("TWOKSPEAK_GPU") is { Length: > 0 } forced && int.TryParse(forced, out var index)
            ? Choose(adapters.Where(a => a.Index == index).ToList())
            : Choose(adapters);
    });

    /// <summary>The GPU to use on this PC, or null to stay on the CPU.</summary>
    public static GpuChoice? Detected => Detection.Value;

    /// <summary>
    /// An NVIDIA card with enough memory first (CUDA is the fastest path), then an AMD or Intel card with enough
    /// memory, then an AMD integrated GPU with enough shared memory, pending a speed test.
    /// </summary>
    public static GpuChoice? Choose(IReadOnlyList<GpuAdapter> adapters)
    {
        GpuAdapter? Largest(Func<GpuAdapter, bool> filter) => adapters.Where(filter).MaxBy(a => a.DedicatedBytes);

        if (Largest(a => a.VendorId == Nvidia && a.DedicatedBytes >= MinimumBytes) is { } nvidia)
        {
            return new GpuChoice(nvidia, GpuBackend.Cuda, MeasureFirst: false);
        }
        if (Largest(a => a.VendorId is Amd or Intel && a.DedicatedBytes >= MinimumBytes) is { } card)
        {
            return new GpuChoice(card, GpuBackend.DirectML, MeasureFirst: false);
        }
        if (Largest(a => a.VendorId == Amd && a.DedicatedBytes + a.SharedBytes >= MinimumBytes) is { } integrated)
        {
            return new GpuChoice(integrated, GpuBackend.DirectML, MeasureFirst: true);
        }
        return null;
    }

    /// <summary>Hardware adapters in DXGI order; empty when DXGI is unavailable.</summary>
    public static IReadOnlyList<GpuAdapter> Adapters()
    {
        try
        {
            return EnumerateDxgi();
        }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDesc1 description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE: Microsoft Basic Render Driver
    private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND: past the last adapter

    /// <summary>Calls through the COM vtables: IDXGIFactory1::EnumAdapters1 is slot 12, IDXGIAdapter1::GetDesc1 slot 10.</summary>
    private static List<GpuAdapter> EnumerateDxgi()
    {
        var adapters = new List<GpuAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in iid, out var factory));
        try
        {
            var enumerate = Method<EnumAdapters1>(factory, 12);
            for (uint index = 0; ; index++)
            {
                var result = enumerate(factory, index, out var adapter);
                if (result == NotFound)
                {
                    break;
                }
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDesc1>(adapter, 10)(adapter, out var desc));
                    if ((desc.Flags & SoftwareAdapter) == 0)
                    {
                        adapters.Add(new GpuAdapter((int)index, desc.Description, desc.VendorId,
                            (long)desc.DedicatedVideoMemory, (long)desc.SharedSystemMemory));
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return adapters;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
}
