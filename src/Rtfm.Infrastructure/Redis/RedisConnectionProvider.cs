using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rtfm.Infrastructure.Options;
using StackExchange.Redis;

namespace Rtfm.Infrastructure.Redis;

/// <summary>Owns the single multiplexer for the process.</summary>
public interface IRedisConnectionProvider
{
    /// <summary>The shared multiplexer. Cheap to call; the connection is established once.</summary>
    Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken);

    /// <summary>The application database handle.</summary>
    Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One <see cref="ConnectionMultiplexer"/> per process, created lazily and shared.
/// </summary>
/// <remarks>
/// StackExchange.Redis multiplexes every command over a small number of sockets; a
/// multiplexer per operation is the single most common way to make a Redis client
/// slower than no cache at all. Creation is guarded by a semaphore rather than
/// Lazy&lt;Task&gt; so that a failed first attempt is not cached forever - Redis being
/// down at startup must not permanently poison the process (S10, resilience).
/// </remarks>
public sealed class RedisConnectionProvider : IRedisConnectionProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RedisOptions _options;
    private readonly ILogger<RedisConnectionProvider> _logger;

    private volatile IConnectionMultiplexer? _multiplexer;

    public RedisConnectionProvider(IOptions<RedisOptions> options, ILogger<RedisConnectionProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
    {
        var existing = _multiplexer;
        if (existing is { IsConnected: true })
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_multiplexer is not null)
            {
                return _multiplexer;
            }

            var configuration = ConfigurationOptions.Parse(_options.ConnectionString);

            // Let our own retry pipeline decide when to give up rather than blocking
            // a request thread inside the client's reconnect loop.
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 3;

            var created = await ConnectionMultiplexer.ConnectAsync(configuration);
            created.ConnectionFailed += (_, e) =>
                _logger.LogWarning("Redis connection failed: {FailureType} on {EndPoint}.", e.FailureType, e.EndPoint);
            created.ConnectionRestored += (_, e) =>
                _logger.LogInformation("Redis connection restored on {EndPoint}.", e.EndPoint);

            _multiplexer = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await GetAsync(cancellationToken);
        return multiplexer.GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        var multiplexer = _multiplexer;
        if (multiplexer is not null)
        {
            await multiplexer.CloseAsync();
            multiplexer.Dispose();
        }

        _gate.Dispose();
    }
}
