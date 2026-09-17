# Implementation notes

Per §1 of the build prompt, the architectural decisions in §4 were implemented as
specified. This file records the places where the implementation adds to or
narrows a specified detail, with the reasoning, so a reviewer comparing the code
against §4 does not have to work out whether a difference was intentional.

None of these change an architectural decision. Where I disagreed with something,
it is noted as an objection and implemented as written anyway.

---

## 1. `201 Created` is returned only when a row was created

**Specified (§4.2):** `validate -> SQL Server UPSERT -> Redis window update ->
SignalR broadcast -> 201 Created`.

**Implemented:** the ordering is exactly as specified. The status code is `201
Created` for `IngestResult.Created`, and `200 OK` for `Updated` and `Ignored`. Every
response carries the outcome in its body:

```json
{ "result": "Created" | "Updated" | "Ignored", "transaction": { ... } }
```

**Reasoning.** I read the `201 Created` in §4.2 as the terminal step of the happy
path rather than as a statement about all three outcomes. Answering `201 Created` to
a late duplicate that created nothing is an assertion the system cannot support, and
producers' retry logic reads status codes: a client distinguishing "my retry was
accepted as new" from "my retry was a no-op" would be misled by a uniform `201`.
The `result` field makes the distinction explicit either way, so nothing is lost by
also making the status code honest.

If a reviewer wants a uniform `201`, it is one line in
`TransactionEndpoints.IngestAsync`.

---

## 2. The hot-window TTL is applied alongside the Lua script, not inside it

**Specified (§4.5):** the Lua script exactly as given, *and* "TTL of 24h on both
keys".

The script as specified contains no `EXPIRE`, so the two requirements do not meet.

**Implemented:** the script is byte-for-byte as specified. The TTL is applied by two
`PEXPIRE` commands issued immediately after the `EVALSHA` with
`CommandFlags.FireAndForget` (`RedisHotWindow.ApplyTtl`).

**Reasoning.** Adding the `EXPIRE` calls inside the script would have changed a
statement that §1 says is already decided, and the atomicity argument in §4.5 is
about the trim sequence, not about the TTL. Issuing them fire-and-forget pipelines
them behind the script call, so there is no extra round trip, and losing the
acknowledgement costs nothing: the TTL is a safety net against abandoned keys, not a
correctness property. The rebuild path sets the same TTL inside its batch.

---

## 3. Validation is stricter than §6 requires, in two places

**Specified (§6):** "negative or zero amount, unknown status, empty id, malformed
timestamp, unknown currency format".

**Implemented:** all of those, plus two additions.

- **Amount scale.** An amount with more than four decimal places is rejected rather
  than rounded to fit `DECIMAL(19,4)`. Silently truncating money is the class of bug
  that is found a year later during a reconciliation. An amount beyond 15 integral
  digits is rejected for the same reason — it would overflow the column.
- **`Guid.Empty`.** `00000000-0000-0000-0000-000000000000` parses as a GUID but means
  "unset". Accepting it would collapse every producer that forgot to assign an id
  onto a single row, and that row would then have a lifecycle.

Both are additive: nothing §6 requires to be accepted is rejected.

---

## 4. `status` is matched exactly, not parsed as an enum

**Specified (§3):** "`status` is exactly these three values."

**Implemented:** an explicit `switch` over the three strings rather than
`Enum.TryParse`.

**Reasoning.** `Enum.TryParse` also accepts the numeric forms — `"0"`, `"1"`, `"2"`
— and, with `ignoreCase`, any casing. Either would widen the wire contract beyond
"exactly these three values" by accident. There is a test for `"1"` specifically, so
this cannot regress unnoticed.

The consequence is that `"completed"` is a `400`. That is the stricter reading of
§3, and it is documented in the README's API contract.

---

## 5. The integration suite is excluded from `dotnet test` by MSBuild, not only by attribute

**Specified (§6):** the integration suite "must not run under plain `dotnet test`".

**Implemented:** two gates.

- The tests specific to real infrastructure use `[RequiresInfrastructureFact]`,
  which sets `Skip` unless `RTFM_INTEGRATION=1`.
- `Rtfm.IntegrationTests.csproj` clears `IsTestProject` unless the same variable is
  set, which removes it from `dotnet test` at the solution root entirely.

**Reasoning.** The attribute alone is not sufficient. The SQL/Redis run of the
shared contract suite inherits its `[Fact]` attributes from the base class in
`Rtfm.Tests`, so those cannot be skipped by an attribute on the derived class
without duplicating every test. The MSBuild gate covers them, and it still builds
with the solution, so the suite cannot silently rot.

---

## 6. `Currency` is validated for format only

The brief stores a currency and never uses it. `CHAR(3)` and three upper-case ASCII
letters is a format check, not membership of ISO 4217 — validating against the real
list needs a list, and a list needs an owner and an update process. Recorded in the
README under known limitations.

---

## 7. Objections

One, and it is minor.

**The `cache:rebuild` guard is specified as `SET cache:rebuild 1 NX PX 10000`**
(§4.5) — a constant value with no owner token. A replica that takes the lock,
exceeds 10 seconds, and then releases it would be deleting a lock another replica
now holds. Implemented as specified, and made safe by never releasing it: the key is
allowed to expire. That works because the rebuild is idempotent and because once the
winner has finished, nobody else needs the lock — their reads find a populated
window. Documented in ADR-006.

If the rebuild ever stops being idempotent, this needs a token and a
compare-and-delete release script.
