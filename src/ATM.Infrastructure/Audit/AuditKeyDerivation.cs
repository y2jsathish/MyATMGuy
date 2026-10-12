using System.Security.Cryptography;
using System.Text;
using ATM.Application.Audit;
using ATM.Application.Configuration;

namespace ATM.Infrastructure.Audit;

/// <summary>
/// Derives the audit signing key from key version 1 so rotating the general encryption key never invalidates
/// the audit chain. Plain keys never leave the key provider.
/// </summary>
public static class AuditKeyDerivation
{
    public static AuditSigningKey Derive(IKeyProvider keys) =>
        new(HMACSHA256.HashData(keys.GetKey(1).Key, Encoding.UTF8.GetBytes("atm-audit-signing-v1")));
}
