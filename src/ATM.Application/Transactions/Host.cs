using ATM.Core.ValueObjects;

namespace ATM.Application.Transactions;

public sealed record HostAuthorizationRequest(
    Guid TransactionId,
    TerminalId Terminal,
    MaskedPan Card,
    decimal Amount,
    ReadOnlyMemory<byte> EncryptedPinBlock);

/// <summary>The host's answer. ResponseCode "00" means approved, following ISO 8583 practice; anything else is a decline reason.</summary>
public sealed record HostAuthorizationResponse(bool Approved, string ResponseCode, decimal? AvailableBalance, string? Message);

public sealed record HostStatementEntry(DateTimeOffset Date, string Description, decimal Amount);

public sealed record HostBalanceResponse(bool Approved, string ResponseCode, decimal? AvailableBalance);

public sealed record HostStatementResponse(bool Approved, string ResponseCode, IReadOnlyList<HostStatementEntry> Entries);

public sealed record HostPinChangeRequest(Guid TransactionId, TerminalId Terminal, MaskedPan Card, ReadOnlyMemory<byte> OldPinBlock, ReadOnlyMemory<byte> NewPinBlock);

public sealed record HostPinChangeResponse(bool Approved, string ResponseCode);

/// <summary>
/// The only way Application code reaches the authorization host. Phase 2 adapters (ISO 8583, NDC, DDC, REST) implement it.
/// Implementations throw on timeout or connectivity loss; that is a different thing from a decline.
/// </summary>
public interface IHostGateway
{
    Task<HostAuthorizationResponse> AuthorizeAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<HostBalanceResponse> InquireBalanceAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<HostStatementResponse> GetMiniStatementAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<HostPinChangeResponse> ChangePinAsync(HostPinChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tells the host to undo an earlier authorization, in full or for the part not dispensed.</summary>
    Task ReverseAsync(Guid transactionId, decimal amount, CancellationToken cancellationToken = default);
}

public sealed class TransactionOptions
{
    public decimal MinimumAmount { get; set; } = 10m;

    public decimal MaximumAmount { get; set; } = 500m;

    /// <summary>Withdrawals must be a whole multiple of this note denomination.</summary>
    public decimal Multiple { get; set; } = 10m;

    public int CardTimeoutSeconds { get; set; } = 30;

    public int HostTimeoutSeconds { get; set; } = 30;

    /// <summary>Number of lines on a mini statement.</summary>
    public int StatementLines { get; set; } = 5;

    public int PinMinLength { get; set; } = 4;

    public int PinMaxLength { get; set; } = 12;
}
