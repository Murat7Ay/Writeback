# Migrating from the original DapperHelper (2017)

Writeback is the rewrite of this repository's original project, published on GitHub as "DapperExtension" (assembly
`DapperHelper`). The original code is preserved in git history (`master` at `43093a0`). It targeted .NET Framework 4.5.2 and
Dapper 1.50, and only worked on SQL Server.

## API mapping

| Original | Now | Notes |
|---|---|---|
| `db.Insert(dto)` → returned a new `T` via `SELECT CAST(SCOPE_IDENTITY() AS INT) AS Id` | `await db.InsertAsync(dto)` | The **same instance** receives the key *and* every other generated value (defaults, computed, rowversion). Keys of type `long` no longer overflow through an `int` cast. |
| `db.InsertList(list)` → `bool` | `await db.InsertManyAsync(list)` → `int` | Previously one round trip per row with no keys. Now batched within parameter limits, in one transaction, and every key is written back. |
| `db.Update(dto, () => new Project { Name = "x" })` | `dto.Name = "x"; await db.UpdateAsync(dto, p => new { p.Name })` | The partial-update idea survives: you still say which columns change, but the values come from the entity. The old version compiled and invoked a lambda per property per call. |
| `db.Update` threw `InvalidCastException` when no row matched | `UpdateAsync` returns `false`, or throws `ConcurrencyConflictException` when the entity has a concurrency token | |
| (none) | `DeleteAsync`, `DeleteByKeyAsync`, `GetAsync`, `GetManyAsync`, `ExistsAsync`, `SelectAsync`, `CountAsync`, `UpdateManyAsync`, `DeleteManyAsync` | |
| `ConnectionFactory.GetOpenConnection(cs)` | removed | Use your provider's connection or data source (`new SqlConnection(cs)`, `NpgsqlDataSource`). Closed connections are opened as needed. |
| `GetConsumerSqlQuery` (string-built WHERE with inlined ids) | removed | It concatenated values into SQL. Use parameterized SQL (`SelectAsync<T>("WHERE ...", param)`). |

## Attributes

| Original | Now |
|---|---|
| `[DbTableName("dbo.Projects", "p")]` | `[Table("Projects", Schema = "dbo")]`. Aliases belong to queries: `db.Sql<Project>().ColumnsOf("p")`. A dotted name now produces an explicit error suggesting this form. |
| `[DbKey]` | `[Key]`, or nothing: a property named `Id` or `{Type}Id` is the key by convention, and integer keys are identity by convention. |
| `[Ignore]` | `[NotMapped]` (standard .NET), or `e.Ignore(x => x.Prop)` fluently. |

All of these come from `System.ComponentModel.DataAnnotations(.Schema)`, so the entity classes no longer
reference the data-access assembly.

## Behavior that changed on purpose

- **`OUTPUT inserted.*` is back, safely.** The last two original commits removed it because SQL Server rejects
  `OUTPUT` on tables with triggers. Now `OUTPUT` is used by default; mark trigger tables `[HasTriggers]` to get a
  trigger-safe re-select. If you forget, the error tells you exactly that.
- **Relationships (the old TODO) will not be implemented.** See
  [ADR-007](architecture/DECISIONS.md#adr-007-no-relationships) and the recipes for loading and saving
  aggregates explicitly.
- **Async only, with `CancellationToken`.**
- **Every identifier is quoted.** Tables and columns with reserved names (`Order`, `User`) work.
