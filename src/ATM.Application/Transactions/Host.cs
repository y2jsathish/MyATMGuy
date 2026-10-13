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

/// <summary>
/// The only way Application code reaches the authorization host. Phase 2 adapters (ISO 8583, NDC, DDC, REST) implement it.
/// Implementations throw on timeout or connectivity loss; that is a different thing from a decline.
/// </summary>
public interface IHostGateway
{
    Task<HostAuthorizationResponse> AuthorizeAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default);

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

    public int PinMinLength { get; set; } = 4;

    public int PinMaxLength { get; set; } = 12;
}
