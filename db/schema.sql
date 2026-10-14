-- Real-Time Financial Monitor -- system of record.
--
-- Applied idempotently on startup by SqlSchemaInitializer, so this file is both
-- the migration and the documentation. It must stay re-runnable: every statement
-- is guarded, and no statement is destructive.
--
-- No GO batch separators: this script is executed as a single command.

IF OBJECT_ID(N'dbo.Transactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Transactions
    (
        TransactionId  UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        Amount         DECIMAL(19,4)    NOT NULL,
        Currency       CHAR(3)          NOT NULL,
        Status         VARCHAR(16)      NOT NULL,
        OccurredAt     DATETIME2(3)     NOT NULL,
        IngestedAtUtc  DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

-- Covering index for the hot-window rebuild: the ORDER BY is served by the index
-- order and every projected column is in the INCLUDE list, so the rebuild never
-- touches the clustered index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_OccurredAt' AND object_id = OBJECT_ID(N'dbo.Transactions'))
BEGIN
    CREATE INDEX IX_Transactions_OccurredAt
        ON dbo.Transactions (OccurredAt DESC) INCLUDE (Amount, Currency, Status);
END;
