# External-maintainer review of v0.1

After the first vertical slice worked, the implementation was reviewed as if submitted by someone else, against
the checklist below. Benchmarks and the four real databases served as the review's instruments: several of the
findings came from measurements, not from reading the code.

**Checklist:** accidental ORM behavior, unnecessary abstractions, provider leakage, reflection bottlenecks, async
design, ergonomics, concurrency, SQL injection, identifier escaping, transactions, generated values, composite
keys, bulk claims, breaking API decisions, dependencies.

## Findings fixed

| # | Severity | Finding | How it was found | Fix |
|---|---|---|---|---|
| 1 | **Critical (perf)** | `InsertManyAsync` was **quadratic**: 1,000 rows took 1.35 s and allocated 462 MB. Dapper's `DynamicParameters` calls `Parameters.Contains(name)` before each add (O(n²), with culture-aware comparisons in SqlClient), and Microsoft.Data.Sqlite binds every parameter to every statement of a multi-statement command. | Benchmark: 10× rows took 100× the time | `BatchParameters` (an O(n) `IDynamicParameters` that still routes type-handler values through Dapper). Dialect capability `BatchesStatements`: SQLite executes **one prepared statement** per row in one transaction. Result: 1,000 rows linear, a few MB. |
| 2 | **High (correctness)** | A database-generated **Guid** key counted as an "identity", so MySQL and SQL Server trigger tables would re-select `WHERE id = LAST_INSERT_ID()`/`scope_identity()` and silently read the **wrong row**. | Writing the SQL-generation tests | `IsIdentity` now requires an integer key. Non-reselectable mappings fail at plan time and tell you to use `Guid.CreateVersion7()` in the application. |
| 3 | **High (correctness)** | SQL Server updates and deletes trusted the provider's affected-row count, which is **-1 under `SET NOCOUNT ON`** and includes trigger writes. Updates could report "not found", or throw false concurrency conflicts. | Review of trigger semantics; integration test with a trigger writing 2 extra rows | Every SQL Server update/delete selects `@@ROWCOUNT` ([ADR-013](DECISIONS.md#adr-013-sql-server-trusts-only-rowcount)). |
| 4 | **High (consistency)** | Batch parameters let the provider infer types, ignoring `SqlMapper.AddTypeMap` (e.g. `DateTime → DateTime2`), so `InsertManyAsync` could store values at a different precision than `InsertAsync`. | Review of the batch binder | DbTypes come from Dapper's own `SetDbType`, isolated in `DapperTyping` and pinned by a unit test (see the trade-off below). |
| 5 | Medium (correctness) | Read-back did no type conversion: SQLite returns timestamps as TEXT, and Dapper's per-column parser passes values through unconverted. | SQLite integration tests | `ValueConverter` (string → DateTime/Guid/DateOnly…, numeric widening, enums). Failures name the column and both types. |
| 6 | Medium (transactions) | Batches opened and closed a closed connection **per chunk**: pool churn, and under an ambient `TransactionScope` each reopen re-enlists (possible escalation). | Review | One open per operation; `GetManyAsync` too. |
| 7 | Medium (API) | `GetReadbackMethod` was `protected internal abstract`. **Outside this assembly an override must be declared `protected`**, so the documented custom-dialect example did not compile for real users. | Compiling the ARCHITECTURE.md example as a unit test | Now `protected abstract`; the example is a unit test. |
| 8 | Medium (DX) | `Configure` threw when called twice, which breaks `WebApplicationFactory` test hosts that run startup per test. | Review | It replaces atomically ([ADR-008](DECISIONS.md#adr-008-static-configuration)). |
| 9 | Low (DX) | Fluent mapping errors surfaced only on first use. | Review | Fluent-configured entities are validated inside `Configure`. |
| 10 | Low (async) | Streaming relied on Dapper's `QueryUnbufferedAsync` honoring `WithCancellation`, which isn't documented. | Review | Own `ReadAsync(ct)` loop over Dapper's row parser; the configured timeout now applies too. |
| 11 | Low (ergonomics) | `GetProperties()` returns derived members first, so base-class `Id` came last in column lists. | Unit test | Base-first ordering; a redeclared member keeps its base position. |
| 12 | Low (perf) | Composite-key lookups reflected `GetProperty` on every call; key lookups used `DynamicParameters`. | Review / bisecting benchmark | Cached accessors; `BatchParameters` for keys. |
| 13 | Low (text) | The conflict message read "affected no row for the row". | Sample output | Reworded. |

### Trade-off accepted in fix 4

`SqlMapper.SetDbType` is public but marked `[Obsolete("for internal use only")]`. The alternative, a private copy
of Dapper's type table, cannot see user `AddTypeMap` overrides, and would recreate the inconsistency. The call is
confined to `DapperTyping` behind a scoped suppression, and `BatchParameterTypingTests` pins the behavior, so a
Dapper change fails CI rather than silently changing stored precision.

## Reviewed and accepted (with reasons)

| Concern | Verdict |
|---|---|
| *Accidental ORM behavior* | Only two mutations exist, both intentional ([ADR-003](DECISIONS.md#adr-003-mutate-the-entity-in-place)): generated values written back, and version counters incremented after success. There is no tracking, caching of entities, lazy loading, cascading, or hidden queries. |
| *SQL injection* | All generated SQL consists of quoted identifiers (embedded quote characters doubled, NUL rejected) and parameters. Batch row positions are integer literals produced by the library. `SelectAsync`/`CountAsync` clauses are raw SQL by design, exactly like Dapper, and documented as such. |
| *Identifier escaping* | Exact-text tests include hostile names (`x]; DROP TABLE users; --`) and reserved words (`order`, `select`, `from`) on all four databases. |
| *Unnecessary abstractions* | The public surface is the extension methods, `EntitySql<T>`, the options/fluent builders, and the extension points for dialects. The statement records are public only so dialects can be written outside the repository. No repositories, no interfaces-for-mocking. |
| *Provider leakage* | Core references no ADO.NET provider. SQL Server error translation reads `Number` reflectively, on the exception path only. |
| *Dependencies* | Core depends only on Dapper. Provider packages add exactly one driver each. |
| *Breaking API decisions* | Names deliberately match Contrib/Dommel (`InsertAsync`, `GetAsync`, …) to ease migration. A project importing both namespaces gets ambiguity errors and must pick one. The NuGet name is still open ([ADR-014](DECISIONS.md#adr-014-package-name)). |
| *Partial update allocations* | The caller's lambda allocates an expression tree per call (that's C#), and parsing it costs microseconds. The statement is cached per column set. |
| *MySQL "found rows"* | Correct update results need `UseAffectedRows=false`, which is the default in both MySQL drivers. Documented. |
| *`DeleteManyAsync` conflicts* | One `DELETE` reports a count, not which rows failed, so the exception lists the whole chunk. Duplicate entities in one call can trigger a false conflict. Documented. |
| *`UpdateManyAsync` inside the caller's transaction* | On conflict, non-conflicting rows have been updated *inside the caller's transaction*. Entities are untouched, and the caller decides whether to roll back. Documented. |
| *`BulkCopyAsync` type handlers* | Bulk protocols bypass parameters, so Dapper type handlers can't apply, and PostgreSQL binary COPY needs CLR types that match the column types exactly. Documented on the methods. |
| *Plan caching vs. Dapper's query cache* | Remainder-size batch SQL uses `CommandFlags.NoCache` so Dapper's cache can't grow without bound. |
| *Cold start* | One reflection pass per entity type (a few ms). No source generator yet ([ADR-010](DECISIONS.md#adr-010-no-source-generator-in-v1)). |

## Measured overhead on top of Dapper

The first ShortRun suggested Writeback's single-row operations cost **2×** raw Dapper. That claim did not
survive investigation:

- Microbenchmarks put the library's own CPU work at about **40 ns** for plan lookup plus **0.2–0.45 µs** for
  read-back parsers, against tens of µs for the Dapper call itself.
- A bisecting benchmark (`GetBreakdownBenchmarks`) showed identical SQL text costs the same as hand-written
  SQL, and `DynamicParameters` was the only measurable addition, now removed.
- The remaining differences between separate BenchmarkDotNet processes were dominated by machine noise on the
  test laptop.

[`docs/BENCHMARKS.md`](../BENCHMARKS.md) reports the final numbers with that caveat.

## Open items (roadmap, not defects)

- `UpsertAsync` (`ON CONFLICT` / `MERGE` / `ON DUPLICATE KEY`), designed per database.
- A sync API, using real sync ADO.NET calls if there is demand.
- A source generator for `EntityMap`s, and a Dapper.AOT story.
- MariaDB as a built-in dialect, plus Oracle and DB2 via community dialects.
- CI workflow running the integration suite with service containers (`WRITEBACK_POSTGRES`, `WRITEBACK_SQLSERVER`, `WRITEBACK_MYSQL`).
