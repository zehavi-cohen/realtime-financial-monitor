using System.Data;
using Dapper;
using Rtfm.Core;

namespace Rtfm.Infrastructure.Sql;

/// <summary>
/// Every SQL statement this application issues, in one file, so a reviewer can read
/// the entire data access surface without opening anything else.
/// </summary>
internal static class TransactionSql
{
    /// <summary>
    /// The concurrency guarantee of the whole system, in one statement (ADR-003).
    ///
    /// WITH (HOLDLOCK) is load-bearing, not decoration. MERGE without it takes only
    /// an update lock for the search and releases it before the action, so two
    /// concurrent MERGEs for the same key can both see "not matched" and both try
    /// to INSERT. HOLDLOCK holds a range lock for the duration of the statement,
    /// which serialises them: one inserts, the other then matches.
    ///
    /// The lifecycle rule lives in the WHEN MATCHED predicate rather than in C#,
    /// so it is enforced by the same lock that serialises the writers. A read-then-
    /// decide-then-write in application code could not be, at any isolation level
    /// short of taking the lock ourselves.
    ///
    /// OUTPUT $action reports what actually happened: INSERT, UPDATE, or no row at
    /// all when the predicate rejected the transition. That is the whole
    /// Created/Updated/Ignored decision, decided by the database, in one round trip.
    /// </summary>
    public const string Upsert =
        """
        MERGE dbo.Transactions WITH (HOLDLOCK) AS target
        USING (SELECT @TransactionId AS TransactionId) AS source
            ON target.TransactionId = source.TransactionId
        WHEN MATCHED AND target.Status = 'Pending' AND @Status <> 'Pending' THEN
            UPDATE SET Status = @Status, Amount = @Amount, OccurredAt = @OccurredAt
        WHEN NOT MATCHED THEN
            INSERT (TransactionId, Amount, Currency, Status, OccurredAt)
            VALUES (@TransactionId, @Amount, @Currency, @Status, @OccurredAt)
        OUTPUT $action;
        """;

    /// <summary>
    /// The hot-window rebuild, in one query (ADR-006). Served entirely by
    /// IX_Transactions_OccurredAt.
    /// </summary>
    public const string SelectRecent =
        """
        SELECT TOP (@Limit)
               TransactionId,
               Amount,
               Currency,
               Status,
               OccurredAt
        FROM dbo.Transactions WITH (READCOMMITTED)
        ORDER BY OccurredAt DESC;
        """;

    /// <summary>
    /// Dapper parameters with the column types stated explicitly.
    /// </summary>
    /// <remarks>
    /// Without DbString, Dapper sends strings as NVARCHAR. Comparing an NVARCHAR
    /// parameter against a CHAR/VARCHAR column forces an implicit conversion, which
    /// costs an index scan instead of a seek and will not show up until the table
    /// is large. Stating the type is a one-line fix for a problem that is otherwise
    /// found in production.
    /// </remarks>
    public static DynamicParameters ParametersFor(Transaction transaction)
    {
        var parameters = new DynamicParameters();
        parameters.Add("TransactionId", transaction.TransactionId, DbType.Guid);
        parameters.Add("Amount", transaction.Amount, DbType.Decimal, precision: 19, scale: 4);
        parameters.Add("Currency", new DbString { Value = transaction.Currency, IsAnsi = true, IsFixedLength = true, Length = 3 });
        parameters.Add("Status", new DbString { Value = transaction.Status.ToString(), IsAnsi = true, IsFixedLength = false, Length = 16 });
        parameters.Add("OccurredAt", transaction.OccurredAt.UtcDateTime, DbType.DateTime2);
        return parameters;
    }

    /// <summary>The row shape returned by <see cref="SelectRecent"/>.</summary>
    public sealed class TransactionRow
    {
        public Guid TransactionId { get; init; }

        public decimal Amount { get; init; }

        public string Currency { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTime OccurredAt { get; init; }

        public Transaction ToDomain() => new()
        {
            TransactionId = TransactionId,
            Amount = Amount,
            // CHAR(3) comes back space-padded when the value is shorter than the
            // column; it never is here, but trimming keeps a bad row from leaking.
            Currency = Currency.TrimEnd(),
            Status = Enum.Parse<TransactionStatus>(Status),
            OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)),
        };
    }

    /// <summary>Maps the value of OUTPUT $action onto the domain result.</summary>
    /// <param name="action">"INSERT", "UPDATE", or null when the MERGE matched nothing to do.</param>
    public static IngestResult ToIngestResult(string? action) => action switch
    {
        "INSERT" => IngestResult.Created,
        "UPDATE" => IngestResult.Updated,
        null => IngestResult.Ignored,
        _ => throw new InvalidOperationException($"Unexpected MERGE action '{action}'."),
    };
}
