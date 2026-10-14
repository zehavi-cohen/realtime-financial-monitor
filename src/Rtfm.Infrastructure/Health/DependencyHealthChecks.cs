using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rtfm.Infrastructure.Redis;
using Rtfm.Infrastructure.Sql;

namespace Rtfm.Infrastructure.Health;

/// <summary>
/// Readiness for SQL Server. A replica that cannot reach the system of record
/// cannot serve an ingestion, so it must not receive traffic (S7).
/// </summary>
public sealed class SqlServerHealthCheck : IHealthCheck
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SqlServerHealthCheck(ISqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

            // SELECT 1 proves the socket. Reading the table proves the schema was
            // applied, which is the thing that actually breaks on a fresh deploy.
            await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT TOP (1) 1 FROM dbo.Transactions;",
                commandTimeout: _connectionFactory.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server is not reachable.", ex);
        }
    }
}

/// <summary>
/// Readiness for Redis. Redis carries both the hot window and the SignalR
/// backplane, so a replica that cannot reach it can neither serve a snapshot nor
/// deliver a broadcast - it has nothing useful to offer a dashboard.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionProvider _connections;

    public RedisHealthCheck(IRedisConnectionProvider connections) => _connections = connections;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = await _connections.GetDatabaseAsync(cancellationToken);
            await db.PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
