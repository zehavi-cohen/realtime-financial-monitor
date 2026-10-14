namespace Rtfm.Api.Observability;

/// <summary>
/// Gives every request a correlation id, echoes it back, and puts it in the log
/// scope so that every entry produced while handling the request carries it.
/// </summary>
/// <remarks>
/// With five replicas behind one service address, "which replica handled this and
/// what else did it log" is otherwise unanswerable. An id supplied by the caller is
/// honoured so a trace can span the producer and this service.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Bounded so a caller cannot write an arbitrary amount of text into our logs.</summary>
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
        }))
        {
            await _next(context);
        }
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(HeaderName, out var value) && value is string id
            ? id
            : context.TraceIdentifier;

    private static string ResolveCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > MaxLength)
        {
            return context.TraceIdentifier;
        }

        // Control characters in a log field can forge log lines. Reject rather than
        // sanitise: a caller sending them is not sending a correlation id.
        foreach (var c in supplied)
        {
            if (char.IsControl(c))
            {
                return context.TraceIdentifier;
            }
        }

        return supplied;
    }
}
