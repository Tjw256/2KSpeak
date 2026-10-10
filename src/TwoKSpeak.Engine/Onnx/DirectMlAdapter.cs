using System.Runtime.InteropServices;

namespace TwoKSpeak.Engine.Onnx;

/// <summary>Use the hardware DXGI adapter with the most dedicated memory, rather than the integrated GPU.</summary>
public static class DirectMlAdapter
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr adapter, out Description description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    public static int Select() => Select(out _);

    public static int Select(out string name)
    {
        name = "";
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in iid, out var factory));
        try
        {
            var enumerate = Method<EnumAdapter>(factory, 12);
            var selected = -1;
            nuint maximum = 0;
            for (uint index = 0; ; index++)
            {
                var result = enumerate(factory, index, out var adapter);
                if (result == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter, 10)(adapter, out var description));
                    if ((description.Flags & 2) == 0 && (selected < 0 || description.DedicatedVideoMemory > maximum))
                    {
                        selected = (int)index;
                        name = description.Name;
                        maximum = description.DedicatedVideoMemory;
                    }
                }
                finally { Marshal.Release(adapter); }
            }
            return selected >= 0 ? selected : throw new InvalidOperationException("No hardware DXGI adapter found.");
        }
        finally { Marshal.Release(factory); }
    }
}
