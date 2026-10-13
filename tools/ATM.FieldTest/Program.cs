using ATM.FieldTest;

var terminalId = "ATM-FIELD";
var soakMinutes = 0;
string? xfsPath = null;
var outDir = Directory.GetCurrentDirectory();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--soak-minutes" when i + 1 < args.Length:
            soakMinutes = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--out" when i + 1 < args.Length:
            outDir = args[++i];
            break;
        case "--xfs-path" when i + 1 < args.Length:
            xfsPath = args[++i];
            break;
        case "--terminal-id" when i + 1 < args.Length:
            terminalId = args[++i];
            break;
        case "--help" or "-h" or "/?":
            Console.WriteLine("atmfieldtest [--soak-minutes N] [--out DIR] [--terminal-id ID] [--xfs-path KEY]");
            Console.WriteLine("  --xfs-path: registry key of the XFS root if it is not HKLM\\SOFTWARE\\XFS, e.g. \"HKLM\\SOFTWARE\\Vendor\\XFS\".");
            Console.WriteLine("  Runs environment, disk, secrets, platform and XFS discovery checks, optionally a soak run, and writes a .txt and .json report.");
            Console.WriteLine("  Exit code 0 = no failures, 1 = at least one failure. It uses temporary folders and does not touch real ATM data.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Use --help.");
            return 2;
    }
}

var report = new Report();
Console.WriteLine("ATM field test (simulators only: real devices need the Phase 2 adapters)");
Checks.Environment(report);
Checks.DiskSync(report);
Checks.Secrets(report);
if (OperatingSystem.IsWindows())
{
    Checks.Xfs(report, xfsPath);
}
else
{
    report.Add("XFS", "discovery", Outcome.Skip, "Windows only");
}

await Checks.PlatformAsync(report, terminalId);
if (soakMinutes > 0)
{
    await Checks.SoakAsync(report, terminalId, soakMinutes);
}

report.Write(outDir, Environment.MachineName);
var failed = report.Findings.Count(f => f.Outcome == Outcome.Fail);
var warned = report.Findings.Count(f => f.Outcome == Outcome.Warn);
Console.WriteLine($"Result: {(failed == 0 ? "PASS" : "FAIL")} ({failed} failed, {warned} warnings)");
return failed == 0 ? 0 : 1;
