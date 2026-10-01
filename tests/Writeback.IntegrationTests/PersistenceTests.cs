using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Writeback.IntegrationTests;

/// <summary>
/// The behavioral contract, run unchanged against every database. Tests use unique data instead of cleaning tables,
/// so they never depend on each other.
/// </summary>
public abstract class PersistenceTests<TFixture> : IClassFixture<TFixture>
    where TFixture : DatabaseFixture
{
    protected PersistenceTests(TFixture fixture)
    {
        Fixture = fixture;
    }

    protected TFixture Fixture { get; }

    protected static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    protected DbConnection Open()
    {
        Fixture.SkipIfUnavailable();
        return Fixture.CreateConnection();
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    // ------------------------------------------------------------------ insert / generated values

    [Fact]
    public async Task Insert_reads_back_identity_default_and_computed_values()
    {
        await using var db = Open();
        var customer = new Customer { Name = Unique("ada"), Email = "ada@example.com" };

        await db.InsertAsync(customer, cancellationToken: Ct);

        Assert.True(customer.Id > 0);
        Assert.NotEqual(default, customer.CreatedAt);
        Assert.Equal(customer.Name.ToUpperInvariant(), customer.NameUpper);

        var loaded = await db.GetAsync<Customer>(customer.Id, cancellationToken: Ct);
        Assert.NotNull(loaded);
        Assert.Equal(customer.Name, loaded.Name);
        Assert.Equal(customer.Email, loaded.Email);
        Assert.Equal(CustomerStatus.Active, loaded.Status);
        Assert.Equal(customer.NameUpper, loaded.NameUpper);
    }

    [Fact]
    public async Task Insert_works_on_an_already_open_connection_inside_a_transaction()
    {
        await using var db = Open();
        await db.OpenAsync(Ct);
        await using (var tx = await db.BeginTransactionAsync(Ct))
        {
            var customer = new Customer { Name = Unique("rolled-back") };
            await db.InsertAsync(customer, tx, Ct);
            Assert.True(await db.ExistsAsync<Customer>(customer.Id, tx, Ct));
            await tx.RollbackAsync(Ct);
            Assert.False(await db.ExistsAsync<Customer>(customer.Id, cancellationToken: Ct));
        }
    }

    [Fact]
    public async Task Insert_into_keyless_table()
    {
        await using var db = Open();
        var message = Unique("audit");
        await db.InsertAsync(new AuditEntry { Message = message, Level = 3 }, cancellationToken: Ct);

        var rows = await db.SelectAsync<AuditEntry>(Where<AuditEntry>(db, nameof(AuditEntry.Message)), new { v = message }, cancellationToken: Ct);
        Assert.Equal(3, Assert.Single(rows).Level);
    }

    [Fact]
    public async Task Records_with_init_properties_round_trip()
    {
        await using var db = Open();
        var record = new ProductRecord(0, Unique("record"));
        await db.InsertAsync(record, cancellationToken: Ct);
        Assert.True(record.Id > 0);

        var loaded = await db.GetAsync<ProductRecord>(record.Id, cancellationToken: Ct);
        Assert.Equal(record, loaded);
    }

    // ------------------------------------------------------------------ update

    [Fact]
    public async Task Update_writes_all_columns_and_reads_back_computed_values()
    {
        await using var db = Open();
        var customer = new Customer { Name = Unique("before") };
        await db.InsertAsync(customer, cancellationToken: Ct);

        customer.Name = Unique("after");
        customer.Status = CustomerStatus.Suspended;
        Assert.True(await db.UpdateAsync(customer, cancellationToken: Ct));

        Assert.Equal(customer.Name.ToUpperInvariant(), customer.NameUpper);
        var loaded = await db.GetAsync<Customer>(customer.Id, cancellationToken: Ct);
        Assert.Equal(customer.Name, loaded!.Name);
        Assert.Equal(CustomerStatus.Suspended, loaded.Status);
    }

    [Fact]
    public async Task Update_never_overwrites_insert_generated_values()
    {
        await using var db = Open();
        var customer = new Customer { Name = Unique("keep-created") };
        await db.InsertAsync(customer, cancellationToken: Ct);
        var createdAt = customer.CreatedAt;

        // An entity built from a DTO, without CreatedAt: a full update must not clobber it.
        var detached = new Customer { Id = customer.Id, Name = customer.Name, Email = "new@example.com" };
        Assert.True(await db.UpdateAsync(detached, cancellationToken: Ct));

        var loaded = await db.GetAsync<Customer>(customer.Id, cancellationToken: Ct);
        Assert.Equal(createdAt, loaded!.CreatedAt);
    }

    [Fact]
    public async Task Update_of_missing_row_returns_false()
    {
        await using var db = Open();
        Assert.False(await db.UpdateAsync(new Customer { Id = long.MaxValue - 7, Name = "ghost" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task Partial_update_changes_only_the_selected_columns()
    {
        await using var db = Open();
        var customer = new Customer { Name = Unique("partial"), Email = "old@example.com" };
        await db.InsertAsync(customer, cancellationToken: Ct);

        customer.Email = "new@example.com";
        customer.Name = "NOT SAVED";
        Assert.True(await db.UpdateAsync(customer, c => new { c.Email }, cancellationToken: Ct));

        var loaded = await db.GetAsync<Customer>(customer.Id, cancellationToken: Ct);
        Assert.Equal("new@example.com", loaded!.Email);
        Assert.NotEqual("NOT SAVED", loaded.Name);
    }

    [Fact]
    public async Task Partial_update_rejects_generated_columns()
    {
        await using var db = Open();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => db.UpdateAsync(new Customer(), c => c.CreatedAt, cancellationToken: Ct));
        Assert.Contains("database-generated", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ optimistic concurrency

    [Fact]
    public async Task Version_counter_increments_and_detects_conflicts()
    {
        await using var db = Open();
        var doc = new VersionedDoc { Id = Guid.NewGuid(), Title = "v1" };
        await db.InsertAsync(doc, cancellationToken: Ct);

        var stale = await db.GetAsync<VersionedDoc>(doc.Id, cancellationToken: Ct);

        doc.Title = "v2";
        Assert.True(await db.UpdateAsync(doc, cancellationToken: Ct));
        Assert.Equal(1, doc.Version);

        stale!.Title = "lost update";
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.UpdateAsync(stale, cancellationToken: Ct));
        Assert.Same(stale, Assert.Single(conflict.Entities));
        Assert.Equal(0, stale.Version);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.DeleteAsync(stale, cancellationToken: Ct));
        Assert.Equal("v2", (await db.GetAsync<VersionedDoc>(doc.Id, cancellationToken: Ct))!.Title);
        Assert.True(await db.DeleteAsync(doc, cancellationToken: Ct));
    }

    // ------------------------------------------------------------------ delete / get / exists

    [Fact]
    public async Task Delete_get_exists_and_delete_by_key()
    {
        await using var db = Open();
        var a = new Customer { Name = Unique("del-a") };
        var b = new Customer { Name = Unique("del-b") };
        await db.InsertAsync(a, cancellationToken: Ct);
        await db.InsertAsync(b, cancellationToken: Ct);

        Assert.True(await db.ExistsAsync<Customer>(a.Id, cancellationToken: Ct));
        Assert.True(await db.DeleteAsync(a, cancellationToken: Ct));
        Assert.False(await db.DeleteAsync(a, cancellationToken: Ct));
        Assert.False(await db.ExistsAsync<Customer>(a.Id, cancellationToken: Ct));
        Assert.Null(await db.GetAsync<Customer>(a.Id, cancellationToken: Ct));

        Assert.True(await db.DeleteByKeyAsync<Customer>(b.Id, cancellationToken: Ct));
        Assert.False(await db.DeleteByKeyAsync<Customer>(b.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task Composite_keys()
    {
        await using var db = Open();
        var orderId = Random.Shared.Next(1, int.MaxValue);
        var line = new OrderLine { OrderId = orderId, LineNumber = 1, Product = "widget", Quantity = 2 };
        await db.InsertAsync(line, cancellationToken: Ct);

        var loaded = await db.GetAsync<OrderLine>(new { OrderId = orderId, LineNumber = 1 }, cancellationToken: Ct);
        Assert.Equal("widget", loaded!.Product);
        Assert.Null(await db.GetAsync<OrderLine>(new { OrderId = orderId, LineNumber = 2 }, cancellationToken: Ct));

        line.Quantity = 5;
        Assert.True(await db.UpdateAsync(line, cancellationToken: Ct));
        Assert.Equal(5, (await db.GetAsync<OrderLine>(line, cancellationToken: Ct))!.Quantity);

        Assert.True(await db.DeleteByKeyAsync<OrderLine>(new { OrderId = orderId, LineNumber = 1 }, cancellationToken: Ct));
    }

    // ------------------------------------------------------------------ batches

    [Fact]
    public async Task InsertMany_correlates_generated_keys_with_their_entities_across_chunks()
    {
        await using var db = Open();
        // Large enough to span several chunks on every database (SQL Server: 2098 params / 3 per row).
        var batch = Unique("bulk");
        var customers = Enumerable.Range(0, 2500)
            .Select(i => new Customer { Name = $"{batch}-{i:D5}", Email = i % 3 == 0 ? null : $"{i}@example.com" })
            .ToList();

        Assert.Equal(2500, await db.InsertManyAsync(customers, cancellationToken: Ct));

        Assert.All(customers, c => Assert.True(c.Id > 0));
        Assert.Equal(customers.Count, customers.Select(c => c.Id).Distinct().Count());
        Assert.All(customers, c => Assert.Equal(c.Name.ToUpperInvariant(), c.NameUpper));

        var stored = (await db.SelectAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct))
            .ToDictionary(c => c.Id);
        Assert.Equal(2500, stored.Count);
        Assert.All(customers, c =>
        {
            // the id written back into each entity points at that entity's row
            Assert.Equal(c.Name, stored[c.Id].Name);
            Assert.Equal(c.Email, stored[c.Id].Email);
        });
    }

    [Fact]
    public async Task InsertMany_without_generated_values_uses_multi_row_insert()
    {
        await using var db = Open();
        var orderId = Random.Shared.Next(1, int.MaxValue);
        var lines = Enumerable.Range(1, 1200).Select(i => new OrderLine { OrderId = orderId, LineNumber = i, Product = "p" + i, Quantity = i }).ToList();

        Assert.Equal(1200, await db.InsertManyAsync(lines, cancellationToken: Ct));
        Assert.Equal(1200, await db.CountAsync<OrderLine>(Where<OrderLine>(db, nameof(OrderLine.OrderId)), new { v = orderId }, cancellationToken: Ct));
    }

    [Fact]
    public async Task InsertMany_is_all_or_nothing_and_leaves_entities_untouched_on_failure()
    {
        await using var db = Open();
        var batch = Unique("atomic");
        var customers = Enumerable.Range(0, 1500).Select(i => new Customer { Name = $"{batch}-{i}" }).ToList();
        customers[1400].Name = null!; // NOT NULL violation in a late chunk

        await Assert.ThrowsAnyAsync<DbException>(() => db.InsertManyAsync(customers, cancellationToken: Ct));

        Assert.All(customers, c => Assert.Equal(0, c.Id));
        Assert.Equal(0, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task InsertMany_enlists_in_the_callers_transaction()
    {
        await using var db = Open();
        await db.OpenAsync(Ct);
        var batch = Unique("caller-tx");
        await using (var tx = await db.BeginTransactionAsync(Ct))
        {
            await db.InsertManyAsync(Enumerable.Range(0, 10).Select(i => new Customer { Name = $"{batch}-{i}" }), tx, Ct);
            await tx.RollbackAsync(Ct);
        }

        Assert.Equal(0, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task UpdateMany_reads_back_values_and_reports_updated_rows()
    {
        await using var db = Open();
        var batch = Unique("upd-many");
        var customers = Enumerable.Range(0, 30).Select(i => new Customer { Name = $"{batch}-{i}" }).ToList();
        await db.InsertManyAsync(customers, cancellationToken: Ct);

        foreach (var c in customers)
        {
            c.Name += "-x";
        }

        // A missing row in the middle must not shift the read-back values of the rows after it.
        var ghost = new Customer { Id = long.MaxValue - 3, Name = "ghost" };
        var withGhost = customers.Take(15).Append(ghost).Concat(customers.Skip(15)).ToList();
        Assert.Equal(30, await db.UpdateManyAsync(withGhost, cancellationToken: Ct));
        Assert.Null(ghost.NameUpper);
        Assert.All(customers, c => Assert.Equal(c.Name.ToUpperInvariant(), c.NameUpper));
    }

    [Fact]
    public async Task UpdateMany_conflict_rolls_back_everything()
    {
        await using var db = Open();
        var docs = Enumerable.Range(0, 5).Select(i => new VersionedDoc { Id = Guid.NewGuid(), Title = "t" + i }).ToList();
        await db.InsertManyAsync(docs, cancellationToken: Ct);

        var concurrent = await db.GetAsync<VersionedDoc>(docs[3].Id, cancellationToken: Ct);
        concurrent!.Title = "someone else";
        await db.UpdateAsync(concurrent, cancellationToken: Ct);

        foreach (var d in docs)
        {
            d.Title += "-mine";
        }

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.UpdateManyAsync(docs, cancellationToken: Ct));
        Assert.Same(docs[3], Assert.Single(conflict.Entities));
        Assert.All(docs, d => Assert.Equal(0, d.Version));
        Assert.Equal("t0", (await db.GetAsync<VersionedDoc>(docs[0].Id, cancellationToken: Ct))!.Title);
    }

    [Fact]
    public async Task DeleteMany_and_GetMany()
    {
        await using var db = Open();
        var batch = Unique("del-many");
        var customers = Enumerable.Range(0, 25).Select(i => new Customer { Name = $"{batch}-{i}" }).ToList();
        await db.InsertManyAsync(customers, cancellationToken: Ct);

        var some = await db.GetManyAsync<Customer>(customers.Take(10).Select(c => c.Id).Append(long.MaxValue - 11), cancellationToken: Ct);
        Assert.Equal(customers.Take(10).Select(c => c.Id).OrderBy(i => i), some.Select(c => c.Id).OrderBy(i => i));

        Assert.Equal(25, await db.DeleteManyAsync(customers, cancellationToken: Ct));
        Assert.Equal(0, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task DeleteMany_with_stale_version_throws_and_deletes_nothing()
    {
        await using var db = Open();
        var docs = Enumerable.Range(0, 3).Select(i => new VersionedDoc { Id = Guid.NewGuid(), Title = "d" + i }).ToList();
        await db.InsertManyAsync(docs, cancellationToken: Ct);
        var fresh = await db.GetAsync<VersionedDoc>(docs[1].Id, cancellationToken: Ct);
        await db.UpdateAsync(fresh!, cancellationToken: Ct);

        await db.OpenAsync(Ct);
        await using var tx = await db.BeginTransactionAsync(Ct);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.DeleteManyAsync(docs, tx, Ct));
        await tx.RollbackAsync(Ct);
        Assert.Equal(3, (await db.GetManyAsync<VersionedDoc>(docs.Select(d => d.Id), cancellationToken: Ct)).Count);
    }

    // ------------------------------------------------------------------ querying and SQL fragments

    [Fact]
    public async Task Select_count_and_streaming_with_caller_clause()
    {
        await using var db = Open();
        var batch = Unique("select");
        await db.InsertManyAsync(Enumerable.Range(0, 7).Select(i => new Customer { Name = $"{batch}-{i}" }), cancellationToken: Ct);

        var sql = db.Sql<Customer>();
        var clause = $"WHERE {sql.Column(c => c.Name)} LIKE @v ORDER BY {sql.Column(c => c.Name)} DESC";
        var rows = await db.SelectAsync<Customer>(clause, new { v = batch + "%" }, cancellationToken: Ct);
        Assert.Equal($"{batch}-6", rows[0].Name);
        Assert.Equal(7, rows.Count);
        Assert.Equal(7, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));

        var streamed = new List<Customer>();
        await foreach (var c in db.SelectUnbufferedAsync<Customer>(clause, new { v = batch + "%" }, cancellationToken: Ct))
        {
            streamed.Add(c);
        }

        Assert.Equal(rows.Select(r => r.Id), streamed.Select(r => r.Id));
    }

    [Fact]
    public async Task Fluently_renamed_columns_round_trip_and_fragments_work_with_plain_Dapper()
    {
        await using var db = Open();
        var product = new FluentProduct { Name = Unique("fluent"), Price = 12.5m };
        await db.InsertAsync(product, cancellationToken: Ct);
        Assert.True(product.Id > 0);

        var loaded = await db.GetAsync<FluentProduct>(product.Id, cancellationToken: Ct);
        Assert.Equal(product.Name, loaded!.Name);
        Assert.Equal(12.5m, loaded.Price);

        // Hand-written SQL with plain Dapper, using mapping-aware fragments.
        var sql = db.Sql<FluentProduct>();
        var viaDapper = await db.QuerySingleAsync<FluentProduct>(
            $"SELECT {sql.Columns} FROM {sql.Table} WHERE {sql.Column(p => p.Name)} = @name", new { name = product.Name });
        Assert.Equal(product.Id, viaDapper.Id);
        Assert.Equal(12.5m, viaDapper.Price);
    }

    [Fact]
    public async Task Reserved_words_are_safe_as_table_and_column_names()
    {
        await using var db = Open();
        var entity = new KeywordEntity { Select = Unique("kw"), From = "here" };
        await db.InsertAsync(entity, cancellationToken: Ct);
        entity.From = "there";
        await db.UpdateAsync(entity, cancellationToken: Ct);

        var loaded = await db.GetAsync<KeywordEntity>(entity.Id, cancellationToken: Ct);
        Assert.Equal("there", loaded!.From);
        Assert.True(await db.DeleteAsync(entity, cancellationToken: Ct));
    }

    protected static string Where<T>(DbConnection db, string property) where T : class =>
        $"WHERE {db.Sql<T>().Column(property)} = @v";

    protected static string Like<T>(DbConnection db, string property) where T : class =>
        $"WHERE {db.Sql<T>().Column(property)} LIKE @v";
}

public sealed class SqliteTests(SqliteFixture fixture) : PersistenceTests<SqliteFixture>(fixture);

public sealed class PostgreSqlTests(PostgreSqlFixture fixture) : PersistenceTests<PostgreSqlFixture>(fixture)
{
    [Fact]
    public async Task BulkCopy_streams_rows_with_binary_copy()
    {
        await using var db = (Npgsql.NpgsqlConnection)Open();
        var batch = "copy-" + Guid.NewGuid().ToString("N")[..10];
        var rows = Enumerable.Range(0, 20_000).Select(i => new Customer { Name = $"{batch}-{i}", Status = CustomerStatus.Suspended, Email = i % 2 == 0 ? null : "x" });

        Assert.Equal(20_000UL, await PostgreSql.PostgreSqlBulkExtensions.BulkCopyAsync(db, rows, Ct));
        Assert.Equal(20_000, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));
        var sample = (await db.SelectAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)) + " ORDER BY \"Id\" LIMIT 1", new { v = batch + "%" }, cancellationToken: Ct))[0];
        Assert.Equal(CustomerStatus.Suspended, sample.Status);
        Assert.Equal(sample.Name.ToUpperInvariant(), sample.NameUpper);
    }
}

public sealed class MySqlTests(MySqlFixture fixture) : PersistenceTests<MySqlFixture>(fixture);

public sealed class SqlServerTests(SqlServerFixture fixture) : PersistenceTests<SqlServerFixture>(fixture)
{
    [Fact]
    public async Task Tables_with_triggers_use_trigger_safe_readback_and_see_trigger_written_values()
    {
        await using var db = Open();
        var row = new Triggered { Name = "t1" };
        await db.InsertAsync(row, cancellationToken: Ct);

        Assert.True(row.Id > 0);
        Assert.True(row.ModifiedAt > new DateTime(2001, 1, 1)); // set by the AFTER trigger, invisible to OUTPUT

        var before = row.ModifiedAt;
        row.Name = "t2";
        Assert.True(await db.UpdateAsync(row, cancellationToken: Ct));
        Assert.True(row.ModifiedAt >= before);

        // the trigger writes two audit rows per change, yet affected-row counting stays exact
        Assert.True(await db.DeleteAsync(row, cancellationToken: Ct));
        Assert.False(await db.DeleteAsync(row, cancellationToken: Ct));

        var many = Enumerable.Range(0, 50).Select(i => new Triggered { Name = "m" + i }).ToList();
        await db.InsertManyAsync(many, cancellationToken: Ct);
        Assert.Equal(50, many.Select(m => m.Id).Distinct().Count());
        Assert.Equal(50, await db.UpdateManyAsync(many, cancellationToken: Ct));
        Assert.Equal(50, await db.DeleteManyAsync(many, cancellationToken: Ct));
    }

    [Fact]
    public async Task Undeclared_triggers_produce_an_actionable_error()
    {
        await using var db = Open();
        var ex = await Assert.ThrowsAsync<WritebackException>(() => db.InsertAsync(new TriggeredWithoutDeclaration { Name = "x" }, cancellationToken: Ct));
        Assert.Contains("[HasTriggers]", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }

    [Fact]
    public async Task BulkCopy_streams_rows_with_SqlBulkCopy_inside_a_transaction()
    {
        await using var db = (Microsoft.Data.SqlClient.SqlConnection)Open();
        await db.OpenAsync(Ct);
        var batch = "copy-" + Guid.NewGuid().ToString("N")[..10];
        var rows = Enumerable.Range(0, 20_000).Select(i => new Customer { Name = $"{batch}-{i}", Status = CustomerStatus.Suspended });

        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await db.BeginTransactionAsync(Ct))
        {
            Assert.Equal(20_000, await SqlServer.SqlServerBulkExtensions.BulkCopyAsync(db, rows, tx, cancellationToken: Ct));
            Assert.Equal(20_000, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, tx, Ct));
            await tx.RollbackAsync(Ct);
        }

        Assert.Equal(0, await db.CountAsync<Customer>(Like<Customer>(db, nameof(Customer.Name)), new { v = batch + "%" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task Rowversion_is_read_back_and_enforced()
    {
        await using var db = Open();
        var row = new RowVersioned { Name = "a" };
        await db.InsertAsync(row, cancellationToken: Ct);
        Assert.NotNull(row.RowVersion);

        var stale = await db.GetAsync<RowVersioned>(row.Id, cancellationToken: Ct);
        var original = row.RowVersion;
        row.Name = "b";
        Assert.True(await db.UpdateAsync(row, cancellationToken: Ct));
        Assert.NotEqual(original, row.RowVersion);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.UpdateAsync(stale!, cancellationToken: Ct));
        Assert.True(await db.DeleteAsync(row, cancellationToken: Ct));
    }
}
