using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rtfm.Api.Endpoints;
using Rtfm.Api.Observability;
using Rtfm.Api.Realtime;
using Rtfm.Core;
using Rtfm.Infrastructure;
using Rtfm.Infrastructure.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}
else
{
    // Structured output in deployment: five replicas' logs end up in one collector,
    // and a human-formatted line is not queryable there.
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

// ---------------------------------------------------------------------------
// Storage, real-time transport and domain services
// ---------------------------------------------------------------------------
var storageMode = InfrastructureServiceCollectionExtensions.ReadStorageMode(builder.Configuration);

builder.Services.AddRtfmInfrastructure(builder.Configuration);

builder.Services.AddScoped<TransactionIngestionService>();
builder.Services.AddSingleton<IBroadcaster, SignalRBroadcaster>();

var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

if (storageMode is StorageMode.SqlRedis)
{
    // The backplane is what makes the replica count irrelevant (ADR-008): each
    // replica publishes to a Redis channel and forwards only to its own local
    // connections. In InMemory mode there is one replica by definition, so a
    // backplane would be a dependency with nothing to do.
    var redis = builder.Configuration.GetSection(RedisOptions.SectionName);
    var redisConnectionString = redis["ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is required when Storage:Mode is SqlRedis.");
    var channelPrefix = redis["ChannelPrefix"] ?? "rtfm";

    signalR.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal(channelPrefix);
        options.Configuration.AbortOnConnectFail = false;
    });
}

// ---------------------------------------------------------------------------
// HTTP surface
// ---------------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.AddOpenApi();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Required by SignalR, and the reason the origin list is explicit rather
        // than AllowAnyOrigin - the two cannot be combined.
        .AllowCredentials()));
}

// Drain in-flight requests instead of cutting them off. A rolling deployment of
// five replicas otherwise fails a handful of ingestions per rollout (S7).
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();

if (allowedOrigins.Length > 0)
{
    app.UseCors();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------
app.MapTransactionEndpoints();
app.MapHub<TransactionsHub>("/hub/transactions");

// Liveness: is the process alive and able to answer? Nothing about dependencies -
// restarting a replica because SQL Server is slow makes an outage worse.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});

// Readiness: can this replica actually serve? A replica that cannot reach SQL
// Server or Redis cannot serve a snapshot, so it should receive no traffic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(InfrastructureServiceCollectionExtensions.ReadinessTag),
});

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Rtfm.Api.Startup");

lifetime.ApplicationStarted.Register(() =>
    startupLogger.LogInformation("Rtfm API started. Storage mode: {StorageMode}.", storageMode));
lifetime.ApplicationStopping.Register(() =>
    startupLogger.LogInformation("Rtfm API stopping; draining in-flight requests."));
lifetime.ApplicationStopped.Register(() =>
    startupLogger.LogInformation("Rtfm API stopped."));

await app.RunAsync();

/// <summary>Exposed so the integration suite can host the application with WebApplicationFactory.</summary>
public partial class Program;
