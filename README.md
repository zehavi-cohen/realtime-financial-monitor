# Real-Time Financial Monitor

An external system POSTs financial transactions over HTTP. The service validates
them, persists them, and pushes them in real time to a live dashboard used by
support agents. The dashboard opens already populated, updates instantly, and stays
responsive under bursts.

The service is designed to run as **five replicas**. No replica holds state that
another replica's clients need.

- **Why each decision was made:** [`docs/adr/README.md`](docs/adr/README.md)
- **Where the implementation adds to or narrows the brief:** [`NOTES.md`](NOTES.md)

---

## Contents

- [Architecture at a glance](#architecture-at-a-glance)
- [Running it](#running-it)
- [Running the tests](#running-the-tests)
- [API contract](#api-contract)
- [Configuration](#configuration)
- [Project layout](#project-layout)
- [Deviations from the brief](#deviations-from-the-brief)
- [Known limitations](#known-limitations)

---

## Architecture at a glance

```
  producer                    5 x API replica                  agents
 ┌────────┐   POST      ┌───────────────────────────┐    ┌──────────────┐
 │  /add  │────────────▶│  validate                 │    │  /monitor    │
 └────────┘             │     │                     │    │              │
                        │     ▼                     │    │  snapshot +  │
                        │  SQL Server  UPSERT       │◀───│  live stream │
                        │     │        (MERGE)      │ WS └──────────────┘
                        │     ▼                     │            ▲
                        │  Redis  tx:recent         │            │
                        │         tx:payload        │            │
                        │     │                     │            │
                        │     ▼                     │            │
                        │  SignalR broadcast ───────┼──▶ Redis backplane
                        │     │                     │       (fan-out to
                        │     ▼                     │        all replicas)
                        │  201 Created              │
                        └───────────────────────────┘
```

Four things carry the design:

1. **Ordering.** Persist, then project, then broadcast — synchronously, in that
   order. A transaction never reaches an agent's screen unless it is already
   durable. ([ADR-002](docs/adr/README.md#adr-002--ingestion-order-validate-persist-project-broadcast))
2. **Identity.** A repeated `transactionId` is a status change, not a duplicate to
   reject. One `MERGE ... WITH (HOLDLOCK)` decides create / update / ignore and
   enforces the lifecycle, with no application-level lock anywhere.
   ([ADR-003](docs/adr/README.md#adr-003--the-database-enforces-identity-and-the-lifecycle))
3. **The hot window.** The newest 500 transactions live in Redis as a sorted set
   plus a hash, maintained by one Lua script because the trim contains a
   read-then-act step that `MULTI`/`EXEC` cannot express.
   ([ADR-005](docs/adr/README.md#adr-005--the-redis-hot-window-two-keys-one-lua-script))
4. **Connect order.** A dashboard joins the broadcast group *before* it is sent its
   snapshot. That trades a possible duplicate for a guaranteed absence of gaps, and
   the client's `Map` keyed on `transactionId` makes the duplicate free.
   ([ADR-008](docs/adr/README.md#adr-008--signalr-over-a-redis-backplane-subscribe-before-snapshot))

---

## Running it

### Option A — `dotnet run`, in-memory storage

No Docker, no database, no Redis. Everything works except durability across a
restart, and there is one replica by definition.

```bash
dotnet run --project src/Rtfm.Api
```

The API listens on `http://localhost:5080`.

In a second terminal:

```bash
cd client
npm install
npm run dev
```

Open <http://localhost:5173/monitor> for the dashboard and
<http://localhost:5173/add> for the simulator. The Vite dev server proxies both
`/api` and `/hub` (including the WebSocket upgrade), so the browser sees one origin
and there is no CORS to configure.

### Option B — `docker compose up`, the full topology

```bash
docker compose up --build
```

This starts `api`, `sqlserver` and `redis`. The API waits for both to report
healthy, creates the `rtfm` database if it does not exist, applies `db/schema.sql`
idempotently, and then serves on `http://localhost:5080`.

Run the client against it the same way:

```bash
cd client && npm install && npm run dev
```

### Option C — Kubernetes

```bash
kubectl apply -f k8s/configmap.yaml
kubectl create secret generic rtfm-secrets \
  --from-literal=sqlServerConnectionString='Server=...;Database=rtfm;...' \
  --from-literal=redisConnectionString='redis-master:6379'
kubectl apply -f k8s/deployment.yaml -f k8s/service.yaml
```

Five replicas, resource requests and limits, a liveness probe that asks only whether
the process is alive, and a readiness probe that checks SQL Server and Redis — so a
replica that cannot serve a snapshot receives no traffic.
`k8s/secret.example.yaml` shows the Secret's shape; it is an example and contains no
real credentials.

---

## Running the tests

### Unit and contract tests — no infrastructure

```bash
dotnet test
```

67 tests, a few seconds, no Docker, no Redis, no database. This is the suite that
has to stay green.

It covers validation, the transaction lifecycle, the broadcast rule, and the full
`ITransactionStore` contract — including several hundred parallel upserts of
distinct ids, several hundred parallel upserts of the *same* id, window bounding by
`OccurredAt`, and reads issued in a loop during sustained writes.

### Integration tests — Testcontainers, opt-in

```bash
# bash
RTFM_INTEGRATION=1 dotnet test tests/Rtfm.IntegrationTests

# PowerShell
$env:RTFM_INTEGRATION = "1"; dotnet test tests/Rtfm.IntegrationTests
```

Requires Docker. Covers what only real infrastructure can show: Lua script
atomicity under parallel clients, rebuild-on-miss after a Redis flush, the
distributed rebuild guard under concurrent readers, and the same
`ITransactionStore` contract suite run unchanged against SQL Server + Redis.

Without the environment variable this project is excluded from `dotnet test`
entirely — see [`NOTES.md` §5](NOTES.md).

---

## API contract

### `POST /api/transactions`

```json
{
  "transactionId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
  "amount": 1500.50,
  "currency": "USD",
  "status": "Pending",
  "timestamp": "2024-01-15T10:00:00Z"
}
```

| Field | Rule |
|---|---|
| `transactionId` | Required. A GUID, and not the empty GUID. |
| `amount` | Required. Greater than zero, at most 4 decimal places, at most 15 integral digits. |
| `currency` | Required. Exactly three upper-case ASCII letters. Format only — not checked against ISO 4217. |
| `status` | Required. Exactly one of `Pending`, `Completed`, `Failed`. Case-sensitive. |
| `timestamp` | Required. An ISO-8601 instant. Normalised to UTC, truncated to milliseconds. |

Responses:

| Status | When | Body |
|---|---|---|
| `201 Created` | A new transaction was recorded. | `{ "result": "Created", "transactionId": "...", "transaction": {...} }` |
| `200 OK` | An existing `Pending` transaction moved to a terminal state, or the message asserted nothing new. | `{ "result": "Updated" \| "Ignored", "transactionId": "..." }`. `transaction` is present for `Updated`; it is omitted for `Ignored`, because nothing was written. |
| `400 Bad Request` | Validation failed. | RFC 9457 problem details with `errors` keyed by field name. |
| `500 Internal Server Error` | Anything unhandled. | Problem details with a `correlationId` and no internal detail. |

`Pending -> Completed` and `Pending -> Failed` update the row. Everything else —
`Completed -> Pending`, `Completed -> Failed`, an identical re-send — is `Ignored`,
changes nothing, and is **not** broadcast.

Every response carries an `X-Correlation-Id` header, echoing the request's if it
sent one.

### `GET /health/live` and `GET /health/ready`

`live` is the process. `ready` probes SQL Server and Redis.

### Hub: `/hub/transactions`

WebSockets, negotiation skipped. Server-to-client:

| Method | Payload | When |
|---|---|---|
| `ReceiveSnapshot` | `Transaction[]` | Once, immediately after connecting. |
| `ReceiveTransaction` | `Transaction` | On every `Created` or `Updated` ingestion, from any replica. |

### OpenAPI

Served at `/openapi/v1.json` in Development.

---

## Configuration

Bound to strongly-typed options and **validated at startup** — a missing connection
string fails the process while it is starting, not on the first request.

| Key | Default | Meaning |
|---|---|---|
| `Storage:Mode` | `InMemory` | `InMemory` or `SqlRedis`. Selects the `ITransactionStore` implementation; nothing else in the system knows which is active. |
| `HotWindow:Size` | `500` | Transactions kept in the Redis window, and the snapshot size. |
| `HotWindow:Ttl` | `24:00:00` | Safety net against abandoned keys. |
| `HotWindow:RebuildLockDuration` | `00:00:10` | How long one replica may hold the rebuild guard. |
| `HotWindow:RebuildWait` / `RebuildWaitAttempts` | `150ms` / `5` | How a replica that lost the rebuild race waits before re-reading. |
| `SqlServer:ConnectionString` | — | Required when `Storage:Mode=SqlRedis`. |
| `SqlServer:CommandTimeoutSeconds` | `15` | |
| `SqlServer:StartupTimeout` | `00:02:00` | How long startup waits for SQL Server before failing the process. |
| `Redis:ConnectionString` | — | Required when `Storage:Mode=SqlRedis`. |
| `Redis:ChannelPrefix` | `rtfm` | SignalR backplane channel prefix. |
| `Resilience:MaxRetryAttempts` / `BaseDelay` | `3` / `200ms` | Exponential backoff with jitter on transient SQL and Redis faults. |
| `Cors:AllowedOrigins` | `[]` | Explicit origins. Empty disables CORS entirely. |

Environment variables use the standard `__` separator: `Storage__Mode=SqlRedis`.

---

## Project layout

```
/src
  Rtfm.Core/            domain model, validation, ITransactionStore, IBroadcaster
  Rtfm.Infrastructure/  SqlRedisTransactionStore, InMemoryTransactionStore, Lua script
  Rtfm.Api/             Program.cs, endpoints, TransactionsHub, DI wiring, health checks
/tests
  Rtfm.Tests/           unit and contract tests
  Rtfm.IntegrationTests/
/client                 React + TypeScript
/db/schema.sql
/k8s/
/docs/adr/README.md
Dockerfile, docker-compose.yml, README.md, NOTES.md
```

`Rtfm.Core` has **no package references at all** — not even
`Microsoft.Extensions.*`. The dependency direction (API and Infrastructure both
depend on Core, never the reverse) is visible from the project files alone.

Three comments in the code explain *why* rather than what, each pointing at its ADR:
the Lua script's atomicity requirement, the `MERGE` guard, and the
subscribe-before-snapshot order.

---

## Deviations from the brief

Two, both deliberate.

**1. SQL Server is the system of record, not RAM or SQLite.** A system of record
that only exists in RAM is not one: a restart loses every transaction, and five
replicas each hold a private, divergent view of the world. The brief's RAM
requirement is met in full by `InMemoryTransactionStore`, which is retained, tested
by the same contract suite as the SQL store, and runnable with `Storage=InMemory`.
([ADR-007](docs/adr/README.md#adr-007--one-storage-abstraction-two-real-implementations))

**2. `Completed`, not `Success`.** The brief's data model defines
`Pending | Completed | Failed`; its UI section mentions "Success". That is an
inconsistency in the brief. `Completed` is used throughout, and `"Success"` is
rejected with a `400` — there is a test asserting exactly that.

---

## Known limitations

Scoped out deliberately. Each is listed with the condition that would make it the
next thing to build.

**Redis is a single point of failure for both state and transport.** It carries the
hot window *and* the SignalR backplane. If it is down, real-time delivery stops and
snapshots fall back to querying SQL Server directly. Persisted transactions are
never lost and ingestion still succeeds, but the dashboard degrades. *Next step:*
Redis Sentinel or a managed clustered Redis; separating the backplane from the cache
if their availability requirements diverge.

**The backplane broadcasts every message to every replica.** At five replicas and
one group that is the right trade. *Next step:* at tens of replicas, or once agents
are scoped to a subset of traffic, partition with SignalR Groups keyed on that
scope.

**Broadcast is at-most-once.** A broadcast that fails is logged and dropped; the
transaction is already durable, and the dashboard repairs its view from the snapshot
on its next reconnect. *Next step:* if an agent must provably see every transaction,
that needs a durable per-client cursor, not a more reliable broadcast.

**A Redis outage can leave the window missing entries that SQL Server has.** The
projection failure is logged and does not fail the request. It self-heals only when
the window is next rebuilt. *Next step:* a transactional outbox with a relay, which
also removes Redis from the ingestion path.

**No authentication or authorization.** Every endpoint and the hub are open. *Next
step:* this is the first thing to add before any deployment that is not a private
network — JWT bearer on the API and the hub, with agent identity carried into the
group key.

**No rate limiting.** A producer can saturate the ingestion path. *Next step:* ASP.NET
Core's rate limiter, per producer identity, which needs the authentication above to
be meaningful.

**`Currency` is stored but never used.** It is validated for format, persisted, and
displayed. No conversion, no aggregation, no ISO 4217 membership check. *Next step:*
multi-currency totals need a rate source and a policy about which rate applies at
which instant — a bigger decision than it looks.

**No row virtualization.** The dashboard renders a bounded 200 rows, which is what
keeps it fast. *Next step:* virtualization once the window an agent can scroll
through is thousands rather than hundreds.

**No transport fallback.** The client skips negotiation and connects over WebSockets
only, which is what removes the need for sticky sessions. A network that blocks
WebSockets gets no dashboard. *Next step:* enabling negotiation, which then requires
sticky sessions at the ingress.

**Also deliberately absent:** event sourcing, CQRS, asynchronous ingestion queues, a
dedicated streaming layer such as Kafka, Helm charts, CI pipelines. Each is a
reasonable next step under load or organisational pressure this system does not
have yet.
