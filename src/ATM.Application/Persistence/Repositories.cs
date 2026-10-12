using ATM.Core.Entities;
using ATM.Core.ValueObjects;

namespace ATM.Application.Persistence;

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>Persistence ports. No IQueryable crosses this boundary; every query is a named method.</summary>
public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);

    Task<Transaction?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Page<Transaction>> PageAsync(int pageNumber, int pageSize, TerminalId? terminal = null, CancellationToken cancellationToken = default);
}

public interface IDeviceRepository
{
    Task UpsertAsync(Device device, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Device>> ListAsync(TerminalId terminal, CancellationToken cancellationToken = default);
}

public interface IErrorRepository
{
    Task AddAsync(ErrorRecord error, CancellationToken cancellationToken = default);

    Task<Page<ErrorRecord>> PageAsync(int pageNumber, int pageSize, CorrelationId? correlationId = null, CancellationToken cancellationToken = default);
}

public interface IConfigRepository
{
    /// <summary>Stores a value. Secret values are encrypted before they reach the database.</summary>
    Task SetAsync(string key, string value, bool isSecret, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAuditRepository
{
    Task<Page<AuditEntry>> PageAsync(int pageNumber, int pageSize, CorrelationId? correlationId = null, CancellationToken cancellationToken = default);
}

public sealed record PurgeResult(int Transactions, int Errors, int AuditEntries);

/// <summary>One unit of work: repositories share a single database transaction that commits atomically.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    ITransactionRepository Transactions { get; }

    IDeviceRepository Devices { get; }

    IErrorRepository Errors { get; }

    IConfigRepository Config { get; }

    IAuditRepository Audit { get; }

    /// <summary>Saves all pending changes in one transaction. Concurrent modification raises a concurrency error.</summary>
    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWorkFactory
{
    IUnitOfWork Create();
}

public sealed class RetentionOptions
{
    public int TransactionDays { get; set; } = 90;

    public int ErrorDays { get; set; } = 30;

    public int AuditDays { get; set; } = 365;
}
