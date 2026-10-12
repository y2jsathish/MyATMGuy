using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ATM.Infrastructure.Logging;

public sealed record LogQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? MinimumLevel = null,
    string? CorrelationId = null,
    string? Terminal = null,
    string? Text = null);

public sealed record LogLine(DateTimeOffset Timestamp, string Level, string Message, string? CorrelationId, string? Terminal, string Raw);

/// <summary>Streams compact JSON log files and returns the lines matching a query, without loading files into memory.</summary>
public static class LogSearch
{
    private static readonly string[] LevelOrder = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public static async IAsyncEnumerable<LogLine> SearchAsync(string directory, LogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var minRank = query.MinimumLevel is null ? 0 : Math.Max(0, Array.FindIndex(LevelOrder, l => l.Equals(query.MinimumLevel, StringComparison.OrdinalIgnoreCase)));

        foreach (var file in Directory.EnumerateFiles(directory, "*.clef").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.Length == 0 || (query.Text is not null && !line.Contains(query.Text, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var parsed = TryParse(line);
                if (parsed is null || !Matches(parsed, query, minRank))
                {
                    continue;
                }

                yield return parsed;
            }
        }
    }

    private static bool Matches(LogLine line, LogQuery query, int minRank) =>
        (query.From is null || line.Timestamp >= query.From)
        && (query.To is null || line.Timestamp <= query.To)
        && Array.IndexOf(LevelOrder, line.Level) >= minRank
        && (query.CorrelationId is null || string.Equals(line.CorrelationId, query.CorrelationId, StringComparison.OrdinalIgnoreCase))
        && (query.Terminal is null || string.Equals(line.Terminal, query.Terminal, StringComparison.OrdinalIgnoreCase));

    private static LogLine? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("@t", out var t) || !t.TryGetDateTimeOffset(out var timestamp))
            {
                return null;
            }

            var level = root.TryGetProperty("@l", out var l) ? l.GetString() ?? "Information" : "Information";
            var message = root.TryGetProperty("@m", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            return new LogLine(
                timestamp,
                level,
                message,
                root.TryGetProperty("CorrelationId", out var c) ? c.GetString() : null,
                root.TryGetProperty("Terminal", out var term) ? term.GetString() : null,
                line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
