using System.Runtime.InteropServices;
using ATM.XfsProbe;

// Read-only XFS probe: start the manager, open one logical service, read its status and capabilities, close it.
// It never sends a command that moves a card, cash or a PIN. Safe to run next to nothing else: stop Kalignite and device services first.
if (args.Length == 0 || args[0] is "--help" or "-h" or "/?")
{
    Console.WriteLine("atmxfsprobe <logical-service-name> [--timeout-ms N]");
    Console.WriteLine("  Opens the XFS logical service (for example the card reader), prints its raw status and capabilities, closes it.");
    Console.WriteLine("  Must run as a 32-bit process on the terminal with the XFS manager (msxfs.dll). Read only.");
    return 2;
}

var name = args[0];
uint timeout = 10000;
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--timeout-ms")
    {
        timeout = uint.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
    }
}

Console.WriteLine($"atmxfsprobe: process is {(Environment.Is64BitProcess ? "64" : "32")}-bit, logical service '{name}'");
if (Environment.Is64BitProcess)
{
    Console.WriteLine("FAIL: this probe must run as 32-bit.");
    return 1;
}

var ranges = new[]
{
    (Low: (ushort)0x0003, High: (ushort)0x0A03, Label: "3.00-3.10"),
    (Low: (ushort)0x0002, High: (ushort)0x0003, Label: "2.00-3.00"),
    (Low: (ushort)0x0003, High: (ushort)0x1403, Label: "3.00-3.20"),
};

try
{
    var started = false;
    uint usedRange = 0;
    foreach (var r in ranges)
    {
        var version = default(Xfs.WfsVersion);
        var code = Xfs.WFSStartUp(Xfs.VersionRange(r.Low, r.High), ref version);
        Console.WriteLine($"WFSStartUp {r.Label}: result {code}; manager version 0x{version.Version:X4}, '{version.Description}', '{version.SystemStatus}'");
        if (code == Xfs.Success)
        {
            started = true;
            usedRange = Xfs.VersionRange(r.Low, r.High);
            break;
        }
    }

    if (!started)
    {
        Console.WriteLine("FAIL: the XFS manager did not start with any version range tried.");
        return 1;
    }

    var srvc = default(Xfs.WfsVersion);
    var spi = default(Xfs.WfsVersion);
    var openCode = Xfs.WFSOpen(name, IntPtr.Zero, "atmxfsprobe", 0, timeout, usedRange, ref srvc, ref spi, out var service);
    Console.WriteLine($"WFSOpen '{name}': result {openCode}{(openCode == Xfs.Success ? $"; service handle {service}, service version 0x{srvc.Version:X4}, SPI version 0x{spi.Version:X4}" : "")}");
    if (openCode != Xfs.Success)
    {
        Console.WriteLine("Meaning (XFS): -3 internal error, -13 invalid hService, -12 service not found/unknown logical name, -18 version error. Check the logical name is registered and Kalignite/device services are stopped.");
        return 1;
    }

    try
    {
        Dump("status", service, Xfs.IdcStatusCategory, timeout);
        Dump("capabilities", service, Xfs.IdcCapabilitiesCategory, timeout);
    }
    finally
    {
        Console.WriteLine($"WFSClose: result {Xfs.WFSClose(service)}");
    }

    return 0;
}
catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
{
    Console.WriteLine($"FAIL: could not use msxfs.dll ({ex.GetType().Name}: {ex.Message})");
    return 1;
}
finally
{
    try
    {
        Console.WriteLine($"WFSCleanUp: result {Xfs.WFSCleanUp()}");
    }
    catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
    {
        // The library never loaded, so there is nothing to clean up.
    }
}

static void Dump(string what, ushort service, uint category, uint timeout)
{
    var code = Xfs.WFSGetInfo(service, category, IntPtr.Zero, timeout, out var resultPtr);
    Console.WriteLine($"WFSGetInfo {what} (category {category}): result {code}");
    if (code != Xfs.Success || resultPtr == IntPtr.Zero)
    {
        return;
    }

    try
    {
        var result = Marshal.PtrToStructure<Xfs.WfsResult>(resultPtr);
        Console.WriteLine($"  completion hResult {result.Result}, buffer {(result.Buffer == IntPtr.Zero ? "none" : "present")}");
        if (result.Buffer == IntPtr.Zero)
        {
            return;
        }

        // Print the first words of the payload raw. For IDC status these are fwDevice, fwMedia, fwRetainBin, fwSecurity, usCards, fwChipPower.
        var words = new short[12];
        Marshal.Copy(result.Buffer, words, 0, words.Length);
        Console.WriteLine("  first 12 words: " + string.Join(' ', words.Select(w => w.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (category == Xfs.IdcStatusCategory)
        {
            Console.WriteLine($"  fwDevice={words[0]} (0 online, 1 offline, 2 power off, 3 no device, 4 hardware error, 5 user error, 6 busy), fwMedia={words[1]} (0 present, 1 not present, 2 jammed, 3 not supported, 4 unknown, 5 entering, 6 latched)");
        }
    }
    finally
    {
        Xfs.WFSFreeResult(resultPtr);
    }
}
