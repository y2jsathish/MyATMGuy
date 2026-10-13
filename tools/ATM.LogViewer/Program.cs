using ATM.Infrastructure.Logging;

// atmlog <log-directory> [--from <time>] [--to <time>] [--level <Level>] [--correlation <id>] [--terminal <id>] [--text <text>] [--max <n>]
static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: atmlog <log-directory> [--from t] [--to t] [--level L] [--correlation id] [--terminal id] [--text s] [--max n]");
    return 2;
}

static DateTimeOffset? Time(string? text) =>
    text is not null && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

var query = new LogQuery(
    Time(Option(args, "--from")),
    Time(Option(args, "--to")),
    Option(args, "--level"),
    Option(args, "--correlation"),
    Option(args, "--terminal"),
    Option(args, "--text"));
var max = int.TryParse(Option(args, "--max"), out var m) ? m : int.MaxValue;

var count = 0;
await foreach (var line in LogSearch.SearchAsync(args[0], query))
{
    Console.WriteLine($"{line.Timestamp:O} [{line.Level,-11}] {line.Terminal} {line.CorrelationId} {line.Message}");
    if (++count >= max)
    {
        break;
    }
}

Console.Error.WriteLine($"{count} line(s).");
return 0;
