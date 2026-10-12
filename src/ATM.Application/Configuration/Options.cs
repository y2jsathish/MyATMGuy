using System.ComponentModel.DataAnnotations;

namespace ATM.Application.Configuration;

/// <summary>Identity and behaviour of this terminal. Section name: Terminal.</summary>
public sealed class TerminalOptions
{
    public const string SectionName = "Terminal";

    [Required]
    [RegularExpression("^[A-Za-z0-9_-]{1,32}$", ErrorMessage = "TerminalId may contain letters, digits, '-' and '_' (max 32).")]
    public string TerminalId { get; set; } = "ATM-0001";

    [Range(10, 600)]
    public int IdleTimeoutSeconds { get; set; } = 60;

    public bool DebugFeatures { get; set; }
}
