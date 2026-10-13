using System.Text.Json;

namespace ATM.FieldTest;

public enum Outcome
{
    Pass,
    Warn,
    Fail,
    Info,
    Skip,
}

public sealed record Finding(string Section, string Name, Outcome Outcome, string Detail);

/// <summary>Collects findings, prints them as they happen and writes a text and a JSON report at the end.</summary>
public sealed class Report
{
    private readonly List<Finding> findings = [];

    public IReadOnlyList<Finding> Findings => findings;

    public bool HasFailures => findings.Exists(f => f.Outcome == Outcome.Fail);

    public void Add(string section, string name, Outcome outcome, string detail)
    {
        findings.Add(new Finding(section, name, outcome, detail));
        var tag = outcome switch
        {
            Outcome.Pass => "[PASS]",
            Outcome.Warn => "[WARN]",
            Outcome.Fail => "[FAIL]",
            Outcome.Skip => "[SKIP]",
            _ => "[INFO]",
        };
        Console.WriteLine($"{tag} {section} / {name}: {detail}");
    }

    public void Write(string directory, string machine)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, $"atm-fieldtest-{machine}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        File.WriteAllLines(stem + ".txt", findings.Select(f => $"{f.Outcome.ToString().ToUpperInvariant(),-5} {f.Section} / {f.Name}: {f.Detail}"));
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        Console.WriteLine($"Report written: {stem}.txt and .json");
    }
}

public static class Stats
{
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }
}
