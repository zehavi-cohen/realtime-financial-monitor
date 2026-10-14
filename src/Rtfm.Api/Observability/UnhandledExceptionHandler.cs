using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Rtfm.Api.Observability;

/// <summary>
/// The single exit for anything that was not handled.
/// </summary>
/// <remarks>
/// One handler, not try/catch at each call site: a per-endpoint catch drifts, and
/// the first endpoint that forgets one leaks a stack trace to a caller. Full detail
/// goes to the log; the response carries the correlation id and nothing else, so a
/// caller can quote an id to support without learning anything about our internals.
/// </remarks>
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UnhandledExceptionHandler> _logger;
    private readonly IProblemDetailsService _problemDetails;

    public UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger, IProblemDetailsService problemDetails)
    {
        _logger = logger;
        _problemDetails = problemDetails;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // A cancelled request is the client hanging up, not a fault. Logging it as
        // an error trains people to ignore errors.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug("Request {Path} was aborted by the client.", httpContext.Request.Path);
            return true;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(httpContext);

        var (statusCode, title) = exception switch
        {
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "The request body could not be read."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Unhandled exception handling {Method} {Path}. CorrelationId {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);
        }
        else
        {
            _logger.LogInformation(
                "Malformed request on {Method} {Path}: {Reason}. CorrelationId {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message,
                correlationId);
        }

        httpContext.Response.StatusCode = statusCode;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Instance = httpContext.Request.Path,
                Extensions = { ["correlationId"] = correlationId },
            },
        });
    }
}
