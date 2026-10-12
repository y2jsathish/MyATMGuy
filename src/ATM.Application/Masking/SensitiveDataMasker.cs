using System.Text.RegularExpressions;

namespace ATM.Application.Masking;

/// <summary>
/// Removes cardholder data and secrets from text before it reaches a log. There is deliberately no switch to turn this off.
/// PANs keep at most the first six and last four digits; PINs, CVVs, track data and secrets are replaced entirely.
/// </summary>
public static partial class SensitiveDataMasker
{
    public const string Redacted = "***";

    [GeneratedRegex(@"(?<![\d])(\d[ -]?){12,18}\d(?![\d])")]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"%B\d{12,19}\^[^?\r\n]*\??|;\d{12,19}=[^?\r\n]*\??")]
    private static partial Regex TrackPattern();

    [GeneratedRegex(
        @"(?<key>(?:""|')?\b(?:pin|pinblock|pin_block|cvv2?|cvc2?|cvn|password|passwd|pwd|secret|client_secret|token|access_token|api[-_]?key|apikey|authorization|connectionstring|private[-_]?key)\b(?:""|')?\s*[:=]\s*(?:""|')?)(?<value>[^\s""',;&{}]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    /// <summary>True when a property or key name denotes a secret (pin, cvv, password, token ...), so its value must be redacted whatever it contains.</summary>
    public static bool IsSensitiveName(string name) => Mask($"{name}=x") != $"{name}=x";

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = TrackPattern().Replace(text, "[TRACK]");
        result = KeyValuePattern().Replace(result, m => m.Groups["key"].Value + Redacted);
        result = BearerPattern().Replace(result, m => m.Groups["scheme"].Value + " " + Redacted);
        result = PanPattern().Replace(result, MaskPan);
        return result;
    }

    private static string MaskPan(Match match)
    {
        var digits = new string(match.Value.Where(char.IsDigit).ToArray());
        if (digits.Length is < 13 or > 19 || !PassesLuhn(digits))
        {
            return match.Value;
        }

        return string.Concat(digits.AsSpan(0, 6), new string('*', digits.Length - 10), digits.AsSpan(digits.Length - 4));
    }

    internal static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}
