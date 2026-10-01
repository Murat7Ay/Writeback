# Recipes

Short, copyable answers to "how do I…?". Every one is plain Dapper plus Writeback. None needs a feature
this library doesn't have.

- [Load an aggregate (1-N) without N+1](#load-an-aggregate-1-n-without-n1)
- [Save an aggregate](#save-an-aggregate)
- [Many-to-many](#many-to-many)
- [Paging](#paging)
- [PostgreSQL with snake_case](#postgresql-with-snake_case)
- [Upsert (until `UpsertAsync` exists)](#upsert-until-upsertasync-exists)
- [Transactions and `TransactionScope`](#transactions-and-transactionscope)
- [Strongly-typed IDs and other custom types](#strongly-typed-ids-and-other-custom-types)
- [Guids on SQLite](#guids-on-sqlite)
- [Precise `datetime2` on SQL Server](#precise-datetime2-on-sql-server)
- [Logging and asserting the SQL](#logging-and-asserting-the-sql)
- [Dependency injection](#dependency-injection)
- [Bulk loading millions of rows](#bulk-loading-millions-of-rows)

## Load an aggregate (1-N) without N+1

**Two queries** (the usual best choice for collections):

```csharp
var order = await db.GetAsync<Order>(orderId);
var line = db.Sql<OrderLine>();
order!.Lines = (await db.SelectAsync<OrderLine>(
    $"WHERE {line.Column(l => l.OrderId)} = @orderId ORDER BY {line.Column(l => l.LineNumber)}", new { orderId })).ToList();
```

`Lines` is `[NotMapped]` on `Order`. Collections are never columns.

**Many parents at once** (still two queries):

```csharp
var orders = await db.SelectAsync<Order>("WHERE customer_id = @customerId", new { customerId });
var lines = await db.SelectAsync<OrderLine>("WHERE order_id = ANY(@ids)", new { ids = orders.Select(o => o.Id).ToArray() }); // PostgreSQL
var byOrder = lines.ToLookup(l => l.OrderId);
foreach (var o in orders) o.Lines = byOrder[o.Id].ToList();
```

**One join** (multi-mapping, for 1-1 or N-1):

```csharp
var o = db.Sql<Order>();
var c = db.Sql<Customer>();
var rows = await db.QueryAsync<Order, Customer, Order>(
    $"SELECT {o.ColumnsOf("o")}, {c.ColumnsOf("c")} FROM {o.Table} o JOIN {c.Table} c ON c.\"Id\" = o.\"CustomerId\" WHERE o.\"Id\" = @id",
    (order, customer) => { order.Customer = customer; return order; },
    new { id }, splitOn: "Id");
```

## Save an aggregate

The parent's generated key is available immediately, so children can reference it:

```csharp
await using var tx = await db.BeginTransactionAsync();
await db.InsertAsync(order, tx);
foreach (var l in order.Lines) l.OrderId = order.Id;
await db.InsertManyAsync(order.Lines, tx);
await tx.CommitAsync();
```

Replacing the children ("save the new set") is a decision about your domain, so make it explicit:

```csharp
await db.ExecuteAsync("DELETE FROM order_line WHERE order_id = @id", new { id = order.Id }, tx);
await db.InsertManyAsync(order.Lines, tx);
```

## Many-to-many

Map the join table as an entity with a composite key:

```csharp
public class ProductTag { [Key] public int ProductId { get; set; } [Key] public int TagId { get; set; } }

await db.InsertManyAsync(tagIds.Select(t => new ProductTag { ProductId = p.Id, TagId = t }));
await db.DeleteByKeyAsync<ProductTag>(new { ProductId = p.Id, TagId = 7 });
```

## Paging

```csharp
// PostgreSQL / MySQL / SQLite
await db.SelectAsync<Customer>("ORDER BY \"Id\" LIMIT @take OFFSET @skip", new { take = 50, skip = 100 });
// SQL Server
await db.SelectAsync<Customer>("ORDER BY [Id] OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", new { take = 50, skip = 100 });
var total = await db.CountAsync<Customer>();
```

## PostgreSQL with snake_case

```csharp
WritebackConfig.Configure(o => o.NamingConvention = NamingConvention.SnakeCase);
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true; // so hand-written SELECT * also maps created_at -> CreatedAt
```

`CustomerOrder.CreatedAt` maps to `customer_order.created_at`. Generated `SELECT`s alias each column, so they
work either way.

## Upsert (until `UpsertAsync` exists)

Use the mapping fragments so names stay in sync:

```csharp
var s = db.Sql<Setting>();
await db.ExecuteAsync($"""
    INSERT INTO {s.Table} ({s.Column(x => x.Key)}, {s.Column(x => x.Value)}) VALUES (@Key, @Value)
    ON CONFLICT ({s.Column(x => x.Key)}) DO UPDATE SET {s.Column(x => x.Value)} = EXCLUDED.{s.Column(x => x.Value)}
    """, setting);
```

## Transactions and `TransactionScope`

- Every method takes an optional `IDbTransaction`.
- `*ManyAsync` methods without one create their own, so a batch is all-or-nothing.
- Inside a `TransactionScope` (with `TransactionScopeAsyncFlowOption.Enabled`), no local transaction is created;
  the batch joins the ambient one.

## Strongly-typed IDs and other custom types

Register a Dapper type handler before first use. Single-row parameters, batch parameters, materialization and
read-back all go through it:

```csharp
public readonly record struct CustomerId(long Value);

SqlMapper.AddTypeHandler(new CustomerIdHandler());
class CustomerIdHandler : SqlMapper.TypeHandler<CustomerId>
{
    public override void SetValue(IDbDataParameter p, CustomerId v) => p.Value = v.Value;
    public override CustomerId Parse(object v) => new(Convert.ToInt64(v));
}
```

A wrapped key is not an integer, so the identity convention doesn't apply. Mark a database-generated
strongly-typed key `[DatabaseGenerated(DatabaseGeneratedOption.Identity)]`. It is read back through
`OUTPUT`/`RETURNING`. On MySQL, or SQL Server tables with triggers, re-selecting needs a plain integer identity;
the mapping tells you so if you hit it.

`BulkCopyAsync` is the one exception: bulk protocols don't use parameters, so convert in a `[NotMapped]` wrapper
or load through `InsertManyAsync`.

## Guids on SQLite

SQLite stores Guids as TEXT, and plain Dapper can't read them back. Register a handler:

```csharp
SqlMapper.AddTypeHandler(new GuidHandler());
class GuidHandler : SqlMapper.TypeHandler<Guid>
{
    public override void SetValue(IDbDataParameter p, Guid v) => p.Value = v;
    public override Guid Parse(object v) => v is Guid g ? g : Guid.Parse((string)v);
}
```

## Precise `datetime2` on SQL Server

Dapper sends `DateTime` as `datetime` (1/300 s precision) by default. Opt in once:

```csharp
SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
```

Both single-row and batch operations honor it.

## Logging and asserting the SQL

```csharp
logger.LogDebug("Insert SQL: {Sql}", db.Sql<Customer>().Insert);

// in a unit test, without a database
Assert.Equal("""INSERT INTO "customer" ("Name") VALUES (@Name) RETURNING "Id";""",
    EntitySql.For<Customer>(SqlDialect.PostgreSql).Insert);
```

For runtime tracing, the ADO.NET providers already emit OpenTelemetry activities (SqlClient, Npgsql,
MySqlConnector).

## Dependency injection

Nothing to register: Writeback extends the connection you already inject. Configure mapping once in
`Program.cs`:

```csharp
WritebackConfig.Configure(o => o.Entity<Customer>(e => e.ToTable("customers", "sales")));
builder.Services.AddNpgsqlDataSource(connectionString);
// in a handler: await using var db = await dataSource.OpenConnectionAsync(ct); await db.InsertAsync(customer, cancellationToken: ct);
```

## Bulk loading millions of rows

```csharp
// SQL Server: Writeback.SqlServer
await sqlConnection.BulkCopyAsync(rows, transaction, new SqlServerBulkCopyOptions { BatchSize = 10_000 });
// PostgreSQL: Writeback.PostgreSql
await npgsqlConnection.BulkCopyAsync(rows);
```

Both stream from an `IEnumerable<T>` (nothing is buffered) and write the same columns `InsertAsync` would.
Generated values are not read back; use `InsertManyAsync` when you need them.
