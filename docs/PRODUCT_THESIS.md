# Product thesis

## The question

> Why would someone install Writeback instead of just using Dapper?

Writeback is **the write side that Dapper users would otherwise hand-write for each entity and each
database, and usually get subtly wrong.** Reads stay plain Dapper and plain SQL. Writes become one call that
does the right thing on SQL Server, PostgreSQL, MySQL and SQLite, and shows you the exact SQL it ran.

## What becomes possible, concretely

### 1. One call returns every database-generated value, on every database

```csharp
await db.InsertAsync(order);
// order.Id, order.CreatedAt (DEFAULT), order.Total (computed column), order.RowVersion: all populated
```

Doing this by hand means a different statement per database. Generated values come back through `OUTPUT`,
`RETURNING`, or `LAST_INSERT_ID()` plus a re-select, and there is a fourth variant for SQL Server tables with
triggers, where `OUTPUT` fails with error 334 and would report pre-trigger values anyway. Dapper.Contrib returns
only an `int` identity; Dommel returns an `object` id. Writeback reads back identity, defaults, computed
columns and row versions. On trigger tables it sees the values the trigger wrote.

*The original project died on exactly this:* its last two commits removed `OUTPUT inserted.*` because it broke on
tables with triggers. Here it is a mapping flag (`[HasTriggers]`). If you forget the flag, you get an exception
that tells you to add it, not SQL Server error 334.

### 2. Batched inserts that return every key, correctly matched to their entities

```csharp
await db.InsertManyAsync(tenThousandOrders);   // every order.Id is set, in one transaction
```

- **What raw Dapper and the free extensions do:** `Execute(sql, list)` runs one round trip per row and returns
  no keys.
- **What Writeback does:**
  - It chunks rows to fit each database's parameter limit (2,098 on SQL Server) and sends as few commands as
    possible.
  - It runs the whole operation in one transaction (yours, or its own).
  - It matches each generated key to the entity it belongs to by construction: `MERGE ... OUTPUT` with a
    position column on SQL Server, one result set per row elsewhere. It never relies on the order of
    multi-row `RETURNING`, which no database guarantees.
  - Entities are modified only after the transaction commits. A failed batch leaves your objects exactly as
    they were.

And when you need raw speed instead of keys, `BulkCopyAsync` uses `SqlBulkCopy` or PostgreSQL binary `COPY`.
It is named as a bulk copy, not as an insert.

### 3. Optimistic concurrency with one attribute

```csharp
[ConcurrencyCheck] public int Version { get; set; }   // portable, works on all four databases
// or
[Timestamp] public byte[] RowVersion { get; set; }    // SQL Server rowversion
```

Updates and deletes compare the token. A lost update throws `ConcurrencyConflictException`, which names the
entities involved; it is never silently ignored. In `UpdateManyAsync`, one conflict rolls back the batch and
leaves every entity untouched. No free Dapper extension does this.

### 4. Partial updates without change tracking

```csharp
await db.UpdateAsync(customer, c => new { c.Email, c.Status });
```

You state what changed, and the statement contains exactly those columns, plus the concurrency check. There is
no snapshot, no proxy and no `DbContext`.

### 5. A mapping that your own SQL can use

```csharp
var c = db.Sql<Customer>();
await db.QueryAsync<Customer>($"SELECT {c.Columns} FROM {c.Table} WHERE {c.Column(x => x.Email)} = @email", new { email });
```

Renamed or snake_cased columns come back as aliases (`"full_name" AS "Name"`), so **plain Dapper** materializes
them. A renamed property cannot drift from the SQL that uses it. Without this, renaming a column in Dapper means
grepping string literals.

### 6. Your SQL is visible and testable

```csharp
EntitySql.For<Customer>(SqlDialect.PostgreSql).Insert
// INSERT INTO "customer" ("Name", "Email") VALUES (@Name, @Email) RETURNING "Id", "CreatedAt";
```

Every statement is deterministic, cached once, made only of quoted identifiers and parameters, and assertable in
a unit test. There are no hidden queries: no lazy loading, no change detection, no "SaveChanges" that decides for
you what to send.

### 7. Your domain model doesn't reference this library

The mapping vocabulary is `System.ComponentModel.DataAnnotations`, which is part of .NET: `[Key]`, `[Table]`,
`[Column]`, `[NotMapped]`, `[DatabaseGenerated]`, `[ConcurrencyCheck]`, `[Timestamp]`, `[Editable]`. Those are the
same attributes EF Core reads, so an entity can move between EF Core and Dapper, or be used by both in one
codebase.

## Who it is for

- Teams that **chose Dapper on purpose** (for SQL control, predictability and speed) and are tired of
  hand-writing `INSERT` statements and read-back logic for every entity.
- Codebases that use EF Core for most things and Dapper for hot paths, and want those hot paths to *write*
  safely too.
- Multi-database products (SQLite in development and on edge devices, PostgreSQL or SQL Server in production).

## Who it is not for

- Teams that want LINQ queries, relationship graphs, change tracking or migrations: **use EF Core.** It is good
  at those, and this project will not imitate it.
- Teams already happy with Dapper Plus's licensed bulk merge.

## What it deliberately does not do

| Not included | Why |
|---|---|
| LINQ / expression queries | A translator is an ORM's hardest part and endlessly leaky. SQL stays SQL; `SelectAsync<T>("WHERE ...")` provides the mapped select list. |
| Relationships (1-1, 1-N, N-N) | The legacy TODO. Loading graphs is a query problem (Dapper multi-mapping already solves it). Saving graphs requires change tracking to be correct. See [ADR-007](architecture/DECISIONS.md#adr-007-no-relationships). |
| Change tracking, identity map, unit of work | Each one exists to make relationships and `SaveChanges` work. Without those, they are pure overhead. |
| Repositories / `DbContext` | `IDbConnection` is already the unit of work boundary Dapper users know. |
| Migrations | A separate concern with good existing tools. |
| Upsert | Valuable, but database-specific semantics need their own design. It is on the roadmap. |

## Why it needs to exist in 2026

Dapper is still the default for teams who want SQL control, and Dapper.AOT (2026) shows the core is actively
maintained. But the free write-side layer on top of it is a 2020-era package (Contrib) or extensions that each
do one round trip per row and return no keys. The serious alternatives are either commercial (Dapper Plus) or a
separate ORM ecosystem (RepoDb, EF Core).

The details in this space are where hand-written code fails, and building this project produced the evidence:

- **SQL Server writes with triggers.** `OUTPUT` breaks (the original project's fatal bug), and the affected-row
  count includes the trigger's own writes.
- **SQL Server row counts under `NOCOUNT`.** The provider reports -1, so a successful update looks like "not found".
- **Key correlation in batches.** No database guarantees the order of multi-row `RETURNING`.
- **Batch parameter binding.** Our *own* first implementation was quadratic: 1.35 s and 462 MB for 1,000 rows,
  from Dapper's `DynamicParameters` and Microsoft.Data.Sqlite's per-statement binding. The benchmarks found it
  and the fix is now locked in (see [REVIEW.md](architecture/REVIEW.md)).

A library is worth installing when it encodes knowledge like that once, tests it against real databases, and
gets out of the way for everything else.
