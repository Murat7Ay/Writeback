# Architecture decision records

Each record states the context, the decision, and the consequences, including what was given up. The status is
"Accepted" unless noted.

| # | Decision |
|---|---|
| [001](#adr-001-a-persistence-layer-for-dapper-not-an-orm) | A persistence layer for Dapper, not an ORM |
| [002](#adr-002-dataannotations-as-the-attribute-vocabulary) | DataAnnotations as the attribute vocabulary; fluent API for the rest |
| [003](#adr-003-mutate-the-entity-in-place) | Generated values are written into the entity; methods return `Task`/`bool`/`int` |
| [004](#adr-004-dialects-in-core-provider-packages-only-for-driver-apis) | Dialects in core; provider packages only for driver APIs |
| [005](#adr-005-statement-model-and-outcomes-instead-of-string-templates) | Statement model + outcomes instead of string templates |
| [006](#adr-006-batching-is-not-bulk) | Batching is not bulk; both exist, named honestly |
| [007](#adr-007-no-relationships) | No relationships |
| [008](#adr-008-static-configuration) | Static, replaceable configuration |
| [009](#adr-009-async-only-api-on-idbconnection) | Async-only API on `IDbConnection` |
| [010](#adr-010-no-source-generator-in-v1) | No source generator in v1 |
| [011](#adr-011-queries-are-sql-with-a-mapped-select-list) | Queries are SQL with a mapped select list, not LINQ |
| [012](#adr-012-optimistic-concurrency-semantics) | Optimistic concurrency semantics |
| [013](#adr-013-sql-server-trusts-only-rowcount) | SQL Server trusts only `@@ROWCOUNT` |
| [014](#adr-014-package-name) | Package name: Writeback |

---

## ADR-001: A persistence layer for Dapper, not an ORM

**Context.** The original project wanted Insert/Update/InsertList with output values, plus relationships as a
TODO. Dapper users are good at reads; what they repeat, and get wrong, is the write path. ORMs exist (EF Core,
linq2db, RepoDb), and imitating them badly is the most common failure of "Dapper extension" projects.

**Decision.** Own the write path: insert, update, delete, get-by-key, batches, generated values, optimistic
concurrency, and mapping that hand-written SQL can reuse. Leave querying to Dapper and SQL. **No change tracking,
identity map, unit of work, lazy loading, LINQ, relationships or migrations.** Every public method maps to one
statement, or one batch per chunk, that you can print.

**Consequences.** The library is small enough to audit and its behavior is predictable. Teams that want
graph persistence should use EF Core. The thesis has to stand on write-path correctness alone, and
[PRODUCT_THESIS.md](../PRODUCT_THESIS.md) argues that it does.

## ADR-002: DataAnnotations as the attribute vocabulary

**Context.** The legacy project defined `[DbKey]`, `[DbTableName(name, alias)]` and `[Ignore]`. Contrib, Dommel
and DapperExtensions each define their own `[Key]`/`[Table]`. That makes domain assemblies depend on a data-access
package, and annotations don't carry over between libraries.

**Decision.** Read `System.ComponentModel.DataAnnotations(.Schema)`, which ships with .NET: `[Key]`, `[Table]`,
`[Column]`, `[NotMapped]`, `[DatabaseGenerated]`, `[ConcurrencyCheck]`, `[Timestamp]`, `[Editable]`. Add a fluent
API for types you can't annotate, and for anything attributes can't express. Precedence is fluent > attributes >
conventions. The only custom attribute is `[HasTriggers]`, which is SQL Server-specific and has a fluent
equivalent.

**Consequences.** Domain models need no reference to this library, and the same entities work with EF Core.
`[Ignore]` becomes `[NotMapped]` (see the migration guide). The table alias from `[DbTableName]` is dropped,
because aliases belong to queries, not mappings (`EntitySql.ColumnsOf("c")`).

## ADR-003: Mutate the entity in place

**Context.** After `INSERT`, generated values must reach the caller. There are three options:

| Option | Pros | Cons |
|---|---|---|
| (a) Write them into the entity | what EF does; no allocation; `customer.Id` just works | needs setters (`init` is fine) |
| (b) Return a new entity | works for immutable types | callers must use the returned instance; partial objects; more allocation |
| (c) Return an "operation result" object | explicit | boilerplate at every call site for the 99% case |

**Decision.** (a). `InsertAsync` returns `Task`. `UpdateAsync` and `DeleteAsync` return `Task<bool>` ("row
matched"). The `*ManyAsync` methods return `Task<int>`. Mapping validation rejects generated columns without
setters, with a message saying so. **Batches stage values and apply them only after commit**, so a failed
batch never leaves entities half-updated.

**Consequences.** Get-only immutable types can be read but not have generated values written back. Positional
records work because their properties have `init` setters, which the integration tests verify.

## ADR-004: Dialects in core, provider packages only for driver APIs

**Context.** The legacy code was SQL Server-only (`SCOPE_IDENTITY`, `[col]`). The usual alternative is one
package per database. But SQL generation needs no driver, only knowledge of syntax.

**Decision.** All four dialects live in core and reference no ADO.NET provider. The dialect is detected from the
connection's exact type name, with explicit registration for anything else. `Writeback.SqlServer` and
`Writeback.PostgreSql` exist only because `SqlBulkCopy` and binary `COPY` need the driver's API.

**Consequences.** MySQL and SQLite users install one package. Adding a database means subclassing `SqlDialect`
(see [ARCHITECTURE §7](ARCHITECTURE.md#7-adding-a-database)). Detection by name was chosen over a reference to
each driver; matching exact full names (not substrings), plus unwrapping of profiling wrappers, avoids
Contrib's `GetType().Name.ToLower()` fragility.

## ADR-005: Statement model and outcomes instead of string templates

**Context.** Generated-value read-back differs fundamentally between databases. It can be part of the statement
(`OUTPUT`/`RETURNING`), a follow-up `SELECT` in the same batch, or a position-correlated `MERGE`. Each produces
results the executor must interpret differently. Format-string templates scatter that knowledge, and they make it
easy to splice in a name or value unquoted.

**Decision.** Statements are described as data (`InsertStatement`, `UpdateStatement`, `RowFilter`). A dialect
renders them through `SqlBuilder`, whose only primitives are keywords, quoted identifiers and parameter
placeholders. The result is a `CommandPlan` whose `StatementOutcome` (`AffectedRows`, `ScalarRowCount`,
`ReturnedRow`, `ResultSetPerRow` or `PositionedRows`) tells the executor how to read it.

**Consequences.** The executor has one code path per outcome, not per database. SQL is deterministic and pinned
by exact-text unit tests. It is not a general SQL AST, deliberately: only the statements this library emits are
modeled.

## ADR-006: Batching is not bulk

**Context.** The legacy `InsertList` was Dapper's `Execute(sql, list)`, which is one round trip per row and
returns no keys (Contrib and Dommel do the same). Calling that "bulk" is a lie. Real bulk protocols
(`SqlBulkCopy`, `COPY`) are far faster but cannot return generated values.

**Decision.** There are two honestly named operations:

- **`InsertManyAsync`** is batched SQL. It is chunked to parameter limits, runs in one transaction, and reads
  back generated values with exact correlation:
  - SQL Server uses `MERGE … OUTPUT` with a position column.
  - PostgreSQL and MySQL use one statement per row in one command, giving one result set per row.
  - SQLite re-executes the cached statement within the transaction.
  - When nothing needs reading back, it uses one multi-row `VALUES` statement per chunk.
- **`BulkCopyAsync`** (provider packages) uses the bulk protocol and does no read-back, as its documentation says.

`UpdateManyAsync` and `DeleteManyAsync` follow the same rules: per-row success for updates, a single
`IN (...)` delete per chunk.

**Consequences.** Users choose between keys and raw speed knowingly. Benchmarks report `InsertManyAsync` against
the right baseline. The order of multi-row `RETURNING` is never relied on.

## ADR-007: No relationships

**Context.** The legacy TODO: "Relations 1-1, 1-*, *-*".

**Decision.** Relationships are out of scope, permanently, for three reasons:

1. **Loading graphs is a query problem.** It needs joins or split queries, and choosing between them per use
   case is exactly the SQL control Dapper users want. Dapper's multi-mapping (`Query<Order, Customer, Order>`,
   `splitOn`) already does it, and [`docs/examples/RECIPES.md`](../examples/RECIPES.md) shows the patterns.
   `GetManyAsync(keys)` covers the batch-by-key part without N+1.
2. **Saving graphs requires knowing what changed.** Which children were added, removed or modified? Answering
   that needs snapshots (change tracking) and an identity map to avoid duplicates, which is the core of an ORM.
3. **A "lightweight" relationship feature** (say, cascade insert of children) would have to choose ordering,
   FK fix-up, and delete semantics on the user's behalf. That reintroduces the hidden behavior this library
   exists to avoid.

**Consequences.** Writing an aggregate is explicit: insert the parent, set the child FKs, `InsertManyAsync` the
children, all in one transaction. It is a few lines, and every one is visible.

## ADR-008: Static configuration

**Context.** Extension methods on `IDbConnection` need to find the mapping. The options are static state (as
Dapper's `SqlMapper` does), a context object (a `DbContext` by another name), or passing options to every call.

**Decision.** `WritebackConfig.Configure(o => …)` builds an immutable model and publishes it atomically.
Calling it again replaces the model: an earlier draft threw instead, which broke `WebApplicationFactory`-style
test hosts that run startup per test. Fluently configured entities are validated inside `Configure`.
Attribute-only entities need no configuration at all.

**Consequences.** There is one mapping per process (internally, `EntityModel` is instance-based if that ever has
to change). There is no DI package, because nothing needs injecting.

## ADR-009: Async-only API on IDbConnection

**Context.** Sync and async variants double the API and its tests. Modern ADO.NET providers implement true
async, and Dapper's async methods take `IDbConnection` but require `DbConnection` at runtime.

**Decision.** Async only, with `CancellationToken` everywhere and `IAsyncEnumerable` for streaming. The methods
extend `IDbConnection`, because that's what codebases inject, and fail fast with a clear `ArgumentException` if
it isn't a `DbConnection`. Transactions are `IDbTransaction?` parameters, as in Dapper.

**Consequences.** Sync-only codebases must use `.GetAwaiter().GetResult()` or stay on Dapper for writes. A sync
surface can be added later with real sync ADO calls, never sync-over-async.

## ADR-010: No source generator in v1

**Context.** Source generators would remove runtime reflection and help Native AOT.

**Decision.** Not yet. Reflection runs once per type to build the map. Steady-state calls touch only cached
plans and compiled accessors. Dapper itself emits IL at runtime, so a generator here would not make the library
AOT-compatible on its own. The package is honestly marked `IsAotCompatible=false`.

**Consequences.** Cold start for a type is a few milliseconds. The metadata model is a plain immutable object
graph, so a future generator (or a Dapper.AOT integration) can supply `EntityMap`s without changing the rest.

## ADR-011: Queries are SQL with a mapped select list

**Context.** "Should the library provide `db.Query<T>().Where(…)`?" A predicate translator is where micro-ORMs
start becoming ORMs: method calls, nullability, collations, `Contains`, and provider quirks all leak through.

**Decision.** There is no expression-to-SQL translation. Instead:

- `GetAsync`, `GetManyAsync` and `ExistsAsync` look up by key.
- `SelectAsync<T>(clause, param)` and `CountAsync<T>(clause, param)` prepend `SELECT <mapped, aliased
  columns> FROM <table>` to *your* SQL clause.
- `SelectUnbufferedAsync<T>` streams.
- `EntitySql<T>` gives quoted, mapping-aware fragments for fully hand-written SQL with plain Dapper.

**Consequences.** Users write WHERE clauses in SQL using column names, with
`db.Sql<T>().Column(x => x.Name)` when they want rename safety. The clause is raw SQL by design, and the
documentation says to parameterize it, exactly as with Dapper.

## ADR-012: Optimistic concurrency semantics

**Context.** No free Dapper extension offers it. Two token styles are common: a database-maintained rowversion
(SQL Server), and an application-maintained version number, which is portable.

**Decision.**

- **`[Timestamp]`** (or `[ConcurrencyCheck]` plus Computed) is a database-maintained token. It is compared in
  `WHERE` and read back after every write.
- **`[ConcurrencyCheck]` on an integer** is a version counter. Updates do `SET v = v + 1 … WHERE v = @v` and
  increment the entity after success. No triggers are needed, and it works on all four databases.
- Without a token, a missed update or delete returns `false` ("not found"). **With a token it throws
  `ConcurrencyConflictException`**, because silently returning `false` on a conflict is a lost update waiting
  to happen. The library cannot distinguish "deleted" from "modified" without an extra query, and does not
  try.
- `UpdateManyAsync` reports exactly which entities conflicted (it has a per-row signal) and rolls back.
  `DeleteManyAsync` can only report the whole chunk, since one `DELETE` statement returns only a count.
- `DeleteByKeyAsync` ignores tokens, since there is no entity to compare against.

**Consequences.** Clear semantics with one exception type. The version-counter type must be a non-nullable
integer, because `NULL + 1` would match forever. This is validated at mapping time.

## ADR-013: SQL Server trusts only `@@ROWCOUNT`

**Context.** SqlClient's affected-row count includes rows written by triggers, and is -1 when `SET NOCOUNT ON`
is active (some servers set it for every session through `user options`).

**Decision.** Every SQL Server update and delete without read-back appends `SELECT @@ROWCOUNT`. Statements with
read-back use `OUTPUT` (or `WHERE @@ROWCOUNT = 1` in the re-select). The provider count is never used on SQL
Server.

**Consequences.** There is one extra tiny result set per statement. Update and delete results are correct
regardless of triggers or session settings, which the integration test with a trigger writing two extra rows
per change proves.

## ADR-014: Package name

**Status:** Accepted (2026-10-01): **Writeback**.

**Context.** The project was called `DapperExtension`. `DapperExtensions` (plural) is an existing NuGet package
(Thad Smith et al., 1.7.0, 2021) with overlapping CRUD features. Publishing one letter away from it would
confuse search, dependency review and support. The candidates considered were `Writeback`, `DapperWrite`,
`Dapper.Persist` and `Scrivener`, all free on NuGet at the time. The `Dapper.` prefix isn't reserved for
third parties, but it reads as semi-official.

**Decision.** Rename the repository, namespaces, assemblies and packages to **Writeback**: `Writeback`,
`Writeback.SqlServer` and `Writeback.PostgreSql`. The name says what the library adds over Dapper:
database-generated values written back into your entities. It is short, brandable, and avoids implying an
affiliation with the Dapper project.

**Consequences.** The rename happened before the first release, so there are no users to migrate. The old
GitHub URL redirects to the renamed repository. The license is MIT, the same as Dapper.
