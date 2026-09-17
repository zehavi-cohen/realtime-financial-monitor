using Dapper;
using Microsoft.Extensions.Logging;
using Polly;
using Rtfm.Core;
using Rtfm.Infrastructure.Redis;
using Rtfm.Infrastructure.Resilience;
using Rtfm.Infrastructure.Sql;

namespace Rtfm.Infrastructure;

/// <summary>
/// The deployed <see cref="ITransactionStore"/>: SQL Server as the system of
/// record, Redis as the hot window in front of it.
/// </summary>
/// <remarks>
/// The ordering in <see cref="UpsertAsync"/> is the decision in ADR-002 and is not
/// an implementation detail: the row is durable before the projection is written,
/// and the projection is written before the caller is allowed to broadcast.
/// </remarks>
public sealed class SqlRedisTransactionStore : ITransactionStore
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly RedisHotWindow _window;
    private readonly TransientFaultPipelines _resilience;
    private readonly ILogger<SqlRedisTransactionStore> _logger;

    public SqlRedisTransactionStore(
        ISqlConnectionFactory connectionFactory,
        RedisHotWindow window,
        TransientFaultPipelines resilience,
        ILogger<SqlRedisTransactionStore> logger)
    {
        _connectionFactory = connectionFactory;
        _window = window;
        _resilience = resilience;
        _logger = logger;
    }

    public async Task<IngestResult> UpsertAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var result = await PersistAsync(transaction, cancellationToken);

        if (result is IngestResult.Ignored)
        {
            // Nothing changed in the system of record, so nothing may change in the
            // projection. Re-projecting here would overwrite a Completed entry with
            // the Pending payload of a late duplicate.
            return result;
        }

        await ProjectAsync(transaction, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var window = await _window.ReadAsync(limit, cancellationToken);
        if (window.Count > 0)
        {
            return window;
        }

        // A cold window. Either this is a fresh deployment, or Redis was flushed or
        // restarted. Both are recoverable from the system of record (ADR-006).
        return await RebuildAsync(limit, cancellationToken);
    }

    /// <summary>
    /// The MERGE. Everything the system guarantees about identity and concurrency
    /// is decided here, by the database, in one statement - see ADR-003 and
    /// <see cref="TransactionSql.Upsert"/>.
    /// </summary>
    private async Task<IngestResult> PersistAsync(Transaction transaction, CancellationToken cancellationToken) =>
        await _resilience.Sql.ExecuteAsync(
            async token =>
            {
                await using var connection = await _connectionFactory.OpenAsync(token);

                var action = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                    TransactionSql.Upsert,
                    TransactionSql.ParametersFor(transaction),
                    commandTimeout: _connectionFactory.CommandTimeoutSeconds,
                    cancellationToken: token));

                return TransactionSql.ToIngestResult(action);
            },
            cancellationToken);

    /// <summary>
    /// Writes the projection. A Redis failure here degrades the hot window; it must
    /// not fail the request, because the transaction is already durable and telling
    /// the producer otherwise would invite a retry of something that succeeded.
    /// </summary>
    /// <remarks>
    /// The cost of swallowing this is a window that is briefly missing an entry it
    /// should have. It self-heals: a later trim never resurrects it, but a Redis
    /// restart empties the window and the next read rebuilds it from SQL Server.
    /// Recorded in the README under Known limitations.
    /// </remarks>
    private async Task ProjectAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await _window.ProjectAsync(transaction, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Hot window projection failed for {TransactionId}; the transaction is persisted and the window is degraded.",
                transaction.TransactionId);
        }
    }

    /// <summary>
    /// Rebuild-on-miss, guarded so that five replicas reading a cold window produce
    /// one rebuild rather than five (ADR-006).
    /// </summary>
    private async Task<IReadOnlyList<Transaction>> RebuildAsync(int limit, CancellationToken cancellationToken)
    {
        bool acquired;
        try
        {
            acquired = await _window.TryAcquireRebuildLockAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis is unreachable. The dashboard still has to open, so serve the
            // snapshot straight from the system of record and skip the projection.
            _logger.LogError(ex, "Could not reach Redis to take the rebuild lock; serving the snapshot from SQL Server.");
            return await ReadFromSystemOfRecordAsync(limit, cancellationToken);
        }

        if (!acquired)
        {
            return await WaitForAnotherReplicaAsync(limit, cancellationToken);
        }

        _logger.LogInformation("Hot window is cold; rebuilding from the system of record.");

        var transactions = await ReadFromSystemOfRecordAsync(_window.Size, cancellationToken);

        try
        {
            await _window.WriteRebuildAsync(transactions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Hot window rebuild could not be written to Redis; serving this snapshot from SQL Server.");
        }

        return Take(transactions, limit);
    }

    /// <summary>
    /// Lost the rebuild race. Wait briefly and re-read rather than issuing a second
    /// rebuild query: the winner is already doing the work.
    /// </summary>
    private async Task<IReadOnlyList<Transaction>> WaitForAnotherReplicaAsync(int limit, CancellationToken cancellationToken)
    {
        var attempts = _window.RebuildWaitAttempts;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(_window.RebuildWait, cancellationToken);

            var window = await _window.ReadAsync(limit, cancellationToken);
            if (window.Count > 0)
            {
                return window;
            }
        }

        // The winner is slow, or it had nothing to write. Either way the caller is
        // waiting on a dashboard snapshot, so answer from the system of record.
        _logger.LogWarning("Waited for another replica's rebuild without result; serving the snapshot from SQL Server.");
        return await ReadFromSystemOfRecordAsync(limit, cancellationToken);
    }

    private async Task<IReadOnlyList<Transaction>> ReadFromSystemOfRecordAsync(int limit, CancellationToken cancellationToken) =>
        await _resilience.Sql.ExecuteAsync<IReadOnlyList<Transaction>>(
            async token =>
            {
                await using var connection = await _connectionFactory.OpenAsync(token);

                var rows = await connection.QueryAsync<TransactionSql.TransactionRow>(new CommandDefinition(
                    TransactionSql.SelectRecent,
                    new { Limit = limit },
                    commandTimeout: _connectionFactory.CommandTimeoutSeconds,
                    cancellationToken: token));

                return rows.Select(row => row.ToDomain()).ToList();
            },
            cancellationToken);

    private static IReadOnlyList<Transaction> Take(IReadOnlyList<Transaction> transactions, int limit) =>
        transactions.Count <= limit ? transactions : transactions.Take(limit).ToList();
}
