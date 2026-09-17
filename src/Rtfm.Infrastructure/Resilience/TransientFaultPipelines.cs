using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Rtfm.Infrastructure.Options;
using StackExchange.Redis;

namespace Rtfm.Infrastructure.Resilience;

/// <summary>
/// Retry-with-backoff pipelines for the two external dependencies.
/// </summary>
/// <remarks>
/// Only transient faults are retried. Retrying a constraint violation or a bad
/// credential just turns a fast failure into a slow one, and retrying a
/// non-idempotent statement is worse than not retrying at all - which is safe here
/// precisely because both operations are idempotent: the MERGE is keyed on
/// TransactionId, and the Lua script is a ZADD/HSET pair.
/// </remarks>
public sealed class TransientFaultPipelines
{
    /// <summary>
    /// SQL Server error numbers that mean "try again", not "you are wrong".
    /// Connection-level failures, deadlock victim, lock timeout, throttling.
    /// </summary>
    private static readonly HashSet<int> TransientSqlErrors =
    [
        -2,     // Timeout expired
        20,     // Instance does not support encryption (transient during startup)
        64,     // Connection was successfully established but then failed
        233,    // No process on the other end of the pipe
        1205,   // Deadlock victim
        1222,   // Lock request timeout
        4060,   // Cannot open database (still coming up)
        10053,  // Transport-level error on send
        10054,  // Transport-level error on receive
        10060,  // Network-related or instance-specific error
        10928,  // Resource limit reached
        10929,  // Server is too busy
        40197,  // Service encountered an error processing the request
        40501,  // Service is busy
        40613,  // Database is not currently available
        49918,  // Cannot process request, not enough resources
        49919,  // Cannot process create or update request
        49920,  // Cannot process request, too many operations
    ];

    public TransientFaultPipelines(IOptions<ResilienceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;

        Sql = Build(settings, IsTransientSql);
        Redis = Build(settings, IsTransientRedis);
    }

    /// <summary>Retries transient SQL Server faults.</summary>
    public ResiliencePipeline Sql { get; }

    /// <summary>Retries transient Redis faults.</summary>
    public ResiliencePipeline Redis { get; }

    private static ResiliencePipeline Build(ResilienceOptions settings, Func<Exception, bool> shouldHandle) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(shouldHandle(args.Outcome.Exception!)),
                MaxRetryAttempts = settings.MaxRetryAttempts,
                Delay = settings.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,

                // Jitter matters with five replicas: without it, a shared dependency
                // hiccup produces five synchronised retry waves at the same instants.
                UseJitter = true,
            })
            .Build();

    public static bool IsTransientSql(Exception? exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientSqlErrors.Contains(e.Number)),
        TimeoutException => true,
        _ => false,
    };

    public static bool IsTransientRedis(Exception? exception) => exception switch
    {
        RedisConnectionException => true,
        RedisTimeoutException => true,

        // A server-side error is a bug in our script or a wrong key type. Retrying
        // it changes nothing, except NOSCRIPT, which the caller handles explicitly.
        _ => false,
    };
}
