using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SqlLineDebugger;

internal static class NativeMethods
{
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int StartDebugger(IntPtr server, string machine, uint pid);
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RequestStop();
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ResumeDebugger();
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FinishDebugger();
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadDebuggerEvent(out IntPtr text);
    [DllImport("SqlDebugBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseDebuggerEvents();
    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr sd, int count, IntPtr services, IntPtr reserved, uint auth,
        uint impersonation, IntPtr authList, uint capabilities, IntPtr reserved2);
    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
    [DllImport("ole32.dll")]
    private static extern int CoUnmarshalInterface(IStream stream, ref Guid iid, out IntPtr result);

    internal static void InitializeSecurity()
    {
        int hr = CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 2, 3, IntPtr.Zero, 0, IntPtr.Zero);
        if (hr != unchecked((int)0x80010119)) Marshal.ThrowExceptionForHR(hr); // RPC_E_TOO_LATE
    }
    internal static void Start(byte[] blob)
    {
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
        try
        {
            stream.Write(blob, blob.Length, IntPtr.Zero);
            stream.Seek(0, 0, IntPtr.Zero);
            var iid = new Guid("87BC18DC-C8B3-11D5-AE96-00B0D0E93CC1");
            Marshal.ThrowExceptionForHR(CoUnmarshalInterface(stream, ref iid, out var pointer));
            try { Marshal.ThrowExceptionForHR(StartDebugger(pointer, Environment.MachineName, (uint)Environment.ProcessId)); }
            finally { Marshal.Release(pointer); }
        }
        finally { Marshal.ReleaseComObject(stream); }
    }
}
