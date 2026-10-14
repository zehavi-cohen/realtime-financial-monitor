using Microsoft.AspNetCore.Mvc;
using Rtfm.Api.Contracts;
using Rtfm.Core;

namespace Rtfm.Api.Endpoints;

/// <summary>
/// The ingestion endpoint. It translates HTTP into a domain call and a domain
/// result into HTTP, and holds no rules of its own (S10).
/// </summary>
public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapPost("/", IngestAsync)
            .WithName("IngestTransaction")
            .WithSummary("Records a transaction, or applies a status change to one already recorded.")
            .Produces<IngestionResponse>(StatusCodes.Status201Created)
            .Produces<IngestionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        return app;
    }

    private static async Task<IResult> IngestAsync(
        CreateTransactionRequest request,
        TransactionIngestionService ingestion,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(TransactionEndpoints));

        var outcome = await ingestion.IngestAsync(request.ToSubmission(), cancellationToken);

        if (!outcome.IsAccepted)
        {
            logger.LogInformation(
                "Transaction rejected: {Fields}.",
                string.Join(", ", outcome.Failures.Select(f => f.Field)));

            return TypedResults.ValidationProblem(
                ToProblemDictionary(outcome.Failures),
                title: "The transaction was rejected.",
                detail: "One or more fields are invalid.");
        }

        var transaction = outcome.Transaction;

        // Ingestion outcome is a meaningful boundary and is logged once per request.
        // Per-message noise below this level is what makes a log unreadable at
        // several hundred transactions a second (S10).
        logger.LogInformation(
            "Transaction {TransactionId} ingested: {Result}, status {Status}.",
            transaction.TransactionId,
            outcome.Result,
            transaction.Status);

        var response = IngestionResponse.From(transaction, outcome.Result);

        // 201 only when a row was actually created. Updated and Ignored are 200:
        // answering 201 to a late duplicate that changed nothing would be a lie the
        // producer's own retry logic reads. See NOTES.md.
        return outcome.Result is IngestResult.Created
            ? TypedResults.Created($"/api/transactions/{transaction.TransactionId}", response)
            : TypedResults.Ok(response);
    }

    /// <summary>
    /// Groups failures by field, which is the shape
    /// <see cref="ValidationProblemDetails"/> expects and what a form-driven client
    /// needs to place errors next to inputs.
    /// </summary>
    private static Dictionary<string, string[]> ToProblemDictionary(IReadOnlyList<ValidationFailure> failures) =>
        failures
            .GroupBy(failure => failure.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.Message).ToArray(),
                StringComparer.Ordinal);
}
