# Benchmarks

Source: [`benchmarks/Writeback.Benchmarks`](../benchmarks/Writeback.Benchmarks/Program.cs).

```bash
dotnet run -c Release --project benchmarks/Writeback.Benchmarks -- --filter '*' --job medium
```

## Setup and caveats

- **Database:** in-memory SQLite (Microsoft.Data.Sqlite 10.0.12). Having no network removes latency, so the
  numbers show *library overhead*, which is the question for a layer on top of Dapper. Against a networked
  database every variant pays the same round-trip time, and relative differences shrink accordingly. The
  exception is batches, where round-trip *count* dominates.
- **Equal semantics:** every insert variant ends with the generated `Id` and `CreatedAt` in the entity, except
  the `Execute(list)` baseline, which returns no keys. That is the point of comparing against it.
- **EF Core 10:** a new `DbContext` per operation (the usual pattern in web apps), sharing the open connection.
  Reads use `AsNoTracking`.
- **Machine:** a laptop (Intel i7-10510U, Windows 11, .NET 10.0.12), BenchmarkDotNet 0.15.8, MediumRun (15
  iterations × 2 launches). It is noisy; compare **ratios within one table**, not absolute µs across runs.

## Single row

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Insert: raw Dapper (hand-written `INSERT … RETURNING`) | 33.3 µs | 1.00 | 2.24 KB |
| **Insert: Writeback** | **49.3 µs** | **1.54** | **2.63 KB** |
| Insert: EF Core (new context) | 293.9 µs | 9.20 | 60.88 KB |
| Get: raw Dapper | 11.8 µs | 0.37 | 1.98 KB |
| **Get: Writeback** | **12.3 µs** | **0.38** | **2.18 KB** |
| Get: EF Core `AsNoTracking` (new context) | 163.1 µs | 5.10 | 58.33 KB |

- **`GetAsync` is at parity with hand-written Dapper.**
- **`InsertAsync` costs about 16 µs more in-process.** That is mostly read-back (Dapper's per-column parsers plus
  conversion, which also causes the Gen1 promotions) on an engine where the INSERT itself is cheap. Against a
  server, this is noise next to one network round trip. The library's own CPU work was measured separately: about
  40 ns for plan lookup, and 0.2–0.45 µs for the parsers.
- **Against EF Core:** 6–13× less time and about 25× less allocation per operation.

## Batch insert

| Method | Rows | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Dapper `Execute(list)`, **no keys back** (what Contrib/Dommel do) | 100 | 0.29 ms | 1.00 | 102 KB |
| **Writeback `InsertManyAsync`, keys + defaults back** | 100 | **1.14 ms** | **3.95** | **142 KB** |
| EF Core `AddRange` + `SaveChanges`, keys + defaults back | 100 | 4.67 ms | 16.21 | 876 KB |
| Dapper `Execute(list)`, **no keys back** | 1000 | 3.18 ms | 1.00 | 1,046 KB |
| **Writeback `InsertManyAsync`, keys + defaults back** | 1000 | **12.38 ms** | **3.96** | **1,437 KB** |
| EF Core `AddRange` + `SaveChanges`, keys + defaults back | 1000 | 72.67 ms | 23.25 | 8,303 KB |

- **It scales linearly.** The ratio is the same at 100 and 1,000 rows.
- **Read-back is the remaining cost against the no-keys baseline.** On SQLite, `Execute(list)` can't return keys
  at all; Writeback runs one prepared `INSERT … RETURNING` per row and reads the result.
- **Against EF Core: about 6× faster and 6× less allocation** for the same outcome.
- **Over a network the picture inverts.** `Execute(list)` costs one round trip per row, while `InsertManyAsync`
  sends one command per chunk (up to 1,000 rows on PostgreSQL and MySQL, 699 rows of 3 columns on SQL Server).
  At 0.5 ms latency, 1,000 rows cost about 500 ms in round trips for `Execute(list)` versus about 0.5–1 ms for
  `InsertManyAsync`.

## History: the regression this suite caught

The first implementation used Dapper's `DynamicParameters` and multi-statement commands everywhere:

| `InsertManyAsync`, 1000 rows | Mean | Allocated |
|---|---:|---:|
| first draft | 1,348 ms | 462 MB |
| O(n) parameters, statement per command on SQLite | 83 ms | 2.7 MB |
| + one prepared statement reused per row on SQLite (current) | **12.4 ms** | **1.4 MB** |

The causes and fixes are in [REVIEW.md](architecture/REVIEW.md) (finding 1). `GetBreakdownBenchmarks` in the same
project bisects single-row cost into SQL text, parameter object and library code.
