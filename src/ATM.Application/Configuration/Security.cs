using System.Security.Cryptography;
using System.Text;
using ATM.Core.Errors;

namespace ATM.Application.Configuration;

public enum AtmEnvironment
{
    Dev,
    Sit,
    Uat,
    Pilot,
    Prod,
}

public static class AtmEnvironments
{
    public const string VariableName = "ATM_ENVIRONMENT";

    public static AtmEnvironment FromEnvironment() =>
        Enum.TryParse<AtmEnvironment>(Environment.GetEnvironmentVariable(VariableName), ignoreCase: true, out var env) ? env : AtmEnvironment.Dev;

    public static bool AllowsDebugFeatures(this AtmEnvironment environment) => environment != AtmEnvironment.Prod;
}

public readonly record struct KeyMaterial(int Version, byte[] Key);

/// <summary>Source of encryption keys. DPAPI today; TPM or HSM later without touching callers.</summary>
public interface IKeyProvider
{
    int CurrentVersion { get; }

    KeyMaterial GetKey(int version);

    /// <summary>Creates a new key version and makes it current. Older versions stay readable.</summary>
    int Rotate();
}

/// <summary>
/// AES-256-GCM protection for config values and database columns. Output is <c>enc:v{keyVersion}:{base64(nonce|tag|ciphertext)}</c>,
/// so the key version travels with the ciphertext and rotation never strands old values.
/// </summary>
public sealed class SecretProtector(IKeyProvider keys)
{
    public const string Prefix = "enc:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix + "v", StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var material = keys.GetKey(keys.CurrentVersion);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(material.Key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return $"{Prefix}v{material.Version}:{Convert.ToBase64String(packed)}";
    }

    public string Unprotect(string protectedValue)
    {
        if (!IsProtected(protectedValue))
        {
            throw new AtmException(ErrorCatalogue.SecretUnavailable, "Value is not a protected value.");
        }

        try
        {
            var body = protectedValue[Prefix.Length..];
            var separator = body.IndexOf(':', StringComparison.Ordinal);
            var version = int.Parse(body.AsSpan(1, separator - 1), System.Globalization.CultureInfo.InvariantCulture);
            var packed = Convert.FromBase64String(body[(separator + 1)..]);
            var material = keys.GetKey(version);

            var plain = new byte[packed.Length - NonceSize - TagSize];
            using var aes = new AesGcm(material.Key, TagSize);
            aes.Decrypt(packed.AsSpan(0, NonceSize), packed.AsSpan(NonceSize + TagSize), packed.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or OverflowException or IOException)
        {
            throw new AtmException(ErrorCatalogue.SecretUnavailable, "A protected value could not be decrypted.", ex);
        }
    }
}
