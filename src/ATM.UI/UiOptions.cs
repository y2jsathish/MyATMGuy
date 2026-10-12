namespace ATM.UI;

public sealed class UiOptions
{
    public const string SectionName = "Ui";

    /// <summary>Full-screen, borderless, always on top. Off for development, on for production terminals.</summary>
    public bool Kiosk { get; set; }
}
