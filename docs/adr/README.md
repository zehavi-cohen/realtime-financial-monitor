# Architecture Decision Records

One record per decision in §4 of the build brief. Each states the context, the
decision, the alternatives that were considered and why each was rejected, and the
consequences — including the ones that cost something.

These are the answer to the brief's distributed-architecture question. They are
meant to be read as closely as the code.

| ADR | Decision |
|-----|----------|
| [ADR-001](#adr-001--replicas-hold-no-shared-state) | Replicas hold no shared state |
| [ADR-002](#adr-002--ingestion-order-validate-persist-project-broadcast) | Ingestion order: validate, persist, project, broadcast |
| [ADR-003](#adr-003--the-database-enforces-identity-and-the-lifecycle) | The database enforces identity and the lifecycle |
| [ADR-004](#adr-004--schema-and-the-two-timestamps) | Schema, and the two timestamps |
| [ADR-005](#adr-005--the-redis-hot-window-two-keys-one-lua-script) | The Redis hot window: two keys, one Lua script |
| [ADR-006](#adr-006--no-refresh-policy-rebuild-on-miss-and-a-ttl) | No refresh policy: rebuild on miss, and a TTL |
| [ADR-007](#adr-007--one-storage-abstraction-two-real-implementations) | One storage abstraction, two real implementations |
| [ADR-008](#adr-008--signalr-over-a-redis-backplane-subscribe-before-snapshot) | SignalR over a Redis backplane, subscribe before snapshot |

---

## ADR-001 — Replicas hold no shared state

**Status:** Accepted

### Context

The service runs as five replicas behind one address. Any state a replica keeps in
its own memory is invisible to the other four, so a client whose request lands on a
different replica sees a different system. There is no request affinity and there
should not be one.

### Decision

Application processes hold connection handles and per-request data only. All shared
state lives in SQL Server (durable) and Redis (hot window and real-time backplane).
A replica that dies takes nothing with it; a replica that starts serves correctly
from its first request.

### Alternatives considered

**A local in-memory cache per replica, warmed on start.** Rejected. Five caches
diverge the moment a write lands on one of them, and every mechanism for keeping
them together — broadcast invalidation, short TTLs, versioned reads — is a
distributed-cache-coherence problem taken on voluntarily in exchange for a latency
saving that Redis already provides.

**Sticky sessions so a client always reaches the same replica.** Rejected. It makes
correctness depend on the load balancer's configuration, unbalances load by
construction, and turns a single replica restart into a visible failure for the
clients pinned to it. It also solves nothing here: a dashboard needs every
transaction, not the ones that happened to arrive at one replica.

**One replica designated as the writer.** Rejected. It reintroduces a single point
of failure and a leader-election problem, to avoid a concurrency question that SQL
Server answers in one statement (ADR-003).

### Consequences

- Replica count is an operational choice, not an application concern. Scaling to
  ten changes no code.
- Every read of shared state is a network hop. The hot window exists to make the
  common one cheap.
- Redis and SQL Server are hard dependencies on the serving path. Readiness probes
  reflect that (S7), and the README records Redis as a single point of failure.

---

## ADR-002 — Ingestion order: validate, persist, project, broadcast

**Status:** Accepted

### Context

An ingestion has four effects: it must be rejected if malformed, recorded durably,
reflected in the hot window, and pushed to connected dashboards. The order they
happen in is a correctness decision, not a performance detail.

### Decision

```
validate -> SQL Server UPSERT -> Redis window update -> SignalR broadcast -> 201 Created
```

Synchronous, in this order. A transaction is never pushed to an agent's screen
unless it is already durable.

### Alternatives considered

**Broadcast first, persist asynchronously.** Rejected. It is faster and it is
wrong. An agent seeing a transaction that does not survive a restart is a
correctness failure in a financial monitoring context, not a latency trade-off: the
agent acts on what the screen says, and the screen would be asserting something the
system cannot stand behind.

**Persist, return 201, then project and broadcast in the background.** Rejected for
this scope. It shortens the request but introduces a window in which the system of
record and the projection disagree, with no mechanism to close it. Doing it properly
means a transactional outbox with a relay — which is the correct next step when
ingestion throughput becomes the constraint, and is named as deferred in the README.

**A queue between ingestion and projection.** Rejected at MVP scope for the same
reason: it is the right answer to a problem this system does not have yet, and it
adds a component with its own delivery semantics, ordering guarantees and failure
modes.

### Consequences

- Ingestion latency is the sum of a SQL round trip, a Redis round trip and a
  backplane publish. At this scope that is single-digit milliseconds.
- SQL Server is on the critical path of every write. If it is down, ingestion fails,
  which is the correct behaviour: there is nowhere durable to put the transaction.
- Redis and the broadcast are *not* allowed to fail the request once the row is
  durable. Both are wrapped so that a failure degrades real-time delivery and logs
  loudly, rather than telling a producer that a stored transaction was rejected —
  which would invite a retry of something that already worked.

---

## ADR-003 — The database enforces identity and the lifecycle

**Status:** Accepted

### Context

The same `transactionId` arrives more than once: producers retry, replay after
being offline, and report a status change for a transaction they already sent. Five
replicas process these concurrently, on thread pools, with no ordering guarantee
between them.

The question is what a repeated id means, and where the rule that governs it lives.

### Decision

A repeated `transactionId` is a **status change, not a duplicate to reject**: the
transaction is the entity, the message is an assertion about it. Ingestion is an
UPSERT keyed on `TransactionId`, with the legal transitions encoded in the
statement itself:

```sql
MERGE dbo.Transactions WITH (HOLDLOCK) AS target
USING (SELECT @TransactionId AS TransactionId) AS source
    ON target.TransactionId = source.TransactionId
WHEN MATCHED AND target.Status = 'Pending' AND @Status <> 'Pending' THEN
    UPDATE SET Status = @Status, Amount = @Amount, OccurredAt = @OccurredAt
WHEN NOT MATCHED THEN
    INSERT (TransactionId, Amount, Currency, Status, OccurredAt)
    VALUES (@TransactionId, @Amount, @Currency, @Status, @OccurredAt)
OUTPUT $action;
```

Only `Pending -> Completed` and `Pending -> Failed` are permitted. A terminal
transaction cannot regress; a late duplicate is a no-op. The store returns
`Created` / `Updated` / `Ignored`, and the caller broadcasts on the first two only.

`HOLDLOCK` is required. `MERGE` without it takes an update lock for the search and
releases it before the action, so two concurrent statements for the same key can
both see "not matched" and both attempt the `INSERT`. `HOLDLOCK` holds a range lock
for the duration of the statement, which serialises them.

No application-level lock appears anywhere on this path.

### Alternatives considered

**Read, decide in C#, then write.** Rejected. Between the read and the write,
another replica's write lands. The decision is made against state that is no longer
current, and the loser either overwrites a terminal status or throws a primary-key
violation. No isolation level fixes it without taking a lock for the whole
read-decide-write — which is what `MERGE ... WITH (HOLDLOCK)` already is, done by
the engine, in one round trip.

**A distributed lock in Redis per `transactionId`.** Rejected. It puts a second
system on the critical path of a guarantee that the first system already gives for
free; it needs lease renewal, fencing tokens and a correct release; and when Redis
is unavailable the choice is between blocking ingestion and running unprotected.

**`INSERT`, catch the duplicate-key violation, then `UPDATE`.** Rejected. It uses
exceptions for control flow on the expected path — status changes are routine, not
exceptional — and the `UPDATE` still needs the same guard, so nothing is saved.

**A rowversion / optimistic-concurrency retry loop.** Rejected. It converts
contention into retries, and the contention here is on exactly the ids that are
busiest. The pessimistic range lock is both simpler and cheaper for a single-row
statement.

### Consequences

- The lifecycle rule is enforced by the same lock that serialises writers, so it
  cannot be bypassed by any caller, including a future one.
- `TransactionLifecycle` in `Rtfm.Core` mirrors the rule for the in-memory store and
  for unit tests. It is a mirror, not a second source of truth; the contract suite
  runs against both implementations to keep them honest.
- `HOLDLOCK` serialises writers *for the same key*. Different keys take different
  range locks and do not contend.
- `OUTPUT $action` returns no row when the predicate rejected the transition. That
  absence is the `Ignored` signal, and the whole broadcast decision follows from it
  without a second query.

---

## ADR-004 — Schema, and the two timestamps

**Status:** Accepted

### Context

Money and time are the two things a financial system gets quietly wrong.

### Decision

```sql
CREATE TABLE dbo.Transactions (
    TransactionId  UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Amount         DECIMAL(19,4)    NOT NULL,
    Currency       CHAR(3)          NOT NULL,
    Status         VARCHAR(16)      NOT NULL,
    OccurredAt     DATETIME2(3)     NOT NULL,
    IngestedAtUtc  DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Transactions_OccurredAt
    ON dbo.Transactions (OccurredAt DESC) INCLUDE (Amount, Currency, Status);
```

`DECIMAL(19,4)` for money, never `FLOAT`. Two timestamps: `OccurredAt` is asserted
by the producer, `IngestedAtUtc` is observed by us.

Shipped as `db/schema.sql`, applied idempotently on startup.

### Alternatives considered

**`FLOAT` or `REAL` for the amount.** Rejected without qualification. Binary
floating point cannot represent 0.1, so sums drift, equality comparisons fail
unpredictably, and the drift is invisible until someone reconciles a ledger.

**One timestamp.** Rejected. The two diverge routinely — an offline producer
replaying yesterday's batch, clock drift between hosts, a retry carrying its
original timestamp — and both are needed. Ordering the dashboard by arrival time
would show a replayed batch as if it happened now; ordering an ingestion-rate
dashboard by `OccurredAt` would show nothing arriving during a replay. Recording one
and deriving the other is not possible.

**Migrations through a tool (EF Core migrations, DbUp, Flyway).** Rejected at this
scope: one table, one index, one idempotent script that a DBA can read without
running anything. The moment there is a second schema change with data movement, a
migration tool is the right answer.

**Storing `Status` as a `TINYINT`.** Rejected. Four bytes saved against a column
nobody can read in a query result without a lookup table. `VARCHAR(16)` also lets the
`MERGE` predicate be read directly.

### Consequences

- `Amount` is exact to four decimal places, and amounts with more precision are
  rejected at validation rather than silently rounded.
- Every replica applies the schema on start. That is safe because the script is
  guarded and `CREATE DATABASE` is wrapped so the loser of the race sees "already
  exists" and carries on.
- `IngestedAtUtc` is written but not currently exposed. It is recorded because it
  cannot be reconstructed later; a dashboard for ingestion lag is a straightforward
  addition once it is wanted.
- The covering index means the hot-window rebuild never touches the clustered index.

---

## ADR-005 — The Redis hot window: two keys, one Lua script

**Status:** Accepted

### Context

The dashboard must open already populated. Reading the newest few hundred rows from
SQL Server on every dashboard connection, from any of five replicas, is a query the
database should not have to serve.

### Decision

Two keys:

| Key | Type | Purpose |
|-----|------|---------|
| `tx:recent` | Sorted Set | member = id, score = `OccurredAt` epoch ms. Ordering and trimming. |
| `tx:payload` | Hash | field = id, value = transaction JSON. Lookup by id. |

Window size **500**, from configuration: the dashboard renders 200 rows, and the
remainder is headroom so client-side filtering has something to filter.

A Sorted Set is required for the index. `ZADD` on an existing member updates its
score in place, so a status change cannot produce a second entry, and ordering
follows the producer's timestamp rather than arrival order — which is not a reliable
proxy for it, even within a single replica handling requests on a thread pool.

Maintenance runs as a **single Lua script** invoked via `EVALSHA`
(`src/Rtfm.Infrastructure/Redis/hot-window.lua`).

### Alternatives considered

**A Redis List (`LPUSH` + `LTRIM`).** Rejected. A list has no notion of member
identity, so a status change appends a second entry rather than replacing the first,
and the dashboard would show the same transaction twice with two different statuses.
Ordering would also follow arrival, not `OccurredAt`.

**One key holding the whole window as a JSON document.** Rejected. Every ingestion
becomes read-modify-write of the entire window, which is both a large payload on
every write and a lost-update race unless it is wrapped in `WATCH`/`MULTI` with a
retry loop.

**`MULTI`/`EXEC` instead of a script.** Rejected, and this is the load-bearing part.
The maintenance sequence contains a read-then-act step: the ids to delete from the
payload hash are the *result* of the `ZRANGE`. `MULTI`/`EXEC` queues commands and
returns all replies at the end, so the result of the `ZRANGE` cannot be consumed
inside the transaction that issued it. Splitting it into separate round trips means
that across five replicas another replica's writes interleave between the `ZRANGE`
and the `HDEL`, leaving payloads orphaned from the index: a hash that grows without
bound, holding records the window can no longer reach. Redis executes a script
atomically, so every command in it observes the same snapshot.

**`EVAL` on every call instead of `EVALSHA`.** Rejected. It ships the whole script
body on every ingestion from every replica. `EVALSHA` sends 40 hex characters; the
`NOSCRIPT` reply after a Redis restart is handled by falling back to `EVAL` once,
which both runs the script and loads it back into the cache.

### Consequences

- The window is a write-through projection: it is updated on the same path that
  writes the system of record, so it cannot go stale (ADR-006).
- Eviction follows `OccurredAt`, so a transaction that arrives late but happened
  earlier is correctly not admitted over a newer one.
- The two keys are read with two commands (`ZREVRANGE` then `HMGET`), which is *not*
  one atomic unit. A concurrent trim can evict a payload between them; the read skips
  the missing entry, which is correct — it is leaving the window anyway.
- A 24-hour TTL is applied to both keys as a safety net against abandoned keys. It
  is not a freshness mechanism, and it is issued fire-and-forget so it costs no
  round trip.

---

## ADR-006 — No refresh policy: rebuild on miss, and a TTL

**Status:** Accepted

### Context

A cache needs an answer to "when does this become wrong". This one does not become
wrong — but it can become *absent*, and that needs an answer too.

### Decision

**Refresh policy: none.** The window is a write-through projection, updated on the
same path that writes the system of record, so there is nothing to invalidate and
nothing to periodically refresh. Two mechanisms cover the gaps:

- **Rebuild on miss.** An empty `tx:recent` triggers a rebuild from SQL Server in
  one query, guarded by `SET cache:rebuild 1 NX PX 10000` so five replicas do not
  rebuild simultaneously. Losers wait briefly and re-read.
- **TTL of 24h** on both keys, as a safety net against abandoned keys rather than
  for freshness.

### Alternatives considered

**A periodic background refresh.** Rejected. It re-reads data that is already
correct, on a timer, from five replicas, and it papers over projection bugs instead
of surfacing them — a projection that only works because something rewrites it every
minute is a projection nobody can reason about.

**A short TTL, treating the window as a conventional cache.** Rejected for the same
reason, plus the failure mode: every expiry is a thundering herd of dashboard
connections all missing at once.

**Letting each replica rebuild independently on miss.** Rejected. A Redis restart
makes all five miss at the same instant, and all five would run the same rebuild
query against SQL Server at the same moment — precisely when the system is already
recovering.

**A full distributed lock with a fencing token.** Rejected as more than this needs.
The rebuild is idempotent: two concurrent rebuilds write the same rows and the
result is identical. The guard is there to avoid wasted work, not to protect
correctness, so `SET NX PX` is exactly the right weight of tool.

### Consequences

- The lock is never released explicitly, only allowed to expire. Releasing it safely
  would need a token and a compare-and-delete script to avoid deleting a lock that
  had already expired and been retaken; letting it expire costs nothing, because
  once the winner has rebuilt, no other replica needs the lock — their reads find a
  populated window.
- A replica that loses the race and finds the window still empty after its waits
  falls back to reading from SQL Server directly. The dashboard opens; it does not
  hang waiting for another replica.
- Losing Redis entirely degrades to serving snapshots from SQL Server, at SQL
  Server's cost. That is a deliberate fallback, and it is why the readiness probe
  still fails: a degraded replica should not be the one taking new traffic if
  healthy ones exist.

---

## ADR-007 — One storage abstraction, two real implementations

**Status:** Accepted

### Context

The brief asks for RAM storage that is thread-safe and free of race conditions. A
system of record that only exists in RAM is not one — a restart loses every
transaction, which is the failure ADR-002 exists to prevent. Both requirements are
legitimate and they are not the same requirement.

### Decision

```csharp
public interface ITransactionStore
{
    Task<IngestResult> UpsertAsync(Transaction tx, CancellationToken ct);
    Task<IReadOnlyList<Transaction>> GetRecentAsync(int limit, CancellationToken ct);
}
```

| Implementation | Mechanism | Used by |
|---|---|---|
| `InMemoryTransactionStore` | `ConcurrentDictionary` + `ReaderWriterLockSlim`, bounded window | unit tests, `Storage=InMemory` |
| `SqlRedisTransactionStore` | SQL Server + the Redis window above | Docker Compose, deployment |

Selected by one configuration value. No other component knows which is active.

The in-memory implementation is a **first-class implementation, not a stub**. It
satisfies the brief's literal requirement, it is what the concurrency tests
exercise, and it is what makes `dotnet test` require no infrastructure. Both are
verified against the same contract test suite.

### Alternatives considered

**In-memory only.** Rejected: nothing survives a restart, and five replicas each
hold a different, private view of the world (ADR-001).

**SQL + Redis only, with the in-memory store as a test double.** Rejected. A test
double that is never run in anger drifts from the real thing, and every unit test
then needs Docker — which means the fast suite stops being run.

**SQLite as the in-memory option.** Rejected. It buys a SQL dialect that is not the
production one, so a statement that passes locally can still fail in deployment,
while giving up the thing that made an in-memory store attractive: no dependency at
all.

### Consequences

- `dotnet test` runs with no Docker, no Redis and no database, in seconds.
- The in-memory store's window is bounded, so it is also the brief's RAM store with
  an eviction policy rather than an unbounded leak.
- Anything the contract suite cannot express is, by definition, not part of the
  contract. Lua atomicity and rebuild-on-miss are therefore in the separate
  integration suite, which is the honest place for them.
- The two implementations must be kept in step by hand. The contract suite is the
  mechanism that makes a divergence fail a build rather than surprise someone in
  production.

---

## ADR-008 — SignalR over a Redis backplane, subscribe before snapshot

**Status:** Accepted

### Context

Five replicas, each holding a subset of the dashboard connections. A transaction
ingested by replica 3 must reach a dashboard connected to replica 1.

### Decision

`TransactionsHub` at `/hub/transactions`, backed by the Redis backplane:

```csharp
builder.Services.AddSignalR().AddStackExchangeRedis(redisConn, o =>
    o.Configuration.ChannelPrefix = RedisChannel.Literal("rtfm"));
```

Each replica publishes to a Redis channel; all replicas receive it and forward only
to their own local connections. No replica addresses another, which is what makes
the replica count irrelevant to the application.

**On connect, order matters:**

1. Add the connection to the broadcast group **first**.
2. *Then* read the window and send it to that caller as a snapshot.

### Alternatives considered

**Snapshot first, then subscribe.** Rejected. The two steps cannot be made one
atomic unit across five replicas and a backplane, so one has to overlap the other,
and the choice is between duplicates and gaps. A snapshot read before subscribing
loses everything broadcast in between, and loses it *silently*: the dashboard shows
a window that is simply missing rows, with nothing to indicate it. Subscribing first
can deliver the same transaction twice, and the client's state is a
`Map<string, Transaction>` keyed on `transactionId`, so a duplicate overwrites
itself and costs nothing. This ordering deliberately chooses duplicates over gaps.

**Synchronising the two streams server-side** — buffering broadcasts per connection
until its snapshot has been sent, or sequence numbers with client-side reordering.
Rejected. Every mechanism for it adds latency and per-connection state to all
traffic in order to serve one edge case that the client already neutralises for
free. It also reintroduces per-connection server state, against ADR-001.

**Server-Sent Events or raw WebSockets instead of SignalR.** Rejected. Both mean
writing the reconnect, backpressure and fan-out logic that SignalR already has, and
neither has an equivalent of the backplane, so cross-replica fan-out would have to be
built by hand on top of Redis pub/sub anyway.

**SignalR Groups partitioned by some key.** Deferred, not rejected. Every agent
currently watches the same stream, so one group is the correct shape. When agents
are scoped to a merchant or a region, the group key is where that goes.

### Consequences

- Replica count is irrelevant to correctness, and there are no sticky sessions
  (the client skips negotiation and connects straight over WebSockets). The cost is
  no transport fallback: WebSockets or nothing.
- Every message goes to every replica, whether or not it has a connection that wants
  it. At five replicas and one group, that is the right trade; it is the first thing
  to revisit at fifty.
- Delivery is at-most-once. A broadcast that fails is logged and dropped — the
  transaction is already durable, so the dashboard repairs its view on the next
  reconnect snapshot rather than the ingestion being failed.
- The typed hub interface (`ReceiveSnapshot(Transaction[])`,
  `ReceiveTransaction(Transaction)`) means the broadcaster and the hub cannot
  disagree about a method name.
