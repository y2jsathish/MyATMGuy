using System.Security.Cryptography;
using System.Text.Json;
using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Core.Entities;
using ATM.Core.Errors;

namespace ATM.Application.Updates;

public sealed record UpdateFile(string Path, string Sha256, long Length);

/// <summary>What an update package claims to contain. The signature covers the exact manifest bytes, so nothing is re-serialised before checking.</summary>
public sealed record UpdateManifest(string Product, string Version, string? MinimumCurrentVersion, DateTimeOffset IssuedUtc, IReadOnlyList<UpdateFile> Files);

public interface IUpdateSignatureVerifier
{
    bool Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature);
}

/// <summary>ECDSA P-256 with SHA-256, signature in the fixed r||s form. The terminal holds only the public key.</summary>
public sealed class EcdsaUpdateSignatureVerifier(ECDsa publicKey) : IUpdateSignatureVerifier
{
    public bool Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature)
    {
        try
        {
            return publicKey.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

public sealed record UpdateVerification(bool Accepted, IReadOnlyList<string> Errors, UpdateManifest? Manifest);

/// <summary>
/// Decides whether a staged update package may be installed (SEC-12: only signed updates are accepted). Checks the signature first, then the
/// product, that the version is newer than the running one, that the running version is high enough for this update, and that the staged
/// folder holds exactly the files in the manifest with the recorded sizes and SHA-256 hashes. Nothing is installed here.
/// Every verdict is audited; a rejection is also counted as a High error (ATM-6001).
/// </summary>
public sealed class UpdatePackageVerifier(IUpdateSignatureVerifier signatures, IAuditService audit, IErrorCounters counters, string product)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UpdateVerification> VerifyAsync(byte[] manifestBytes, byte[] signature, string packageDirectory, Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(currentVersion);

        var errors = new List<string>();
        UpdateManifest? manifest = null;

        if (!signatures.Verify(manifestBytes, signature))
        {
            errors.Add("The manifest signature is not valid.");
        }
        else
        {
            manifest = Parse(manifestBytes, errors);
            if (manifest is not null)
            {
                Check(manifest, packageDirectory, currentVersion, errors);
            }
        }

        var target = manifest is null ? "unreadable package" : $"{manifest.Product} {manifest.Version}";
        if (errors.Count == 0)
        {
            await audit.RecordAsync("system", "UpdateVerified", target, "accepted", AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new UpdateVerification(true, [], manifest);
        }

        counters.Record(ErrorCatalogue.UpdateRejected);
        await audit.RecordAsync("system", "UpdateRejected", target, errors[0], AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new UpdateVerification(false, errors, manifest);
    }

    private static UpdateManifest? Parse(byte[] bytes, List<string> errors)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Product) || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null || manifest.Files.Count == 0)
            {
                errors.Add("The manifest is incomplete.");
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            errors.Add("The manifest cannot be read.");
            return null;
        }
    }

    private void Check(UpdateManifest manifest, string directory, Version current, List<string> errors)
    {
        if (!string.Equals(manifest.Product, product, StringComparison.Ordinal))
        {
            errors.Add($"The package is for '{manifest.Product}', not '{product}'.");
        }

        if (!Version.TryParse(manifest.Version, out var next))
        {
            errors.Add($"'{manifest.Version}' is not a valid version.");
        }
        else if (next <= current)
        {
            errors.Add($"Version {next} is not newer than the running version {current}.");
        }

        if (manifest.MinimumCurrentVersion is not null)
        {
            if (!Version.TryParse(manifest.MinimumCurrentVersion, out var minimum))
            {
                errors.Add($"'{manifest.MinimumCurrentVersion}' is not a valid minimum version.");
            }
            else if (current < minimum)
            {
                errors.Add($"This update needs at least version {minimum}; the terminal runs {current}.");
            }
        }

        var root = System.IO.Path.GetFullPath(directory);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !listed.Add(file.Path.Replace('\\', '/')))
            {
                errors.Add($"The file list has an empty or repeated path ('{file.Path}').");
                continue;
            }

            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file.Path));
            if (System.IO.Path.IsPathRooted(file.Path) || !full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{file.Path}' points outside the package folder.");
                continue;
            }

            if (!File.Exists(full))
            {
                errors.Add($"'{file.Path}' is missing from the package.");
                continue;
            }

            using var stream = File.OpenRead(full);
            if (stream.Length != file.Length)
            {
                errors.Add($"'{file.Path}' has {stream.Length} bytes; the manifest says {file.Length}.");
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{file.Path}' does not match its recorded hash.");
            }
        }

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!listed.Contains(relative))
                {
                    errors.Add($"'{relative}' is in the package folder but not in the manifest.");
                }
            }
        }
        else
        {
            errors.Add("The package folder does not exist.");
        }
    }
}
