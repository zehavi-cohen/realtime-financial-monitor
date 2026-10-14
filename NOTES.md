# Implementation notes

Places where the implementation adds to, narrows, or departs from the most
obvious reading of the requirements — each with the reasoning, so a reviewer
comparing the code against the spec does not have to work out whether a
difference was intentional.

The two larger deviations (SQL Server as the system of record, `Completed`
rather than `Success`) are in the README under
[Deliberate deviations](README.md#deliberate-deviations). The architectural
decisions themselves are in [`docs/adr/README.md`](docs/adr/README.md).

---

## 1. `201 Created` is returned only when a row was created

The ingestion path ends in `201 Created`. The status code here is `201` for
`IngestResult.Created`, and `200 OK` for `Updated` and `Ignored`. Every response
carries the outcome in its body:

```json
{ "result": "Created" | "Updated" | "Ignored", "transactionId": "...", "transaction": { ... } }
```

**Reasoning.** Answering `201 Created` to a late duplicate that created nothing
is an assertion the system cannot support, and producers' retry logic reads
status codes: a client distinguishing "my retry was accepted as new" from "my
retry was a no-op" would be misled by a uniform `201`. The `result` field makes
the distinction explicit either way, so nothing is lost by also making the status
code honest.

It is one line in `TransactionEndpoints.IngestAsync` if a uniform `201` is wanted.

## 2. An `Ignored` response omits the transaction body

`Ignored` returns `result` and `transactionId` but no `transaction`.

**Reasoning.** On `Created` and `Updated` the submitted values are exactly what
the `MERGE` wrote, so echoing them is accurate. On `Ignored` nothing was written,
and echoing the submission back under the name `transaction` would tell a
producer that a rejected status change had been stored. Reading the real stored
row instead would cost a query on the one path whose whole point is that it did
no work.

## 3. The hot-window TTL is applied alongside the Lua script, not inside it

The maintenance script is implemented exactly as specified and contains no
`EXPIRE`. The 24-hour TTL is applied by two `PEXPIRE` commands issued immediately
after the `EVALSHA`, with `CommandFlags.FireAndForget` (`RedisHotWindow.ApplyTtl`).

**Reasoning.** The atomicity argument for the script is about the trim sequence,
not about the TTL, so there was no reason to modify a statement whose exact shape
carries a concurrency guarantee. Fire-and-forget pipelines the two commands behind
the script call, so there is no extra round trip, and losing the acknowledgement
costs nothing: the TTL is a safety net against abandoned keys, not a correctness
property. The rebuild path sets the same TTL inside its batch.

## 4. Validation is stricter than the stated rules in two places

Required: negative or zero amount, unknown status, empty id, malformed timestamp,
unknown currency format. All implemented, plus:

- **Amount scale.** An amount with more than four decimal places is rejected
  rather than rounded to fit `DECIMAL(19,4)`. Silently truncating money is the
  class of bug found a year later during a reconciliation. An amount beyond 15
  integral digits is rejected for the same reason — it would overflow the column.
- **`Guid.Empty`.** `00000000-0000-0000-0000-000000000000` parses as a GUID but
  means "unset". Accepting it would collapse every producer that forgot to assign
  an id onto a single row, and that row would then have a lifecycle.

Both are additive: nothing that must be accepted is rejected.

## 5. `status` is matched exactly, not parsed as an enum

An explicit `switch` over the three strings rather than `Enum.TryParse`.

**Reasoning.** `Enum.TryParse` also accepts the numeric forms — `"0"`, `"1"`,
`"2"` — and, with `ignoreCase`, any casing. Either would widen the wire contract
beyond "exactly these three values" by accident. There is a test for `"1"`
specifically, so this cannot regress unnoticed.

The consequence is that `"completed"` is a `400`. That is documented in the
README's API contract.

## 6. The integration suite is excluded from `dotnet test` by MSBuild, not only by attribute

The suite must not run under plain `dotnet test`. Two gates:

- Tests specific to real infrastructure use `[RequiresInfrastructureFact]`, which
  sets `Skip` unless `RTFM_INTEGRATION=1`.
- `Rtfm.IntegrationTests.csproj` clears `IsTestProject` unless the same variable
  is set, which removes it from `dotnet test` at the solution root entirely.

**Reasoning.** The attribute alone is not sufficient. The SQL/Redis run of the
shared contract suite inherits its `[Fact]` attributes from the base class in
`Rtfm.Tests`, so those cannot be skipped by an attribute on the derived class
without duplicating every test. The MSBuild gate covers them, and the project
still builds with the solution, so the suite cannot silently rot.

## 7. The simulator has a third button: "Send lifecycle"

The simulator is specified as a form plus "Generate random" and "Send 100 rapid".
A third button sends a `Pending` transaction and then, a second and a half later,
the **same `transactionId`** with a terminal status.

**Reasoning.** Without it the status-change animation and the repeated-id rule -
both required behaviour - have no path to being seen from the UI at all: every
other control mints a fresh id, so the dashboard only ever renders arrivals. This
button is the one way to watch the server answer `Updated` rather than `Created`
and the row transition in place instead of a second row appearing. It is a
demonstration of required behaviour, not new behaviour.

## 8. The runtime image is `aspnet:9.0`, not `aspnet:9.0-alpine`

The Alpine variant would be meaningfully smaller and ships BusyBox `wget`, which
would remove the `apt-get install curl` layer that exists only so the Compose
healthcheck can call `/health/ready`. `InvariantGlobalization` is already enabled,
so the usual Alpine pitfall does not apply.

It was not taken because it could not be verified: there is no Docker on the
machine this was built on, and swapping the base OS of the one artifact that has
to work on the first `docker compose up` is not a change to ship untested. It is
a one-line change for anyone who can run the image once.

## 9. `Currency` is validated for format only

`CHAR(3)` and three upper-case ASCII letters is a format check, not membership of
ISO 4217 — validating against the real list needs a list, and a list needs an
owner and an update process. Recorded in the README under Known limitations.

---

## Objections

One, and it is minor.

**The rebuild guard is a constant value with no owner token** —
`SET cache:rebuild 1 NX PX 10000`. A replica that takes the lock, exceeds ten
seconds, and then releases it would be deleting a lock another replica now holds.

Implemented as specified, and made safe by never releasing it: the key is allowed
to expire. That works because the rebuild is idempotent, and because once the
winner has finished nobody else needs the lock — their reads find a populated
window. Documented in
[ADR-006](docs/adr/README.md#adr-006--no-refresh-policy-rebuild-on-miss-and-a-ttl).

If the rebuild ever stops being idempotent, this needs a token and a
compare-and-delete release script.
