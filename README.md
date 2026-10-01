# Writeback

[![NuGet](https://img.shields.io/nuget/v/Writeback.svg)](https://www.nuget.org/packages/Writeback)
[![ci](https://github.com/Murat7Ay/Writeback/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/Writeback/actions/workflows/ci.yml)

**The write side of Dapper, done right on SQL Server, PostgreSQL, MySQL and SQLite.** Insert, update and delete
with every database-generated value written back, optimistic concurrency, and honest batching. Reads stay plain
Dapper and plain SQL. No change tracking, no LINQ, no `DbContext`.

```csharp
[Table("customer")]
public class Customer
{
    public long Id { get; set; }                       // key + identity by convention

    public string Name { get; set; } = "";

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }            // DEFAULT now(): read back after insert

    [ConcurrencyCheck]
    public int Version { get; set; }                   // optimistic concurrency, portable

    [NotMapped]
    public string UiOnlyValue { get; set; } = "";      // never persisted
}
```

```csharp
var customer = new Customer { Name = "Ada" };
await db.InsertAsync(customer);                        // customer.Id and customer.CreatedAt are now set

customer.Name = "Ada Lovelace";
await db.UpdateAsync(customer);                        // WHERE "Version" = @Version; Version is now 1
await db.UpdateAsync(customer, c => new { c.Name });   // only the columns you name

await db.InsertManyAsync(customers);                   // batched, one transaction, every Id written back
await db.DeleteAsync(customer);

var found  = await db.GetAsync<Customer>(42);
var active = await db.SelectAsync<Customer>("WHERE \"Name\" LIKE @p ORDER BY \"Id\"", new { p = "A%" });
```

Those attributes are standard `System.ComponentModel.DataAnnotations`, the same ones EF Core reads, so your
entities don't reference this library.

## Why not just Dapper?

Because the write path is where hand-written Dapper code goes subtly wrong, and it goes wrong differently on each
database:

| You want | By hand with Dapper | With Writeback |
|---|---|---|
| The generated key **and** defaults, computed columns, rowversion | different SQL per database: `OUTPUT`, `RETURNING`, `LAST_INSERT_ID()` + re-select | `InsertAsync` picks the right one |
| …on a SQL Server table **with triggers** | `OUTPUT` fails (error 334) and would show pre-trigger values anyway | `[HasTriggers]` → trigger-safe re-select that sees trigger-written values |
| Insert 10,000 rows **and get their keys** | `Execute(sql, list)` = 10,000 round trips, no keys (also what Dapper.Contrib and Dommel do) | chunked to the parameter limit, one transaction, every key correlated exactly (never by row order) |
| Detect lost updates | write `WHERE version = @v`, check counts, handle `NOCOUNT` and trigger-inflated row counts | `[ConcurrencyCheck]` or `[Timestamp]` → `ConcurrencyConflictException` |
| Update two columns | hand-write the `UPDATE` | `UpdateAsync(e, x => new { x.A, x.B })` |
| Rename a column | grep string literals | `[Column("full_name")]`; generated SQL aliases it, and `db.Sql<T>()` fragments keep your own SQL in sync |
| See exactly what runs | (it's your SQL) | `EntitySql.For<Customer>(SqlDialect.PostgreSql).Insert`, deterministic and assertable in tests |

Each one sounds small, but this repository's integration tests exist because each was found wrong at least once,
including in this project's own first draft. The full argument is in [the product thesis](https://github.com/Murat7Ay/Writeback/blob/master/docs/PRODUCT_THESIS.md).

## Install

```bash
dotnet add package Writeback             # SQL Server, PostgreSQL, MySQL, SQLite: dialects need no driver reference
dotnet add package Writeback.SqlServer   # optional: BulkCopyAsync via SqlBulkCopy
dotnet add package Writeback.PostgreSql  # optional: BulkCopyAsync via binary COPY
```

The dialect is detected from the connection type: `SqlConnection` (both clients), `NpgsqlConnection`,
`MySqlConnection` (MySqlConnector or MySql.Data), and `SqliteConnection` (Microsoft.Data.Sqlite or
System.Data.SQLite). Wrappers like MiniProfiler are unwrapped. Anything else takes one line:
`o.UseDialect<MyConnection>(SqlDialect.PostgreSql)`.

## A two-minute tour

```csharp
// Optional, once at startup: conventions and fluent mapping for types you don't annotate.
WritebackConfig.Configure(o =>
{
    o.NamingConvention = NamingConvention.SnakeCase;            // CreatedAt -> created_at
    o.Entity<Invoice>(e =>
    {
        e.ToTable("invoices", schema: "billing");
        e.HasKey(x => new { x.Year, x.Number });                // composite key
        e.Property(x => x.Total).ValueGeneratedOnInsertAndUpdate();
        e.HasTriggers();                                        // SQL Server: no OUTPUT on this table
    });
});

// Composite keys
var invoice = await db.GetAsync<Invoice>(new { Year = 2026, Number = 17 });

// Conflicts are exceptions, never silent
try { await db.UpdateAsync(staleCopy); }
catch (ConcurrencyConflictException ex) { /* ex.Entities: who conflicted */ }

// Batches are all-or-nothing; entities change only after commit
await db.UpdateManyAsync(invoices);
await db.DeleteManyAsync(oldInvoices);
var several = await db.GetManyAsync<Customer>(new[] { 1L, 2L, 3L });

// Querying stays SQL; the mapping gives you correctly quoted, aliased fragments
var c = db.Sql<Customer>();
var rows = await db.QueryAsync<Customer>(
    $"SELECT {c.ColumnsOf("c")} FROM {c.Table} c WHERE {c.Column(x => x.Email)} = @email", new { email });
await foreach (var x in db.SelectUnbufferedAsync<Customer>("ORDER BY \"Id\"")) { /* streamed */ }

// Millions of rows, no read-back: the provider's bulk protocol
await sqlConnection.BulkCopyAsync(rows);                        // Writeback.SqlServer
```

Run the whole thing against in-memory SQLite: `dotnet run --project samples/Writeback.Samples`.

## What it deliberately doesn't do

LINQ queries, relationships/navigation properties, change tracking, identity maps, unit of work, lazy loading,
and migrations. Those are EF Core's job, and EF Core is good at them. Here, every method maps to one SQL
statement (or one batch per chunk) that you can print. Loading and saving aggregates explicitly takes a few lines:
see [the recipes](https://github.com/Murat7Ay/Writeback/blob/master/docs/examples/RECIPES.md).

## Supported

| | SQL Server 2016+ | PostgreSQL 12+ | MySQL 8.0+ | SQLite 3.35+ |
|---|---|---|---|---|
| Generated values on insert | `OUTPUT` / trigger-safe re-select | `RETURNING` | `LAST_INSERT_ID()` re-select | `RETURNING` |
| Generated values on update | `OUTPUT` / re-select | `RETURNING` | re-select | `RETURNING` |
| Batch insert with keys | `MERGE` + position | statement per row, 1 round trip | statement per row, 1 round trip | prepared statement, 1 transaction |
| Bulk copy (no read-back) | `SqlBulkCopy` | binary `COPY` | none | none |
| Integration-tested here | 2022 | 18 | 8.4 | bundled |

The library targets .NET 8 and .NET 10, async-only with `CancellationToken` throughout, and depends only on Dapper.

## Performance

These numbers are in-process (SQLite), so they measure library overhead, not network time:

- **`GetAsync`:** at parity with hand-written Dapper.
- **`InsertAsync`:** about 16 µs more than hand-written Dapper, which is the cost of reading back generated
  values.
- **`InsertManyAsync`, 1,000 rows with every key written back:** 12 ms, against 73 ms for EF Core 10.
- **Allocations:** about 25× less than EF Core per single-row operation.

Over a network, `InsertManyAsync` needs one round trip per chunk, where `Execute(list)` needs one per row.
Methodology, tables and caveats are in [docs/BENCHMARKS.md](https://github.com/Murat7Ay/Writeback/blob/master/docs/BENCHMARKS.md).

## Documentation

| | |
|---|---|
| [Product thesis](https://github.com/Murat7Ay/Writeback/blob/master/docs/PRODUCT_THESIS.md) | why this exists, for whom, and what it refuses to be |
| [Ecosystem research](https://github.com/Murat7Ay/Writeback/blob/master/docs/research/ECOSYSTEM_RESEARCH.md) | Dapper, Contrib, Dommel, RepoDb, Dapper Plus, EF Core; database capabilities |
| [Architecture](https://github.com/Murat7Ay/Writeback/blob/master/docs/architecture/ARCHITECTURE.md) | metadata → statements → dialects → execution; adding a database |
| [Decisions (ADRs)](https://github.com/Murat7Ay/Writeback/blob/master/docs/architecture/DECISIONS.md) | the 14 decisions that shape the public API |
| [Self-review](https://github.com/Murat7Ay/Writeback/blob/master/docs/architecture/REVIEW.md) | what an external review found and how it was fixed |
| [Recipes](https://github.com/Murat7Ay/Writeback/blob/master/docs/examples/RECIPES.md) | aggregates, many-to-many, paging, upsert, type handlers, DI, bulk |
| [Benchmarks](https://github.com/Murat7Ay/Writeback/blob/master/docs/BENCHMARKS.md) | raw Dapper vs Writeback vs EF Core |
| [Migration](https://github.com/Murat7Ay/Writeback/blob/master/docs/MIGRATION.md) | from the original 2017 DapperHelper API |
| [Releasing](https://github.com/Murat7Ay/Writeback/blob/master/docs/RELEASING.md) | publishing to nuget.org with Trusted Publishing |

## Building and testing

```bash
dotnet build Writeback.slnx
```

```bash
dotnet test --project tests/Writeback.Tests
```

```bash
dotnet test --project tests/Writeback.IntegrationTests
```

- **Unit tests:** mapping rules and exact SQL per dialect. No database needed.
- **Integration tests:** one behavioral contract run against SQLite, PostgreSQL, MySQL and SQL Server, using
  Testcontainers (Docker). Without Docker the server suites are skipped, not failed. Set `WRITEBACK_POSTGRES`,
  `WRITEBACK_SQLSERVER` or `WRITEBACK_MYSQL` to use existing servers instead.
- **Benchmarks:** `dotnet run -c Release --project benchmarks/Writeback.Benchmarks -- --filter '*'`

## History

Writeback was called **DapperExtension** until October 2026 ([why it was renamed](https://github.com/Murat7Ay/Writeback/blob/master/docs/architecture/DECISIONS.md#adr-014-package-name)).
The original 2017 helper lives in git history; [docs/MIGRATION.md](https://github.com/Murat7Ay/Writeback/blob/master/docs/MIGRATION.md) maps its API to this one.

## License

[MIT](https://github.com/Murat7Ay/Writeback/blob/master/LICENSE)
