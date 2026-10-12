using System.Security.Cryptography;
using ATM.Application.Audit;

namespace ATM.Infrastructure.Audit;

/// <summary>
/// Interim audit key storage: base64 key from ATM_AUDIT_KEY, else a key file created next to the audit log.
/// Sprint 3 moves this behind IKeyProvider (DPAPI protected).
/// </summary>
public static class AuditKeyLoader
{
    public static AuditSigningKey LoadOrCreate(string keyFilePath)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ATM_AUDIT_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new AuditSigningKey(Convert.FromBase64String(fromEnvironment));
        }

        if (File.Exists(keyFilePath))
        {
            return new AuditSigningKey(Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim()));
        }

        var directory = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFilePath, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new AuditSigningKey(key);
    }
}
