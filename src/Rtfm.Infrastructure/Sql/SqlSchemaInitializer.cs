using System.Diagnostics;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rtfm.Infrastructure.Options;

namespace Rtfm.Infrastructure.Sql;

/// <summary>
/// Applies <c>db/schema.sql</c> on startup, idempotently, before the first request
/// is served.
/// </summary>
/// <remarks>
/// Runs as an <see cref="IHostedService"/> rather than lazily on first use: five
/// replicas racing to create a table on first request is a worse failure mode than
/// a slow start, and a replica that cannot reach its database should fail its
/// readiness probe rather than accept traffic it cannot serve.
///
/// Every replica runs this. That is safe because the script is guarded and
/// CREATE DATABASE is wrapped so the loser of the race sees "already exists" and
/// carries on.
/// </remarks>
public sealed class SqlSchemaInitializer : IHostedService
{
    private const string SchemaResourceName = "Rtfm.Infrastructure.Sql.schema.sql";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlSchemaInitializer> _logger;

    public SqlSchemaInitializer(
        ISqlConnectionFactory connectionFactory,
        IOptions<SqlServerOptions> options,
        ILogger<SqlSchemaInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);

        await WaitForServerAsync(timeout.Token);
        await EnsureDatabaseAsync(timeout.Token);
        await ApplySchemaAsync(timeout.Token);

        _logger.LogInformation("Schema applied to database {Database}.", _connectionFactory.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Poll until the instance accepts connections. Compose healthchecks cover the
    /// common case; this covers the rest, including a SQL Server that accepts TCP
    /// before it finishes recovery.
    /// </summary>
    private async Task WaitForServerAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                await using var connection = await _connectionFactory.OpenMasterAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    _logger.LogWarning(
                        ex,
                        "SQL Server not reachable yet (attempt {Attempt}, {Elapsed:0}s elapsed). Retrying.",
                        attempt,
                        stopwatch.Elapsed.TotalSeconds);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenMasterAsync(cancellationToken);

        // CREATE DATABASE cannot be parameterised and cannot run inside IF on all
        // versions, so the name is quoted with QUOTENAME and executed dynamically.
        // The name comes from our own connection string, never from a request.
        const string sql =
            """
            IF DB_ID(@Database) IS NULL
            BEGIN
                DECLARE @create NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@Database);
                EXEC sp_executesql @create;
            END;
            """;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { Database = _connectionFactory.DatabaseName },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // Another replica won the race. Expected, not an error.
            _logger.LogDebug("Database {Database} already created by another replica.", _connectionFactory.DatabaseName);
        }
    }

    private async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = ReadSchemaScript();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            script,
            commandTimeout: _options.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }

    private static string ReadSchemaScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Embedded schema '{SchemaResourceName}' is missing from the assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
