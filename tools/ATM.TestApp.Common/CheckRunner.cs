using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ATM.TestApp.Common;

public sealed record CheckResult(string Name, bool Passed, string? Message, long ElapsedMs);

public sealed class CheckFailedException(string message) : Exception(message);

/// <summary>Tiny assertion helpers so a layer test app needs no test framework and runs anywhere .NET runs.</summary>
public static class Expect
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"{what ?? "Value"}: expected '{expected}' but was '{actual}'.");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new CheckFailedException($"Expected {typeof(TException).Name} but nothing was thrown.");
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new CheckFailedException($"Expected {typeof(TException).Name} but nothing was thrown.");
    }
}

/// <summary>
/// Runs named checks for one layer, prints PASS or FAIL per check and returns a process exit code (0 = all passed).
/// Pass <c>--json</c> to print a machine-readable summary instead.
/// </summary>
public sealed class CheckRunner(string layer)
{
    private readonly List<CheckResult> _results = [];

    public void Check(string name, Action body) =>
        CheckAsync(name, () =>
        {
            body();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    public async Task CheckAsync(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await body();
            _results.Add(new CheckResult(name, true, null, watch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            _results.Add(new CheckResult(name, false, ex is CheckFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", watch.ElapsedMilliseconds));
        }
    }

    public int Complete(string[] args)
    {
        var failed = _results.Count(r => !r.Passed);

        if (args.Contains("--json", StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    layer,
                    os = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                    passed = _results.Count - failed,
                    failed,
                    results = _results,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"{layer} layer test app");
            Console.WriteLine($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
            Console.WriteLine();
            foreach (var r in _results)
            {
                Console.WriteLine(r.Passed ? $"[PASS] {r.Name} ({r.ElapsedMs} ms)" : $"[FAIL] {r.Name}: {r.Message}");
            }

            Console.WriteLine();
            Console.WriteLine($"{_results.Count - failed} passed, {failed} failed.");
        }

        return failed == 0 ? 0 : 1;
    }
}
