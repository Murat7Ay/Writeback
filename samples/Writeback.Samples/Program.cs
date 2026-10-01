// A tour of Writeback against an in-memory SQLite database. Run: dotnet run --project samples/Writeback.Samples
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Dapper;
using Writeback;
using Microsoft.Data.Sqlite;

WritebackConfig.Configure(options => options
    .Entity<Product>(e =>
    {
        // A type you don't own (or don't want to annotate): map it fluently.
        e.ToTable("product");
        e.Property(p => p.Name).HasColumnName("product_name");
    }));

await using var db = new SqliteConnection("Data Source=:memory:");
await db.OpenAsync();
await db.ExecuteAsync("""
    CREATE TABLE customer (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Name TEXT NOT NULL,
        Email TEXT NULL,
        CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
        Version INTEGER NOT NULL);
    CREATE TABLE product (Id INTEGER PRIMARY KEY AUTOINCREMENT, product_name TEXT NOT NULL, Price NUMERIC NOT NULL);
    """);

// 1. Insert: identity key and database defaults are written back into the entity.
var ada = new Customer { Name = "Ada", Email = "ada@example.com" };
await db.InsertAsync(ada);
Console.WriteLine($"Inserted #{ada.Id}, created {ada.CreatedAt}");

// 2. Update everything, or only what you say changed (no change tracking). Version is checked and incremented.
ada.Email = "ada@lovelace.dev";
await db.UpdateAsync(ada, c => new { c.Email });
Console.WriteLine($"Updated, version is now {ada.Version}");

// 3. Optimistic concurrency: a stale copy cannot overwrite newer data.
var stale = await db.GetAsync<Customer>(ada.Id);
ada.Name = "Ada Lovelace";
await db.UpdateAsync(ada);
try
{
    stale!.Name = "lost update";
    await db.UpdateAsync(stale);
}
catch (ConcurrencyConflictException ex)
{
    Console.WriteLine($"Conflict detected: {ex.Message}");
}

// 4. Batches: one round trip per chunk, all-or-nothing, every generated key correlated back.
var customers = Enumerable.Range(1, 500).Select(i => new Customer { Name = $"Customer {i}" }).ToList();
await db.InsertManyAsync(customers);
Console.WriteLine($"InsertMany: ids {customers[0].Id}..{customers[^1].Id}");

// 5. Queries stay SQL. Fragments come from the mapping, so renames never drift.
var sql = db.Sql<Customer>();
var recent = await db.SelectAsync<Customer>($"WHERE {sql.Column(c => c.Name)} LIKE @p ORDER BY {sql.Column(c => c.Id)} DESC LIMIT 3", new { p = "Customer%" });
Console.WriteLine($"Latest: {string.Join(", ", recent.Select(c => c.Name))}");
Console.WriteLine($"Count: {await db.CountAsync<Customer>()}");

// 6. Renamed columns work with plain Dapper too, through the aliased column list.
var keyboard = new Product { Name = "Keyboard", Price = 49.90m };
await db.InsertAsync(keyboard);
var p = db.Sql<Product>();
var viaDapper = await db.QuerySingleAsync<Product>($"SELECT {p.Columns} FROM {p.Table} WHERE {p.Column(x => x.Name)} = @name", new { name = "Keyboard" });
Console.WriteLine($"Plain Dapper read: {viaDapper.Name} {viaDapper.Price}");

// 7. Nothing is hidden: every statement is inspectable.
Console.WriteLine();
Console.WriteLine("SQL used by InsertAsync<Customer>:  " + sql.Insert);
Console.WriteLine("SQL used by UpdateAsync<Customer>:  " + sql.Update);
Console.WriteLine("SQL Server would run instead:       " + EntitySql.For<Customer>(SqlDialect.SqlServer).Insert);

await db.DeleteManyAsync(customers);
Console.WriteLine($"After DeleteMany: {await db.CountAsync<Customer>()} customer(s) left");

[Table("customer")]
public class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }

    [ConcurrencyCheck]
    public int Version { get; set; }

    [NotMapped]
    public string Display => $"{Name} <{Email}>";
}

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
