using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
using Writeback;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Writeback.Benchmarks;

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[Table("bench_customer")]
public class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string? CreatedAt { get; set; }
}

public sealed class BenchContext(SqliteConnection connection) : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite(connection);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Customer>().Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
}

/// <summary>
/// Same semantics in every variant: the inserted entity ends up with its generated Id and CreatedAt.
/// SQLite in-memory removes network latency so the numbers show library overhead, which is what this library adds or saves.
/// </summary>
[MemoryDiagnoser]
public class SingleRowBenchmarks
{
    private SqliteConnection _connection = null!;
    private long _existingId;

    [GlobalSetup]
    public void Setup()
    {
        _connection = BenchDatabase.Open();
        _existingId = _connection.ExecuteScalar<long>("INSERT INTO bench_customer (Name, Email) VALUES ('seed', 'seed@example.com') RETURNING Id");
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark(Baseline = true, Description = "Insert: raw Dapper (hand-written SQL)")]
    public async Task<long> Insert_RawDapper()
    {
        var customer = new Customer { Name = "Ada", Email = "ada@example.com" };
        var generated = await _connection.QuerySingleAsync<(long Id, string CreatedAt)>(
            "INSERT INTO bench_customer (Name, Email) VALUES (@Name, @Email) RETURNING Id, CreatedAt", customer);
        customer.Id = generated.Id;
        customer.CreatedAt = generated.CreatedAt;
        return customer.Id;
    }

    [Benchmark(Description = "Insert: Writeback")]
    public async Task<long> Insert_Writeback()
    {
        var customer = new Customer { Name = "Ada", Email = "ada@example.com" };
        await _connection.InsertAsync(customer);
        return customer.Id;
    }

    [Benchmark(Description = "Insert: EF Core (new context)")]
    public async Task<long> Insert_EfCore()
    {
        await using var context = new BenchContext(_connection);
        var customer = new Customer { Name = "Ada", Email = "ada@example.com" };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer.Id;
    }

    [Benchmark(Description = "Get: raw Dapper")]
    public Task<Customer?> Get_RawDapper() =>
        _connection.QueryFirstOrDefaultAsync<Customer>("SELECT Id, Name, Email, CreatedAt FROM bench_customer WHERE Id = @id", new { id = _existingId });

    [Benchmark(Description = "Get: Writeback")]
    public Task<Customer?> Get_Writeback() => _connection.GetAsync<Customer>(_existingId);

    [Benchmark(Description = "Get: EF Core AsNoTracking (new context)")]
    public async Task<Customer?> Get_EfCore()
    {
        await using var context = new BenchContext(_connection);
        return await context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == _existingId);
    }
}

[MemoryDiagnoser]
public class BatchBenchmarks
{
    private SqliteConnection _connection = null!;

    [Params(100, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _connection = BenchDatabase.Open();

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    private List<Customer> NewCustomers() =>
        Enumerable.Range(0, Rows).Select(i => new Customer { Name = "n" + i, Email = i + "@example.com" }).ToList();

    [Benchmark(Baseline = true, Description = "Dapper Execute(list) — no keys back (what Contrib/Dommel do)")]
    public async Task<int> Dapper_ExecuteList()
    {
        await using var tx = await _connection.BeginTransactionAsync();
        var count = await _connection.ExecuteAsync("INSERT INTO bench_customer (Name, Email) VALUES (@Name, @Email)", NewCustomers(), tx);
        await tx.CommitAsync();
        return count;
    }

    [Benchmark(Description = "Writeback InsertManyAsync — keys + defaults back")]
    public Task<int> Writeback_InsertMany() => _connection.InsertManyAsync(NewCustomers());

    [Benchmark(Description = "EF Core AddRange + SaveChanges — keys + defaults back")]
    public async Task<int> EfCore_AddRange()
    {
        await using var context = new BenchContext(_connection);
        context.Customers.AddRange(NewCustomers());
        return await context.SaveChangesAsync();
    }
}

internal static class BenchDatabase
{
    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.Execute("""
            CREATE TABLE bench_customer (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)
            """);
        return connection;
    }
}

/// <summary>Bisects the cost difference between raw Dapper and GetAsync: SQL text vs parameter object vs library code.</summary>
[MemoryDiagnoser]
public class GetBreakdownBenchmarks
{
    private const string RawSql = "SELECT Id, Name, Email, CreatedAt FROM bench_customer WHERE Id = @Id";
    private SqliteConnection _connection = null!;
    private string _librarySql = null!;
    private long _id;

    [GlobalSetup]
    public void Setup()
    {
        _connection = BenchDatabase.Open();
        _id = _connection.ExecuteScalar<long>("INSERT INTO bench_customer (Name, Email) VALUES ('seed', 'seed@example.com') RETURNING Id");
        _librarySql = _connection.Sql<Customer>().SelectByKey;
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark(Baseline = true)]
    public Task<Customer?> RawSql_AnonymousParam() => _connection.QueryFirstOrDefaultAsync<Customer>(RawSql, new { Id = _id });

    [Benchmark]
    public Task<Customer?> LibrarySql_AnonymousParam() => _connection.QueryFirstOrDefaultAsync<Customer>(_librarySql, new { Id = _id });

    [Benchmark]
    public Task<Customer?> RawSql_DynamicParameters()
    {
        var p = new DynamicParameters();
        p.Add("Id", _id);
        return _connection.QueryFirstOrDefaultAsync<Customer>(RawSql, p);
    }

    [Benchmark]
    public Task<Customer?> GetAsync() => _connection.GetAsync<Customer>(_id);
}
