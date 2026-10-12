using System.Diagnostics.CodeAnalysis;

namespace ATM.Core.ValueObjects;

/// <summary>Identifies one ATM terminal in the estate.</summary>
public sealed record TerminalId
{
    public const int MaxLength = 32;

    private TerminalId(string value) => Value = value;

    public string Value { get; }

    public static TerminalId Create(string? value)
    {
        if (!TryCreate(value, out var result))
        {
            throw new ArgumentException(
                $"Terminal ID must be 1 to {MaxLength} letters, digits, '-' or '_'.", nameof(value));
        }

        return result;
    }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out TerminalId? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        result = new TerminalId(trimmed);
        return true;
    }

    public override string ToString() => Value;
}
