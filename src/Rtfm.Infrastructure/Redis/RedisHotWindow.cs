using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Rtfm.Core;
using Rtfm.Infrastructure.Options;
using Rtfm.Infrastructure.Resilience;
using StackExchange.Redis;

namespace Rtfm.Infrastructure.Redis;

/// <summary>
/// The Redis hot window (ADR-005): the newest N transactions by OccurredAt, held
/// in two keys and maintained by one Lua script.
/// </summary>
/// <remarks>
/// This type knows about Redis and nothing else. It does not know that SQL Server
/// exists, it does not decide when a rebuild is needed, and it never broadcasts.
/// <see cref="SqlRedisTransactionStore"/> composes it with the system of record.
/// </remarks>
public sealed class RedisHotWindow
{
    /// <summary>Sorted set. member = transaction id, score = OccurredAt epoch ms.</summary>
    public const string RecentKey = "tx:recent";

    /// <summary>Hash. field = transaction id, value = transaction JSON.</summary>
    public const string PayloadKey = "tx:payload";

    /// <summary>Rebuild mutex, so five replicas do not rebuild the same window five times.</summary>
    public const string RebuildLockKey = "cache:rebuild";

    private const string ScriptResourceName = "Rtfm.Infrastructure.Redis.hot-window.lua";

    private static readonly RedisKey[] WindowKeys = [RecentKey, PayloadKey];

    private readonly IRedisConnectionProvider _connections;
    private readonly TransientFaultPipelines _resilience;
    private readonly HotWindowOptions _options;
    private readonly ILogger<RedisHotWindow> _logger;
    private readonly string _scriptText;
    private readonly byte[] _scriptHash;

    public RedisHotWindow(
        IRedisConnectionProvider connections,
        TransientFaultPipelines resilience,
        IOptions<HotWindowOptions> options,
        ILogger<RedisHotWindow> logger)
    {
        _connections = connections;
        _resilience = resilience;
        _options = options.Value;
        _logger = logger;

        _scriptText = ReadScript();

        // EVALSHA sends 40 hex characters instead of the whole script on every
        // ingestion. The hash is SHA-1 of the script body, which is what Redis
        // itself keys its script cache on.
        _scriptHash = SHA1.HashData(Encoding.UTF8.GetBytes(_scriptText));
    }

    public int Size => _options.Size;

    /// <summary>How long a replica that lost the rebuild race waits before re-reading.</summary>
    public TimeSpan RebuildWait => _options.RebuildWait;

    /// <summary>How many times that replica re-reads before falling back to SQL Server.</summary>
    public int RebuildWaitAttempts => _options.RebuildWaitAttempts;

    /// <summary>
    /// Projects one transaction into the window and trims it, atomically.
    /// </summary>
    public async Task ProjectAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var payload = HotWindowPayload.From(transaction).ToJson();
        RedisValue[] arguments =
        [
            transaction.TransactionId.ToString(),
            transaction.OccurredAtEpochMs,
            payload,
            _options.Size,
        ];

        await _resilience.Redis.ExecuteAsync(
            async token =>
            {
                var db = await _connections.GetDatabaseAsync(token);
                await EvaluateAsync(db, arguments);
                ApplyTtl(db);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads the newest <paramref name="limit"/> transactions, newest first.
    /// Returns an empty list when the window is cold - deciding what to do about
    /// that is the store's job, not this type's.
    /// </summary>
    public async Task<IReadOnlyList<Transaction>> ReadAsync(int limit, CancellationToken cancellationToken) =>
        await _resilience.Redis.ExecuteAsync(
            async token =>
            {
                var db = await _connections.GetDatabaseAsync(token);
                return await ReadCoreAsync(db, limit);
            },
            cancellationToken);

    private async Task<IReadOnlyList<Transaction>> ReadCoreAsync(IDatabase db, int limit)
    {
        var ids = await db.SortedSetRangeByRankAsync(RecentKey, 0, limit - 1, Order.Descending);
        if (ids.Length == 0)
        {
            return [];
        }

        var payloads = await db.HashGetAsync(PayloadKey, ids);

        var results = new List<Transaction>(payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
        {
            var raw = payloads[i];
            if (raw.IsNullOrEmpty)
            {
                // The index and the hash are written atomically, but the two reads
                // above are not one unit: a concurrent trim can evict a payload in
                // between. Skipping is correct - the entry is leaving the window
                // anyway - and is strictly better than failing the whole snapshot.
                _logger.LogDebug("Hot window payload missing for {TransactionId}; skipped.", ids[i].ToString());
                continue;
            }

            var payload = HotWindowPayload.FromJson(raw!);
            if (payload is not null)
            {
                results.Add(payload.ToDomain());
            }
        }

        return results;
    }

    /// <summary>
    /// Tries to take the rebuild lock.
    /// </summary>
    /// <remarks>
    /// The lock is never released explicitly, only allowed to expire. Releasing it
    /// would need a token and a compare-and-delete script to avoid deleting a lock
    /// that has already expired and been retaken by another replica; letting it
    /// expire costs nothing, because once this replica has rebuilt, no other replica
    /// needs the lock - their reads will find a populated window.
    /// </remarks>
    public async Task<bool> TryAcquireRebuildLockAsync(CancellationToken cancellationToken) =>
        await _resilience.Redis.ExecuteAsync(
            async token =>
            {
                var db = await _connections.GetDatabaseAsync(token);
                return await db.StringSetAsync(
                    RebuildLockKey,
                    value: 1,
                    expiry: _options.RebuildLockDuration,
                    when: When.NotExists);
            },
            cancellationToken);

    /// <summary>
    /// Replaces the window contents with the given transactions, newest-first.
    /// </summary>
    public async Task WriteRebuildAsync(IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken)
    {
        if (transactions.Count == 0)
        {
            return;
        }

        var entries = new SortedSetEntry[transactions.Count];
        var fields = new HashEntry[transactions.Count];
        for (var i = 0; i < transactions.Count; i++)
        {
            var transaction = transactions[i];
            var id = transaction.TransactionId.ToString();
            entries[i] = new SortedSetEntry(id, transaction.OccurredAtEpochMs);
            fields[i] = new HashEntry(id, HotWindowPayload.From(transaction).ToJson());
        }

        await _resilience.Redis.ExecuteAsync(
            async token =>
            {
                var db = await _connections.GetDatabaseAsync(token);

                // A batch, not a transaction: these commands are pipelined in one
                // round trip but need no atomicity between them. A concurrent
                // ingestion interleaving here writes a newer value for one id,
                // and ZADD/HSET are last-writer-wins on that id only.
                var batch = db.CreateBatch();
                var tasks = new List<Task>
                {
                    batch.SortedSetAddAsync(RecentKey, entries),
                    batch.HashSetAsync(PayloadKey, fields),
                    batch.KeyExpireAsync(RecentKey, _options.Ttl),
                    batch.KeyExpireAsync(PayloadKey, _options.Ttl),
                };

                batch.Execute();
                await Task.WhenAll(tasks);
            },
            cancellationToken);
    }

    /// <summary>
    /// Invokes the maintenance script by hash, loading it on first use.
    /// </summary>
    /// <remarks>
    /// NOSCRIPT is expected, not exceptional: it happens on the first call after a
    /// Redis restart or a SCRIPT FLUSH, on every replica independently. EVAL both
    /// runs the script and loads it into the cache, so one fallback call recovers
    /// and every subsequent call is an EVALSHA again.
    /// </remarks>
    private async Task EvaluateAsync(IDatabase db, RedisValue[] arguments)
    {
        try
        {
            await db.ScriptEvaluateAsync(_scriptHash, WindowKeys, arguments);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            _logger.LogInformation("Hot-window script not in the Redis script cache; loading it.");
            await db.ScriptEvaluateAsync(_scriptText, WindowKeys, arguments);
        }
    }

    /// <summary>
    /// TTL is a safety net against abandoned keys, not a freshness mechanism, so it
    /// is fire-and-forget: the commands are pipelined behind the script call with no
    /// extra round trip, and losing the acknowledgement costs nothing.
    /// </summary>
    private void ApplyTtl(IDatabase db)
    {
        db.KeyExpire(RecentKey, _options.Ttl, CommandFlags.FireAndForget);
        db.KeyExpire(PayloadKey, _options.Ttl, CommandFlags.FireAndForget);
    }

    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException($"Embedded script '{ScriptResourceName}' is missing from the assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
