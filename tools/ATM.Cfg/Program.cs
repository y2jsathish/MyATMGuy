using System.Text;
using ATM.Application.Configuration;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Logging;

const string Usage = """
    atmcfg - configuration security tool (run on the target machine)

      atmcfg encrypt [--keys <dir>]            Reads a value from stdin (hidden when interactive) and prints enc:... for config files.
      atmcfg verify <config-dir> [--env <Env>] [--keys <dir>]
                                               Checks validity, plaintext secrets, decryptability and the integrity manifest.
      atmcfg manifest <config-dir>             Writes config.manifest with SHA-256 hashes of the config files.
      atmcfg rotate-key [--keys <dir>]         Creates a new key version. Old versions keep decrypting existing values.
      atmcfg schema <output-file>              Writes the JSON schema generated from the options classes.

    Plaintext values are never printed.
    """;

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string KeysDir(string[] args) =>
    Option(args, "--keys") ?? Environment.GetEnvironmentVariable("ATM_KEYS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "keys");

static string ReadSecret()
{
    if (Console.IsInputRedirected)
    {
        return Console.In.ReadLine() ?? string.Empty;
    }

    Console.Error.Write("Value (hidden): ");
    var buffer = new StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
            }
        }
        else
        {
            buffer.Append(key.KeyChar);
        }
    }

    Console.Error.WriteLine();
    return buffer.ToString();
}

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var environment = Enum.TryParse<AtmEnvironment>(Option(args, "--env"), ignoreCase: true, out var env) ? env : AtmEnvironments.FromEnvironment();

try
{
switch (args[0])
{
    case "encrypt":
    {
        var protector = new SecretProtector(KeyProviderFactory.Create(KeysDir(args), environment));
        var value = ReadSecret();
        if (value.Length == 0)
        {
            Console.Error.WriteLine("No value supplied.");
            return 1;
        }

        Console.WriteLine(protector.Protect(value));
        return 0;
    }

    case "verify" when args.Length > 1:
    {
        var dir = Path.GetFullPath(args[1]);
        var raw = AtmConfiguration.BuildRaw(dir, environment, reloadOnChange: false);
        var flat = AtmConfiguration.Flatten(raw);
        var problems = new List<string>(AtmConfiguration.Validate(flat, environment));

        var protector = new SecretProtector(KeyProviderFactory.Create(KeysDir(args), environment));
        foreach (var (key, value) in flat.Where(kv => SecretProtector.IsProtected(kv.Value)))
        {
            try
            {
                protector.Unprotect(value!);
            }
            catch (Exception ex) when (ex is ATM.Core.Errors.AtmException)
            {
                problems.Add($"{key}: cannot be decrypted with the keys in {KeysDir(args)}.");
            }
        }

        foreach (var key in SecretKeys.FindPlaintext(flat))
        {
            if (environment != AtmEnvironment.Prod)
            {
                problems.Add($"{key}: plaintext secret (allowed outside Prod, but encrypt it).");
            }
        }

        if (File.Exists(Path.Combine(dir, ConfigIntegrity.ManifestName)) || environment == AtmEnvironment.Prod)
        {
            problems.AddRange(ConfigIntegrity.Verify(dir).Problems);
        }

        foreach (var problem in problems)
        {
            Console.WriteLine($"PROBLEM {problem}");
        }

        Console.WriteLine(problems.Count == 0 ? "OK" : $"{problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }

    case "manifest" when args.Length > 1:
    {
        var dir = Path.GetFullPath(args[1]);
        var files = Directory.GetFiles(dir, "appsettings*.json").Concat(Directory.GetFiles(dir, AtmConfiguration.TerminalFile)).Select(Path.GetFileName)!;
        ConfigIntegrity.WriteManifest(dir, files!);
        Console.WriteLine($"Wrote {Path.Combine(dir, ConfigIntegrity.ManifestName)}");
        return 0;
    }

    case "rotate-key":
    {
        var keys = KeyProviderFactory.Create(KeysDir(args), environment);
        Console.WriteLine($"Current key version is now {keys.Rotate()}. Re-encrypt values with 'atmcfg encrypt' to move them to the new key.");
        return 0;
    }

    case "schema" when args.Length > 1:
    {
        File.WriteAllText(args[1], OptionsSchema.GenerateDocument(
            (TerminalOptions.SectionName, typeof(TerminalOptions)),
            (LoggingOptions.SectionName, typeof(LoggingOptions))));
        Console.WriteLine($"Wrote {args[1]}");
        return 0;
    }

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
}
catch (PlatformNotSupportedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
