using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Rtfm.Core;
using Rtfm.Infrastructure.Health;
using Rtfm.Infrastructure.InMemory;
using Rtfm.Infrastructure.Options;
using Rtfm.Infrastructure.Redis;
using Rtfm.Infrastructure.Resilience;
using Rtfm.Infrastructure.Sql;

namespace Rtfm.Infrastructure;

/// <summary>
/// The one place where <c>Storage</c> decides which <see cref="ITransactionStore"/>
/// exists. Nothing downstream of this method can tell which branch was taken.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Health check tag for the readiness probe (S7).</summary>
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddRtfmInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Options are bound and validated at startup, not on first use: a missing
        // connection string should fail the process while it is still starting,
        // where an orchestrator will notice, rather than the first request (S10).
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<HotWindowOptions>()
            .Bind(configuration.GetSection(HotWindowOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ResilienceOptions>()
            .Bind(configuration.GetSection(ResilienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<TransientFaultPipelines>();

        var mode = ReadStorageMode(configuration);

        return mode switch
        {
            StorageMode.InMemory => AddInMemoryStorage(services, configuration),
            StorageMode.SqlRedis => AddSqlRedisStorage(services, configuration),
            _ => throw new InvalidOperationException($"Unsupported storage mode '{mode}'."),
        };
    }

    /// <summary>
    /// The storage mode is read directly from configuration rather than resolved
    /// from the container, because it decides which registrations exist - it has to
    /// be known before the container is built.
    /// </summary>
    public static StorageMode ReadStorageMode(IConfiguration configuration)
    {
        var raw = configuration.GetSection(StorageOptions.SectionName)["Mode"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return StorageMode.InMemory;
        }

        return Enum.TryParse<StorageMode>(raw, ignoreCase: true, out var mode)
            ? mode
            : throw new InvalidOperationException(
                $"Storage:Mode is '{raw}'. Valid values are {nameof(StorageMode.InMemory)} and {nameof(StorageMode.SqlRedis)}.");
    }

    private static IServiceCollection AddInMemoryStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITransactionStore>(provider =>
        {
            var window = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HotWindowOptions>>();
            return new InMemoryTransactionStore(window.Value.Size);
        });

        // Nothing external to probe, so readiness is a function of the process being
        // up - which is exactly what the liveness probe already says. Registering a
        // trivially healthy check keeps /health/ready present and uniform in both modes.
        services.AddHealthChecks()
            .AddCheck("storage", () => HealthCheckResult.Healthy("In-memory storage."), tags: [ReadinessTag]);

        return services;
    }

    private static IServiceCollection AddSqlRedisStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
        services.AddSingleton<RedisHotWindow>();
        services.AddSingleton<ITransactionStore, SqlRedisTransactionStore>();

        services.AddHostedService<SqlSchemaInitializer>();

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>("sqlserver", tags: [ReadinessTag])
            .AddCheck<RedisHealthCheck>("redis", tags: [ReadinessTag]);

        return services;
    }
}
