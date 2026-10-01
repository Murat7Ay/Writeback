# Architecture

## Overview

```text
 your code ──► ConnectionExtensions (InsertAsync, UpdateAsync, …)       EntitySql<T> (fragments, statements)
                    │                                                         │
                    ▼                                                         ▼
               EntityModel ── frozen options + caches ─────────────────────────┘
                    │
     ┌──────────────┼───────────────────────────┐
     ▼              ▼                           ▼
 EntityMapBuilder  dialect resolution       EntityCommands (per entity × dialect, cached)
 (fluent > attrs     (explicit > resolver        │  builds statement descriptions:
  > conventions)      > type name > unwrap)      │  InsertStatement / UpdateStatement / RowFilter
     │                                           ▼
     ▼                                      SqlDialect.Insert/Update/Delete/…  ──►  CommandPlan
 EntityMap / ColumnMap                                         (SQL text + StatementOutcome + read-back columns)
 (immutable, cached)                                               │
                                                                   ▼
                                                    Executor ── Dapper ── ADO.NET provider
                                                    (outcome interpretation, read-back conversion,
                                                     batching, transactions, error translation)
```

Every box below `ConnectionExtensions` is internal except the metadata types (`EntityMap`, `ColumnMap`), the
dialects, and the statement model (`Writeback.Sql`). Those are public so that a new database can be added
outside this repository.

## Packages

| Package | Depends on | Contains |
|---|---|---|
| `Writeback` | Dapper | Mapping, all four dialects, every command, `EntityDataReader<T>` |
| `Writeback.SqlServer` | core + Microsoft.Data.SqlClient | `BulkCopyAsync` via `SqlBulkCopy` |
| `Writeback.PostgreSql` | core + Npgsql | `BulkCopyAsync` via binary `COPY` |

Dialects are pure SQL text and reference no ADO.NET provider, so they all live in core and a MySQL or SQLite
user installs one package. Provider packages exist **only** for features that need the driver's own API
([ADR-004](DECISIONS.md#adr-004-dialects-in-core-provider-packages-only-for-driver-apis)).

## 1. Metadata

`EntityMapBuilder` turns a CLR type into an immutable `EntityMap`. It runs once per type per configuration, and
precedence is **fluent > DataAnnotations attributes > conventions**:

| Concern | Convention | Attribute | Fluent |
|---|---|---|---|
| Table | type name through `NamingConvention` (no pluralization) | `[Table(name, Schema=)]` | `ToTable(name, schema)` |
| Column | property name through `NamingConvention` | `[Column(name)]` | `Property(x).HasColumnName()` |
| Excluded | none | `[NotMapped]` | `Ignore(x => …)` |
| Key | `Id`, then `{Type}Id` | `[Key]` (several = composite) | `HasKey(x => x.Id)` / `HasKey(x => new { x.A, x.B })` |
| Identity | single integer key | `[DatabaseGenerated(Identity)]` / `(None)` | `ValueGeneratedOnInsert()` / `ValueGeneratedNever()` |
| Generated on insert | none | `[DatabaseGenerated(Identity)]` on a non-key | `ValueGeneratedOnInsert()` |
| Computed | none | `[DatabaseGenerated(Computed)]` | `ValueGeneratedOnInsertAndUpdate()` |
| Insert-only | none | `[Editable(false, AllowInitialValue = true)]` | `IsInsertOnly()` |
| Read-only | none | `[Editable(false)]` | `IsReadOnly()` |
| Row version | none | `[Timestamp]`, or `[ConcurrencyCheck]` + Computed | `IsRowVersion()` |
| Version counter | none | `[ConcurrencyCheck]` on an integer | `IsVersionCounter()` |
| Triggers (SQL Server) | none | `[HasTriggers]` | `HasTriggers()` |

From those inputs each `ColumnMap` derives `IsInsertable`, `IsUpdatable` and `IsIdentity`, and each `EntityMap`
derives `InsertColumns`, `UpdateColumns`, `InsertReadback` and `UpdateReadback`. Everything downstream works only
from these derived lists.

**Validation collects every problem and then throws once** (`EntityMappingException.Problems`). The checks are:
unsupported property types (navigations and collections, with guidance), `"dbo.Table"` style names, generated
columns without setters, non-integer version counters, two tokens, composite identity keys, duplicate column
names, and fluent references to unknown or ignored properties. Fluent-configured entities are validated inside
`Configure`, so errors appear at startup.

Accessors are compiled expression trees, or reflection when `RuntimeFeature.IsDynamicCodeCompiled` is false. A
setter assigns `default` for NULL into value types. `init` setters work, so positional records are supported.

## 2. Statements and dialects

`EntityCommands` builds **descriptions** of statements, and `SqlDialect` renders them:

- `InsertStatement(entity, columns, rows[parameter names], readback)`
- `UpdateStatement(entity, set, increment, where, readback, requireRowSignal)`
- `RowFilter(anyOf: [[column = parameter, …], …])`, rendered as `a = @x AND b = @y`, as `col IN (...)` when
  every group has one predicate on the same column, or as OR-ed groups.

Rendering goes through `SqlBuilder`, which offers only three primitives: keywords, quoted identifiers
(`QuoteIdentifier`, which escapes the quote character) and parameter placeholders. **No code path can splice a
value or an unquoted name into SQL.** Identifiers are always quoted, so reserved words such as a table named
`order` with columns `select` and `from` just work; the integration tests prove it.

A dialect returns a `CommandPlan`: the SQL, the read-back columns, and a **`StatementOutcome`** telling the
executor how to interpret the results:

| Outcome | Meaning | Used for |
|---|---|---|
| `AffectedRows` | provider's affected-row count | PostgreSQL/MySQL/SQLite updates and deletes without read-back |
| `ScalarRowCount` | statement ends with `SELECT @@ROWCOUNT` | every SQL Server update/delete: triggers inflate the provider count, `NOCOUNT` makes it -1 |
| `ReturnedRow` | one row if and only if the row was written | single-row writes with read-back or a row signal |
| `ResultSetPerRow` | one result set per input row, in order | batched writes needing per-row read-back or success |
| `PositionedRows` | rows carry their input position in the last column | SQL Server `MERGE` batch insert |

### Generated values by database

| | Single insert/update | Batch insert with read-back | Batch insert without read-back |
|---|---|---|---|
| SQL Server | `OUTPUT INSERTED.…` | `MERGE … USING (VALUES (…, 0), (…, 1)) … OUTPUT INSERTED.…, i.position` | multi-row `VALUES` |
| SQL Server + triggers | `…; SELECT … WHERE @@ROWCOUNT = 1 AND id = scope_identity()` | the same statement repeated, one result set per row | multi-row `VALUES` |
| PostgreSQL | `RETURNING` | one `INSERT … RETURNING` per row in one command | multi-row `VALUES` |
| MySQL | `…; SELECT … WHERE ROW_COUNT() = 1 AND id = LAST_INSERT_ID()` | the same statement repeated in one command | multi-row `VALUES` |
| SQLite | `RETURNING` | the cached single-row statement, executed per row in one transaction | multi-row `VALUES` |

The re-select needs a way to find the row. That is either the identity function (integer identity keys only) or
key parameters (keys supplied by the application). A database-generated **non-integer** key, such as `Guid DEFAULT
NEWID()`, can only be read back with `RETURNING`/`OUTPUT`. On MySQL or SQL Server trigger tables, the plan fails
at build time with an explanation instead of silently matching the wrong row.

### Update semantics

- **Full update:** all `UpdateColumns`. These never include keys, insert-generated columns (so a `CreatedAt`
  default can't be clobbered by an entity built from a DTO), computed or insert-only columns, or tokens.
- **Partial update:** `UpdateAsync(e, x => new { x.A, x.B })` uses those columns only. The plan is cached per
  column set.
- **Version counter:** `SET v = v + 1 … WHERE … AND v = @v`. After success the entity's value is incremented in
  memory, so no read-back is needed.
- **Row version:** `WHERE … AND rv = @rv`, with the new value read back.
- **Zero rows matched:** `false` without a token; `ConcurrencyConflictException` with one.

## 3. Execution

`Executor` runs plans through Dapper:

- **Single-row statements bind by property name** (`@Name`), so the entity instance itself is Dapper's parameter
  object. That means Dapper's cached IL binder, Dapper's type handlers, and no extra allocation.
- **Batch statements use positional names** (`@p0…`) and `BatchParameters`, which adds parameters in O(n).
  Dapper's `DynamicParameters` checks `Parameters.Contains` before every add, which is quadratic. Values of
  types with a Dapper type handler are still routed through Dapper. NULLs carry an explicit `DbType`.
- **Read-back** uses Dapper's per-column row parsers (so type handlers apply) followed by `ValueConverter` (SQLite
  TEXT → `DateTime`/`Guid`, `decimal` identity → `long`, and so on). Conversion failures name the column and both
  types.
- **Readers are drained,** so an error raised by a later statement in a batch surfaces as an exception instead of
  being swallowed at dispose.
- **Database errors are translated** where a fix is known. SQL Server error 334 (an `OUTPUT` on a trigger table)
  becomes "mark it `[HasTriggers]`", keeping the original as `InnerException`.

### Batches

```text
chunk = min(options.MaxBatchSize (1000), dialect.MaxRowsPerStatement (1000), dialect.MaxParameters / paramsPerRow)
```

1. A closed connection is opened once for the whole operation.
2. A transaction is started unless the caller passed one or an ambient `TransactionScope` exists.
3. Chunks execute in order.
4. Read-back values are **staged** and applied to entities only after commit.
5. On any failure the transaction rolls back and no entity has been modified.

`InsertManyAsync` of one entity delegates to `InsertAsync`. `DeleteManyAsync` within one chunk is a single
`DELETE … WHERE key IN (…)`, which is atomic without a transaction.

Only full-size chunk SQL is cached. Remainder sizes vary, so they are rendered on demand and sent with Dapper's
`NoCache` flag. Neither cache can grow without bound.

## 4. Caching and cost per call

| What | Built | Cached in |
|---|---|---|
| `EntityMap` + compiled accessors | first use of the type | `EntityModel` (`ConcurrentDictionary<Type, …>`) |
| Dialect for a connection type | first use of that connection type | `EntityModel` |
| Every single-row statement | first use per entity × dialect | `EntityCommands` (`Lazy<T>`) |
| Partial-update statements | first use per column set | `EntityCommands` |
| Full-size batch statements | first use per size | `EntityCommands` |
| Dapper parameter binders / row materializers | by Dapper | Dapper |

The steady-state cost of `InsertAsync`, beyond what Dapper itself does, is two dictionary lookups, a plan field
read, and a few small allocations for read-back. [`docs/BENCHMARKS.md`](../BENCHMARKS.md) has the measurements.

## 5. Configuration and thread-safety

`WritebackConfig.Configure` builds a new immutable `EntityModel` and publishes it with `Volatile.Write`.
Calling it again replaces the model atomically (test hosts call startup repeatedly), and calls already in flight
keep the model they started with. All caches are `ConcurrentDictionary` or `Lazy<T>` in
`ExecutionAndPublication` mode. Plans and maps are immutable after construction, and no per-call state is shared.

The configuration is static, as Dapper's is, because extension methods on `IDbConnection` have nowhere else to
find it ([ADR-008](DECISIONS.md#adr-008-static-configuration)). For the rare process that needs two mappings of
one type, `EntityModel` is already instance-based internally.

## 6. Dialect resolution

The dialect for a connection is resolved in this order:

1. Registered resolvers (`UseDialectResolver`), which run on every call.
2. Explicit registrations (`UseDialect<TConnection>`, which also match subclasses).
3. **Exact full type names** of known connections, including base types: `Microsoft.Data.SqlClient.SqlConnection`,
   `System.Data.SqlClient.SqlConnection`, `Npgsql.NpgsqlConnection`, `MySqlConnector.MySqlConnection`,
   `MySql.Data.MySqlClient.MySqlConnection`, `Microsoft.Data.Sqlite.SqliteConnection`,
   `System.Data.SQLite.SQLiteConnection`.
4. Wrapping connections with a public `WrappedConnection` or `InnerConnection` property (MiniProfiler and
   similar), which are unwrapped recursively.
5. Otherwise `DialectNotFoundException`, telling you the exact `UseDialect<…>` call to add.

## 7. Adding a database

Subclass `SqlDialect` and override what differs. The capability members (`GetReadbackMethod`,
`IdentityFunction`, `RowCountFunction`, `DefaultValuesClause`, `BatchesStatements`, `MaxParameters`) cover most
databases. When syntax genuinely differs, override one rendering hook.

For example, MariaDB 10.5+ has `INSERT … RETURNING` but no `UPDATE … RETURNING`. Updates therefore keep the
MySQL-style re-select, while inserts switch to `RETURNING`:

```csharp
public sealed class MariaDbDialect : SqlDialect
{
    public override string Name => "MariaDB";
    public override int MaxParameters => 65535;
    public override string QuoteIdentifier(string id) => "`" + id.Replace("`", "``") + "`";

    // Updates: re-select in the same batch, exactly like MySQL.
    protected override ReadbackMethod GetReadbackMethod(EntityMap e) => ReadbackMethod.Reselect;
    protected override string IdentityFunction => "LAST_INSERT_ID()";
    protected override string RowCountFunction => "ROW_COUNT()";
    protected override string DefaultValuesClause => "() VALUES ()";

    // Inserts: RETURNING. (Zero-column inserts omitted for brevity.)
    protected override void AppendInsertRow(SqlBuilder sql, EntityMap e, IReadOnlyList<ColumnMap> columns,
        IReadOnlyList<string> parameters, IReadOnlyList<ColumnMap> readback)
    {
        sql.Append("INSERT INTO ").Table(e).Append(" (").ColumnList(columns)
           .Append(") VALUES (").ParameterList(parameters).Append(")");
        if (readback.Count > 0)
        {
            sql.Append(" RETURNING ").ColumnList(readback);
        }

        sql.Append(";");
    }
}

WritebackConfig.Configure(o => o.UseDialect<MySqlConnection>(new MariaDbDialect()));
```

Then assert its output in unit tests with `EntitySql.For<T>(new MariaDbDialect())`, and run
`PersistenceTests<TFixture>` against a MariaDB fixture. The behavioral suite is database-agnostic by design.

## 8. AOT and trimming

The core **is not marked AOT-compatible, and says so honestly**: Dapper emits IL at runtime. Its own
reflection is confined to building maps (once per type), and accessors fall back to plain reflection when
dynamic code is not compiled. Supporting AOT properly would need Dapper.AOT-style generated binders, plus a source
generator that emits `EntityMap`s. The metadata model is already a plain immutable object graph that a generator
could produce ([ADR-010](DECISIONS.md#adr-010-no-source-generator-in-v1)).

## 9. Tests

| Suite | What | Runs against |
|---|---|---|
| `Writeback.Tests` (87) | mapping rules and errors, **exact SQL text per dialect**, quoting and injection, chunk sizing, dialect resolution, key binding | nothing (pure) |
| `Writeback.IntegrationTests` (97) | one behavioral contract (23 tests) run unchanged on each database, plus database-specific tests (triggers, rowversion, bulk copy) | SQLite (file), PostgreSQL 18, MySQL 8.4, SQL Server 2022 (Testcontainers) |
| `Writeback.Benchmarks` | raw Dapper vs Writeback vs EF Core 10 | in-memory SQLite |

Docker-backed suites skip (not fail) when Docker is unavailable. `WRITEBACK_POSTGRES`, `WRITEBACK_SQLSERVER` and `WRITEBACK_MYSQL`
point them at existing servers for CI.
