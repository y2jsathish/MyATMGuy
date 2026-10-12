using System.Text.RegularExpressions;

namespace ATM.Core.ValueObjects;

/// <summary>
/// A card number that is safe to log and store: at most the first six and last four digits are kept.
/// The full PAN is never held by this type.
/// </summary>
public sealed partial record MaskedPan
{
    private MaskedPan(string value) => Value = value;

    public string Value { get; }

    /// <summary>Masks a full card number. The input must pass the Luhn check.</summary>
    public static MaskedPan FromPan(string pan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pan);

        var digits = pan.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (digits.Length is < 12 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Card number must be 12 to 19 digits.", nameof(pan));
        }

        if (!PassesLuhn(digits))
        {
            throw new ArgumentException("Card number failed the Luhn check.", nameof(pan));
        }

        return new MaskedPan(string.Concat(digits.AsSpan(0, 6), new string('*', digits.Length - 10), digits.AsSpan(digits.Length - 4)));
    }

    /// <summary>Rehydrates a value that was masked earlier, for example when read back from storage.</summary>
    public static MaskedPan Parse(string masked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masked);
        if (!MaskedPattern().IsMatch(masked))
        {
            throw new FormatException("Value is not a masked card number.");
        }

        return new MaskedPan(masked);
    }

    public override string ToString() => Value;

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"^\d{6}\*{2,9}\d{4}$")]
    private static partial Regex MaskedPattern();
}
