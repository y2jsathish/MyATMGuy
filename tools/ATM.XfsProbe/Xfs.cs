using System.Runtime.InteropServices;

namespace ATM.XfsProbe;

/// <summary>
/// Minimal binding to the standard XFS manager (msxfs.dll, 32-bit). Only what a read-only status probe needs.
/// Structure layouts follow the CEN/XFS 3.x headers (packed to 1 byte); results are printed raw so a wrong assumption shows up in the report.
/// </summary>
internal static class Xfs
{
    public const int Success = 0;
    public const uint IdcStatusCategory = 201;       // WFS_INF_IDC_STATUS
    public const uint IdcCapabilitiesCategory = 202; // WFS_INF_IDC_CAPABILITIES

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct WfsVersion
    {
        public ushort Version;
        public ushort LowVersion;
        public ushort HighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string SystemStatus;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WfsResult
    {
        public uint RequestId;
        public ushort Service;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        public int Result;
        public uint CommandOrEvent;
        public IntPtr Buffer;
    }

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WFSStartUp(uint versionsRequired, ref WfsVersion version);

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WFSCleanUp();

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int WFSOpen(string logicalName, IntPtr app, string appId, uint traceLevel, uint timeout, uint versionsRequired, ref WfsVersion srvcVersion, ref WfsVersion spiVersion, out ushort service);

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WFSClose(ushort service);

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WFSGetInfo(ushort service, uint category, IntPtr query, uint timeout, out IntPtr result);

    [DllImport("msxfs.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WFSFreeResult(IntPtr result);

    /// <summary>XFS versions are WORDs with the major in the low byte (3.00 = 0x0003, 3.10 = 0x0A03); a range is packed as high-word then low-word.</summary>
    public static uint VersionRange(ushort low, ushort high) => ((uint)high << 16) | low;
}
