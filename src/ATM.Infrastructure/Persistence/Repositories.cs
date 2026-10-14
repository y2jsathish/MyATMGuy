using ATM.Application.Configuration;
using ATM.Application.Masking;
using ATM.Application.Persistence;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ATM.Infrastructure.Persistence;

internal static class PagingExtensions
{
    public static async Task<Page<T>> ToPageAsync<T>(this IQueryable<T> ordered, int pageNumber, int pageSize, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var total = await ordered.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new Page<T>(items, pageNumber, pageSize, total);
    }
}

internal sealed class TransactionRepository(AtmDbContext db) : ITransactionRepository
{
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        await db.Transactions.AddAsync(transaction, cancellationToken).ConfigureAwait(false);

    public Task<Transaction?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Transactions.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<Page<Transaction>> PageAsync(int pageNumber, int pageSize, TerminalId? terminal = null, CancellationToken cancellationToken = default)
    {
        var query = db.Transactions.AsQueryable();
        if (terminal is not null)
        {
            query = query.Where(t => t.Terminal == terminal);
        }

        return query.OrderByDescending(t => t.CreatedUtc).ToPageAsync(pageNumber, pageSize, cancellationToken);
    }
}

internal sealed class DeviceRepository(AtmDbContext db) : IDeviceRepository
{
    public async Task UpsertAsync(Device device, CancellationToken cancellationToken = default)
    {
        var existing = await db.Devices.FirstOrDefaultAsync(d => d.Terminal == device.Terminal && d.Kind == device.Kind, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await db.Devices.AddAsync(device, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(device with { Id = existing.Id });
        }
    }

    public async Task<IReadOnlyList<Device>> ListAsync(TerminalId terminal, CancellationToken cancellationToken = default) =>
        await db.Devices.Where(d => d.Terminal == terminal).OrderBy(d => d.Kind).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class ErrorRepository(AtmDbContext db) : IErrorRepository
{
    public async Task AddAsync(ErrorRecord error, CancellationToken cancellationToken = default) =>
        await db.Errors.AddAsync(error with { Message = SensitiveDataMasker.Mask(error.Message) }, cancellationToken).ConfigureAwait(false);

    public Task<Page<ErrorRecord>> PageAsync(int pageNumber, int pageSize, CorrelationId? correlationId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Errors.AsQueryable();
        if (correlationId is not null)
        {
            query = query.Where(e => e.CorrelationId == correlationId);
        }

        return query.OrderByDescending(e => e.TimestampUtc).ToPageAsync(pageNumber, pageSize, cancellationToken);
    }
}

internal sealed class ConfigRepository(AtmDbContext db, SecretProtector protector) : IConfigRepository
{
    public async Task SetAsync(string key, string value, bool isSecret, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var stored = isSecret ? protector.Protect(value) : value;
        var existing = await db.Configuration.FirstOrDefaultAsync(c => c.Key == key, cancellationToken).ConfigureAwait(false);
        var item = new ConfigItem(key, stored, isSecret, nowUtc);
        if (existing is null)
        {
            await db.Configuration.AddAsync(item, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(item);
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var item = await db.Configuration.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, cancellationToken).ConfigureAwait(false);
        return item is null ? null : item.IsSecret ? protector.Unprotect(item.Value) : item.Value;
    }
}

internal sealed class AuditRepository(AtmDbContext db) : IAuditRepository
{
    public Task<Page<AuditEntry>> PageAsync(int pageNumber, int pageSize, CorrelationId? correlationId = null, CancellationToken cancellationToken = default)
    {
        var query = db.AuditLogs.AsQueryable();
        if (correlationId is not null)
        {
            query = query.Where(a => a.CorrelationId == correlationId);
        }

        return query.OrderByDescending(a => EF.Property<long>(a, AtmDbContext.Sequence)).ToPageAsync(pageNumber, pageSize, cancellationToken);
    }
}

internal sealed class ReversalRepository(AtmDbContext db) : IReversalRepository
{
    public async Task AddAsync(ReversalRequest reversal, CancellationToken cancellationToken = default) =>
        await db.Reversals.AddAsync(reversal, cancellationToken).ConfigureAwait(false);

    public Task<ReversalRequest?> GetAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        db.Reversals.FirstOrDefaultAsync(r => r.Id == transactionId, cancellationToken);

    public async Task<IReadOnlyList<ReversalRequest>> DueAsync(DateTimeOffset nowUtc, int take, CancellationToken cancellationToken = default) =>
        await db.Reversals
            .Where(r => r.State == ReversalState.Pending && r.NextAttemptUtc <= nowUtc)
            .OrderBy(r => r.CreatedUtc)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ReversalRequest>> UnresolvedAsync(CancellationToken cancellationToken = default) =>
        await db.Reversals
            .Where(r => r.State != ReversalState.Delivered)
            .OrderBy(r => r.CreatedUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class EfUnitOfWork(AtmDbContext db, SecretProtector protector) : IUnitOfWork
{
    public ITransactionRepository Transactions { get; } = new TransactionRepository(db);

    public IDeviceRepository Devices { get; } = new DeviceRepository(db);

    public IErrorRepository Errors { get; } = new ErrorRepository(db);

    public IConfigRepository Config { get; } = new ConfigRepository(db, protector);

    public IAuditRepository Audit { get; } = new AuditRepository(db);

    public IReversalRepository Reversals { get; } = new ReversalRepository(db);

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new AtmException(ErrorCatalogue.ConcurrencyConflict, innerException: ex);
        }
    }

    public ValueTask DisposeAsync() => db.DisposeAsync();
}

public sealed class UnitOfWorkFactory(IDbContextFactory<AtmDbContext> contexts, SecretProtector protector) : IUnitOfWorkFactory
{
    public IUnitOfWork Create() => new EfUnitOfWork(contexts.CreateDbContext(), protector);
}
