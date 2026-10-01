# Ecosystem research (October 2026)

What already exists, what each library actually does on the write path, and what the four target databases
offer for returning generated values. Every claim is tagged with how it was established:

- **[src]**: read in the library's source code.
- **[nuget]**: NuGet registration data, queried on 2026-10-01.
- **[test]**: observed in this repository's integration tests against a real database.
- **[docs]**: vendor documentation or well-established behavior, not re-verified here.

## 1. The libraries

| Library | Latest (date) [nuget] | Built on Dapper | What it is |
|---|---|---|---|
| Dapper | 2.1.89 (2026-09-23) | n/a | Query/execute + object materialization. Nothing else. |
| Dapper.AOT | 1.1.0 (2026-09-11) | yes | Build-time interceptors that generate Dapper's binders for Native AOT. |
| Dapper.Contrib | 2.0.78 (**2020-11-18**) | yes | `Get/GetAll/Insert/Update/Delete` with `[Key]`, `[ExplicitKey]`, `[Computed]`, `[Write(false)]`. |
| Dommel | 3.5.3 (2026-06-09) | yes | CRUD plus LINQ predicates (`Select(p => ...)`), paging, multi-mapping joins. |
| DapperExtensions | 1.7.0 (2021-07-01) | yes | CRUD plus a predicate object model. Its name is the plural of this project's former name, which is why this project was renamed. |
| RepoDb | 1.16.0 | **no**, own materializer | "Hybrid ORM": fluent CRUD, batch and bulk operations, caching, tracing, 16 databases. |
| Z.Dapper.Plus | 9.3.5 | yes | **Commercial.** Real bulk insert/update/merge/delete with identity output. |
| EF Core | 10.0.x | no | Full ORM. |
| linq2db | n/a | no | Full LINQ-to-SQL with first-class bulk copy. |

### Dapper.Contrib [src]

- `Insert(IEnumerable<T>)` builds one `INSERT` and calls `connection.Execute(sql, entities)`. Dapper runs that
  **once per element**: one round trip per row, and it returns only a row count, no keys.
- `Insert(T)` on SQL Server runs `insert ...; select SCOPE_IDENTITY() id` and casts the result to `int`. Other
  database-generated columns (defaults, computed values, rowversion) are never read back.
- The SQL adapter is chosen from `connection.GetType().Name.ToLower()`. A wrapped connection (e.g. MiniProfiler)
  silently falls back to the default adapter.
- There is no optimistic concurrency: `Update` checks a change-tracking proxy's dirty flag, nothing else.
- Table names are pluralized by appending "s" unless `[Table]` says otherwise.
- No release since 2020.

### Dommel [src] and [docs]

- `InsertAll` calls `connection.Execute(sql, entities)`: one round trip per row, and no keys come back.
- `Insert` returns the identity as `object` via `ExecuteScalar`. It has no read-back of other generated columns.
- Its strength is querying: LINQ expressions are translated to SQL (`SelectAsync<Product>(p => p.Name == "x")`).
  That is a small expression-to-SQL translator with all the edge cases translators have.
- There is no optimistic concurrency.

### RepoDb [docs]

- It covers more than this project wants to: batch and bulk operations, field caching, tracing, property handlers,
  and 16 databases.
- It is **not Dapper**: it has its own materializer and its own type-handler model, so existing Dapper type
  handlers and habits do not transfer.
- Its README states plainly that a single maintainer runs it, with no security team or SLA.
- **Takeaway:** the space between Dapper and EF *is* valuable, since RepoDb proves the demand. But its answer
  is a whole second ecosystem.

### Dapper Plus [docs]

- It is the reference for real bulk operations on top of Dapper, including identity output from bulk insert.
- It is commercial and closed-source. Teams that cannot buy a license have no Dapper-native equivalent.

### EF Core 10 [docs]

EF Core is a strong default, and it has solved most of the problems this project addresses:

- **Triggers.** Since EF Core 7, SQL Server writes use `OUTPUT`. Tables with triggers need `HasTrigger(...)`;
  EF then falls back to `@@ROWCOUNT` and re-selects.
- **Batch key correlation.** Batched inserts on SQL Server use `MERGE ... OUTPUT` with a position column to
  correlate generated keys, because the order of multi-row `OUTPUT` is not guaranteed.
- **Set-based writes without tracking.** `ExecuteUpdate`/`ExecuteDelete` (EF Core 7+) and `SqlQuery<T>` for
  unmapped types narrow the gap with Dapper.
- **Its cost is in this repository's benchmarks.** EF still means a `DbContext`, a model, change tracking (or
  opting out of it), and LINQ translation. In-process, its per-operation overhead is roughly an order of
  magnitude above Dapper's (see [`docs/BENCHMARKS.md`](../BENCHMARKS.md)). Teams that chose Dapper did so for
  SQL control and predictability, and they are not coming back for `ExecuteUpdate`.

## 2. What the databases offer

| | SQL Server | PostgreSQL | MySQL 8 | SQLite |
|---|---|---|---|---|
| Return generated values from INSERT | `OUTPUT INSERTED.*` | `RETURNING` | none (MariaDB 10.5+ has `RETURNING`) | `RETURNING` (3.35+, 2021) |
| From UPDATE | `OUTPUT INSERTED.*` | `RETURNING` | none | `RETURNING` |
| Identity of this scope | `scope_identity()` | (use `RETURNING`) | `LAST_INSERT_ID()` | `last_insert_rowid()` |
| Row count of the last statement | `@@ROWCOUNT` | none in SQL (use `RETURNING`) | `ROW_COUNT()` | `changes()` |
| Max parameters per command | 2,100 | 65,535 per statement | 65,535 placeholders | 32,766 (3.32+) |
| Multi-row `VALUES` | yes, ≤ 1,000 rows | yes | yes | yes |
| Native bulk load | `SqlBulkCopy` | binary `COPY` | `LOAD DATA` / `MySqlBulkCopy` (needs `AllowLoadLocalInfile`) | none (prepared statements in one transaction) |

### Sharp edges that decided the design

1. **SQL Server rejects `OUTPUT` without `INTO` on tables with enabled triggers** (error 334) [docs][test]. The
   legacy version of this project hit this exact bug: its last two commits remove `OUTPUT inserted.*` "because it
   causes trigger errors". `OUTPUT` also reports values from *before* `AFTER` triggers ran [docs]. The trigger-safe
   pattern is `INSERT ...; SELECT ... WHERE @@ROWCOUNT = 1 AND id = scope_identity()` [test].
2. **The order of multi-row `OUTPUT`/`RETURNING` is not guaranteed** on SQL Server, PostgreSQL or SQLite [docs].
   Correlating generated keys to entities by row order is a latent data-corruption bug. Correct options are
   `MERGE` with a position column (SQL Server), or one statement per row in a single command, which gives one
   result set per row (everything else) [test].
3. **The affected-row count lies on SQL Server.** It includes rows written by triggers, and it is -1 under
   `SET NOCOUNT ON` [docs][test: the trigger table writes 2 audit rows per change]. `SELECT @@ROWCOUNT` right
   after the statement is exact.
4. **MySQL's affected rows means "changed", not "matched",** unless the client sets `CLIENT_FOUND_ROWS`.
   MySqlConnector and MySql.Data set it by default (`UseAffectedRows=false`) [docs]. An update that writes
   identical values would otherwise look like "row not found".
5. **`LAST_INSERT_ID()` after a multi-row insert returns the *first* id.** Assuming the rest are consecutive
   depends on `innodb_autoinc_lock_mode` and `auto_increment_increment` [docs]. Re-selecting per statement avoids
   the assumption.
6. **Microsoft.Data.Sqlite binds every parameter of a command to every statement in it** [test: one 1,000-statement
   batch allocated 462 MB]. Round trips are free in-process, so batching statements there is pure cost.
7. **Dapper's `DynamicParameters` checks `command.Parameters.Contains(name)` before every add** [src + test]. That is
   O(n²) with linear parameter collections; SqlClient's collection compares names culture-aware. A 2,098-parameter
   batch pays millions of comparisons before it is even sent.
8. **NULL parameters without a declared type** are sent by some providers as `nvarchar`, which SQL Server refuses to
   convert into `varbinary` [docs]. Batch parameters must declare a `DbType` for NULLs.
9. **Dapper cannot materialize `Guid` from SQLite TEXT,** and its per-column row parser performs no type conversion,
   so SQLite TEXT timestamps don't convert to `DateTime` [test]. Users need a type handler for Guids; read-back
   needs its own lenient conversion.

## 3. Modern .NET practice relevant here

- **Target frameworks.** .NET 10 is the current LTS (November 2025); .NET 8 LTS support ends in November 2026.
  Libraries ship `net8.0;net10.0`.
- **Async.** `DbConnection` provides true async I/O; `IDbConnection` does not. Dapper's async extensions accept
  `IDbConnection` but require a `DbConnection` at runtime.
- **Source generators and AOT.** Dapper itself emits IL at runtime and is not AOT-compatible; Dapper.AOT
  intercepts *call sites in user code* at build time. A layer that builds SQL at runtime cannot use those
  interceptors, so its AOT story is bounded by Dapper's. Metadata reflection runs once per type and is not the
  bottleneck (section 2, items 6 and 7 are).
- **Testing.** Testcontainers (4.15) provides real PostgreSQL, SQL Server and MySQL in CI. xUnit v3 runs on
  Microsoft.Testing.Platform, which .NET 10's `dotnet test` requires.

## 4. Where the room is

Already solved well, so this project does not compete:

- Querying and materialization (Dapper).
- LINQ query translation (EF Core, linq2db, and Dommel's subset).
- Migrations (EF Core, DbUp, FluentMigrator, Grate).
- Paid bulk merge with identity output (Dapper Plus).

Not solved for Dapper users without adopting a second ecosystem:

1. **Generated-value round-trip that is correct on every database:** identity, defaults, computed, rowversion,
   trigger tables, and trigger-modified values.
2. **Batched inserts that return every key, correctly correlated,** inside one transaction and within parameter
   limits. The free Dapper extensions do one round trip per row and return nothing.
3. **Optimistic concurrency,** available in EF but absent from Contrib, Dommel and DapperExtensions.
4. **A mapping that hand-written SQL can use**: renamed and snake_cased columns that still materialize through
   plain Dapper.
5. **Visible, testable SQL**: deterministic statements you can assert on in unit tests.
6. **An honest split between "batched SQL" and "bulk protocol"**, with free bulk copy for SQL Server and PostgreSQL.

[`PRODUCT_THESIS.md`](../PRODUCT_THESIS.md) turns this list into the product.
