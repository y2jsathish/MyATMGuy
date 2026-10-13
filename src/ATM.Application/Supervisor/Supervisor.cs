using System.Security.Cryptography;
using System.Text;

namespace ATM.Application.Supervisor;

public sealed class SupervisorOptions
{
    public const string SectionName = "Supervisor";

    /// <summary>"pbkdf2:{iterations}:{saltBase64}:{hashBase64}", created with <see cref="PasscodeHasher.Hash"/>. Empty means supervisor mode is disabled.</summary>
    public string PasscodeHash { get; set; } = string.Empty;

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan Lockout { get; set; } = TimeSpan.FromMinutes(1);
}

public static class PasscodeHasher
{
    private const int Iterations = 120_000;

    public static string Hash(string passcode)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string passcode, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public enum SupervisorResult
{
    Granted,
    Denied,
    LockedOut,
    Disabled,
}

public interface ISupervisorSession
{
    bool IsActive { get; }

    SupervisorResult TryEnter(string passcode);

    void Exit();
}

/// <summary>Gate for supervisor-only screens. Three wrong passcodes lock it for a minute; every outcome is reported to the caller for auditing.</summary>
public sealed class SupervisorSession(SupervisorOptions options, TimeProvider clock) : ISupervisorSession
{
    private int failures;
    private DateTimeOffset lockedUntil;

    public bool IsActive { get; private set; }

    public SupervisorResult TryEnter(string passcode)
    {
        if (string.IsNullOrEmpty(options.PasscodeHash))
        {
            return SupervisorResult.Disabled;
        }

        if (clock.GetUtcNow() < lockedUntil)
        {
            return SupervisorResult.LockedOut;
        }

        if (PasscodeHasher.Verify(passcode ?? string.Empty, options.PasscodeHash))
        {
            failures = 0;
            IsActive = true;
            return SupervisorResult.Granted;
        }

        if (++failures >= options.MaxAttempts)
        {
            failures = 0;
            lockedUntil = clock.GetUtcNow() + options.Lockout;
            return SupervisorResult.LockedOut;
        }

        return SupervisorResult.Denied;
    }

    public void Exit() => IsActive = false;
}
