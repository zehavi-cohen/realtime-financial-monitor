using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rtfm.Infrastructure.Options;

namespace Rtfm.Infrastructure.Sql;

/// <summary>Hands out connections. Exists so callers never see a connection string.</summary>
public interface ISqlConnectionFactory
{
    /// <summary>An open connection to the application database.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);

    /// <summary>An open connection to <c>master</c>, used only to create the database on first start.</summary>
    Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken);

    /// <summary>The application database name taken from the configured connection string.</summary>
    string DatabaseName { get; }

    int CommandTimeoutSeconds { get; }
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _masterConnectionString;

    public SqlConnectionFactory(IOptions<SqlServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _connectionString = settings.ConnectionString;
        CommandTimeoutSeconds = settings.CommandTimeoutSeconds;

        var builder = new SqlConnectionStringBuilder(_connectionString);
        DatabaseName = builder.InitialCatalog;

        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
    }

    public string DatabaseName { get; }

    public int CommandTimeoutSeconds { get; }

    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        OpenAsync(_connectionString, cancellationToken);

    public Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken) =>
        OpenAsync(_masterConnectionString, cancellationToken);

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
