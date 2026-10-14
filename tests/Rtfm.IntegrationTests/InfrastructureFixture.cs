using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rtfm.Core;
using Rtfm.Infrastructure;
using Rtfm.Infrastructure.Options;
using Rtfm.Infrastructure.Redis;
using Rtfm.Infrastructure.Resilience;
using Rtfm.Infrastructure.Sql;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace Rtfm.IntegrationTests;

/// <summary>
/// Real SQL Server and real Redis, in containers, shared by every test in the
/// collection.
/// </summary>
/// <remarks>
/// Shared rather than per-test because a SQL Server container takes tens of
/// seconds to start; isolation comes from clearing state between tests instead,
/// which is cheap and equally effective for this schema.
/// </remarks>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private MsSqlContainer? _sql;
    private RedisContainer? _redis;

    public string SqlConnectionString { get; private set; } = string.Empty;

    public string RedisConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Belt and braces: the attributes already skip every test when the suite is
        // not enabled, so this should never be reached with Docker absent. If a
        // future xUnit ever constructs fixtures for skipped tests, this keeps the
        // "no Docker required" promise intact.
        if (!Infrastructure.IsEnabled)
        {
            return;
        }

        _sql = new MsSqlBuilder().Build();
        _redis = new RedisBuilder().Build();

        await Task.WhenAll(_sql.StartAsync(), _redis.StartAsync());

        // Point at a named database rather than the container default, so the
        // schema initialiser's CREATE DATABASE path is exercised too.
        SqlConnectionString = _sql.GetConnectionString().Replace("Database=master", "Database=rtfm", StringComparison.Ordinal);
        RedisConnectionString = _redis.GetConnectionString();

        await CreateStoreContextAsync().InitialiseSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_sql is not null)
        {
            await _sql.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds the store and its collaborators by hand rather than through a
    /// container. The wiring under test is the store's, not the DI container's.
    /// </summary>
    public StoreContext CreateStoreContextAsync(int windowSize = 500)
    {
        var sqlOptions = Microsoft.Extensions.Options.Options.Create(new SqlServerOptions
        {
            ConnectionString = SqlConnectionString,
            CommandTimeoutSeconds = 30,
            StartupTimeout = TimeSpan.FromMinutes(2),
        });

        var redisOptions = Microsoft.Extensions.Options.Options.Create(new RedisOptions
        {
            ConnectionString = RedisConnectionString,
        });

        var windowOptions = Microsoft.Extensions.Options.Options.Create(new HotWindowOptions
        {
            Size = windowSize,
            Ttl = TimeSpan.FromHours(24),
            RebuildLockDuration = TimeSpan.FromSeconds(10),
            RebuildWait = TimeSpan.FromMilliseconds(100),
            RebuildWaitAttempts = 20,
        });

        var resilience = new TransientFaultPipelines(
            Microsoft.Extensions.Options.Options.Create(new ResilienceOptions()));

        var connectionFactory = new SqlConnectionFactory(sqlOptions);
        var redisConnections = new RedisConnectionProvider(redisOptions, NullLogger<RedisConnectionProvider>.Instance);
        var window = new RedisHotWindow(redisConnections, resilience, windowOptions, NullLogger<RedisHotWindow>.Instance);

        var store = new SqlRedisTransactionStore(
            connectionFactory,
            window,
            resilience,
            NullLogger<SqlRedisTransactionStore>.Instance);

        return new StoreContext(store, window, connectionFactory, redisConnections, sqlOptions);
    }

    /// <summary>Everything a test needs to reach past the store and inspect the real state.</summary>
    public sealed class StoreContext : IAsyncDisposable
    {
        private readonly IOptions<SqlServerOptions> _sqlOptions;

        internal StoreContext(
            SqlRedisTransactionStore store,
            RedisHotWindow window,
            SqlConnectionFactory connectionFactory,
            RedisConnectionProvider redisConnections,
            IOptions<SqlServerOptions> sqlOptions)
        {
            Store = store;
            Window = window;
            ConnectionFactory = connectionFactory;
            RedisConnections = redisConnections;
            _sqlOptions = sqlOptions;
        }

        public ITransactionStore Store { get; }

        public RedisHotWindow Window { get; }

        public SqlConnectionFactory ConnectionFactory { get; }

        public RedisConnectionProvider RedisConnections { get; }

        public async Task InitialiseSchemaAsync()
        {
            var initializer = new SqlSchemaInitializer(
                ConnectionFactory,
                _sqlOptions,
                NullLogger<SqlSchemaInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);
        }

        /// <summary>Empties both stores so each test starts from nothing.</summary>
        public async Task ResetAsync()
        {
            await using var connection = await ConnectionFactory.OpenAsync(CancellationToken.None);
            await connection.ExecuteAsync("DELETE FROM dbo.Transactions;");

            var multiplexer = await RedisConnections.GetAsync(CancellationToken.None);
            foreach (var endpoint in multiplexer.GetEndPoints())
            {
                await multiplexer.GetServer(endpoint).FlushDatabaseAsync();
            }
        }

        public async Task<IDatabase> GetRedisAsync() =>
            await RedisConnections.GetDatabaseAsync(CancellationToken.None);

        public ValueTask DisposeAsync() => RedisConnections.DisposeAsync();
    }
}

/// <summary>
/// One collection, so the containers start once for the whole suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "infrastructure";
}
