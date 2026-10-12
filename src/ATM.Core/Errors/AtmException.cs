namespace ATM.Core.Errors;

/// <summary>An exception that carries a catalogued <see cref="ErrorCode"/>.</summary>
public class AtmException : Exception
{
    public AtmException(ErrorCode code, string? message = null, Exception? innerException = null)
        : base(message ?? code.Description, innerException)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}
