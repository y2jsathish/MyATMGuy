using System.Runtime.Versioning;
using System.Security.Cryptography;
using ATM.Application.Configuration;

namespace ATM.Infrastructure.Configuration;

/// <summary>
/// Keeps versioned 256-bit keys as files (<c>key-v1.bin</c>, <c>key-v2.bin</c> ...). Subclasses decide how a key is protected at rest.
/// </summary>
public abstract class VersionedFileKeyProvider : IKeyProvider
{
    private readonly string directory;
    private readonly object sync = new();

    protected VersionedFileKeyProvider(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        if (Versions().Count == 0)
        {
            Rotate();
        }
    }

    public int CurrentVersion
    {
        get
        {
            lock (sync)
            {
                return Versions().Max();
            }
        }
    }

    public KeyMaterial GetKey(int version)
    {
        lock (sync)
        {
            var path = PathFor(version);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Key version {version} does not exist.");
            }

            return new KeyMaterial(version, Unprotect(File.ReadAllBytes(path)));
        }
    }

    public int Rotate()
    {
        lock (sync)
        {
            var next = Versions().DefaultIfEmpty(0).Max() + 1;
            var path = PathFor(next);
            File.WriteAllBytes(path, Protect(RandomNumberGenerator.GetBytes(32)));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return next;
        }
    }

    protected abstract byte[] Protect(byte[] key);

    protected abstract byte[] Unprotect(byte[] stored);

    private string PathFor(int version) => Path.Combine(directory, $"key-v{version}.bin");

    private List<int> Versions() =>
        [.. Directory.GetFiles(directory, "key-v*.bin")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(5), out var v) ? v : 0)
            .Where(v => v > 0)];
}

/// <summary>Production provider: keys are DPAPI protected with machine scope, so only this machine can read them.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProvider(string directory) : VersionedFileKeyProvider(directory)
{
    protected override byte[] Protect(byte[] key) => ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);

    protected override byte[] Unprotect(byte[] stored) => ProtectedData.Unprotect(stored, null, DataProtectionScope.LocalMachine);
}

/// <summary>Development and test provider (non-Windows). Keys are plain files with owner-only permissions. Never use for Prod.</summary>
public sealed class FileKeyProvider(string directory) : VersionedFileKeyProvider(directory)
{
    protected override byte[] Protect(byte[] key) => key;

    protected override byte[] Unprotect(byte[] stored) => stored;
}

public static class KeyProviderFactory
{
    public static IKeyProvider Create(string directory, AtmEnvironment environment)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiKeyProvider(directory);
        }

        if (environment == AtmEnvironment.Prod)
        {
            throw new PlatformNotSupportedException("Prod requires the DPAPI key provider (Windows).");
        }

        return new FileKeyProvider(directory);
    }
}
