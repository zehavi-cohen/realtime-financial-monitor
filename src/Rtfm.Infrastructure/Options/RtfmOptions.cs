using System.ComponentModel.DataAnnotations;

namespace Rtfm.Infrastructure.Options;

/// <summary>Which <see cref="Rtfm.Core.ITransactionStore"/> implementation is wired up.</summary>
public enum StorageMode
{
    /// <summary>Process memory. No infrastructure required.</summary>
    InMemory = 0,

    /// <summary>SQL Server as the system of record, Redis as the hot window.</summary>
    SqlRedis = 1,
}

/// <summary>
/// The one configuration value that selects a storage implementation (ADR-007).
/// No other component knows which one is active.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageMode Mode { get; set; } = StorageMode.InMemory;
}

/// <summary>
/// The hot window's shape. Size is derived from what the dashboard renders (200 rows)
/// plus headroom for client-side filtering - see ADR-005.
/// </summary>
public sealed class HotWindowOptions
{
    public const string SectionName = "HotWindow";

    [Range(1, 100_000)]
    public int Size { get; set; } = 500;

    /// <summary>
    /// Safety net against abandoned keys, not a freshness mechanism. The window is a
    /// write-through projection and cannot go stale, so there is nothing to invalidate.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long one replica may hold the rebuild lock before others may retry.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan RebuildLockDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a replica that lost the rebuild race waits before re-reading.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:05")]
    public TimeSpan RebuildWait { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How many times a loser re-reads before giving up and returning what it has.</summary>
    [Range(1, 20)]
    public int RebuildWaitAttempts { get; set; } = 5;
}

public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>How long startup waits for SQL Server to accept connections before failing the process.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Channel prefix for the SignalR backplane. Isolates this application's
    /// pub/sub traffic from anything else sharing the Redis instance.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ChannelPrefix { get; set; } = "rtfm";
}

/// <summary>Retry shape for transient SQL and Redis faults.</summary>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:10")]
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}
