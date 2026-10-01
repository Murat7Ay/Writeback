using System;
using System.ComponentModel.DataAnnotations.Schema;
using Writeback.Execution;
using Writeback.Sql;

namespace Writeback.Tests;

public class SqlGenerationTests
{
    private static EntityCommands Commands<T>(SqlDialect dialect, Action<WritebackOptions>? configure = null)
    {
        var options = new WritebackOptions();
        configure?.Invoke(options);
        return new EntityModel(options).GetCommands(typeof(T), dialect);
    }

    // ------------------------------------------------------------------ insert

    [Fact]
    public void Insert_with_identity_and_default_sql_server_uses_output()
    {
        var plan = Commands<Customer>(SqlDialect.SqlServer).Insert;
        Assert.Equal(
            "INSERT INTO [Customer] ([Name], [Email]) OUTPUT INSERTED.[Id], INSERTED.[CreatedAt] VALUES (@Name, @Email);",
            plan.Sql);
        Assert.Equal(StatementOutcome.ReturnedRow, plan.Outcome);
    }

    [Fact]
    public void Insert_postgres_and_sqlite_use_returning()
    {
        const string expected = "INSERT INTO \"Customer\" (\"Name\", \"Email\") VALUES (@Name, @Email) RETURNING \"Id\", \"CreatedAt\";";
        Assert.Equal(expected, Commands<Customer>(SqlDialect.PostgreSql).Insert.Sql);
        Assert.Equal(expected, Commands<Customer>(SqlDialect.Sqlite).Insert.Sql);
    }

    [Fact]
    public void Insert_mysql_reselects_by_last_insert_id_in_same_batch()
    {
        Assert.Equal(
            "INSERT INTO `Customer` (`Name`, `Email`) VALUES (@Name, @Email);\n"
            + "SELECT `Id`, `CreatedAt` FROM `Customer` WHERE ROW_COUNT() = 1 AND `Id` = LAST_INSERT_ID();",
            Commands<Customer>(SqlDialect.MySql).Insert.Sql);
    }

    [Fact]
    public void Insert_sql_server_table_with_triggers_avoids_output()
    {
        Assert.Equal(
            "INSERT INTO [Triggered] ([Name]) VALUES (@Name);\n"
            + "SELECT [Id], [ModifiedAt] FROM [Triggered] WHERE @@ROWCOUNT = 1 AND [Id] = scope_identity();",
            Commands<Triggered>(SqlDialect.SqlServer).Insert.Sql);
    }

    [Fact]
    public void Insert_with_schema_renamed_column_and_rowversion()
    {
        Assert.Equal(
            "INSERT INTO [sales].[orders] ([customer_id], [Total]) OUTPUT INSERTED.[OrderNumber], INSERTED.[RowVersion] VALUES (@CustomerId, @Total);",
            Commands<Order>(SqlDialect.SqlServer).Insert.Sql);
    }

    [Fact]
    public void Insert_without_generated_values_has_no_readback()
    {
        var plan = Commands<AuditLog>(SqlDialect.PostgreSql).Insert;
        Assert.Equal("INSERT INTO \"AuditLog\" (\"Message\", \"At\") VALUES (@Message, @At);", plan.Sql);
        Assert.Equal(StatementOutcome.AffectedRows, plan.Outcome);
        Assert.Empty(plan.Readback);
    }

    [Fact]
    public void Insert_client_key_with_computed_column_mysql_reselects_by_key_parameter()
    {
        Assert.Equal(
            "INSERT INTO `Document` (`Id`, `Title`, `CreatedBy`) VALUES (@Id, @Title, @CreatedBy);\n"
            + "SELECT `Slug` FROM `Document` WHERE ROW_COUNT() = 1 AND `Id` = @Id;",
            Commands<Document>(SqlDialect.MySql).Insert.Sql);
    }

    [Theory]
    [InlineData("SqlServer", "INSERT INTO [OnlyGenerated] OUTPUT INSERTED.[Id], INSERTED.[CreatedAt] DEFAULT VALUES;")]
    [InlineData("PostgreSql", "INSERT INTO \"OnlyGenerated\" DEFAULT VALUES RETURNING \"Id\", \"CreatedAt\";")]
    [InlineData("MySql", "INSERT INTO `OnlyGenerated` () VALUES ();\nSELECT `Id`, `CreatedAt` FROM `OnlyGenerated` WHERE ROW_COUNT() = 1 AND `Id` = LAST_INSERT_ID();")]
    public void Insert_with_no_writable_columns_uses_defaults(string dialect, string expected) =>
        Assert.Equal(expected, Commands<OnlyGenerated>(Dialect(dialect)).Insert.Sql);

    [Fact]
    public void Database_generated_non_identity_key_cannot_be_reselected()
    {
        var ex = Assert.Throws<WritebackException>(() => Commands<DbGeneratedGuid>(SqlDialect.MySql).Insert);
        Assert.Contains("Guid.CreateVersion7()", ex.Message, StringComparison.Ordinal);

        // ...but RETURNING/OUTPUT databases handle it.
        Assert.Equal(
            "INSERT INTO \"DbGeneratedGuid\" (\"Name\") VALUES (@Name) RETURNING \"Id\";",
            Commands<DbGeneratedGuid>(SqlDialect.PostgreSql).Insert.Sql);
    }

    // ------------------------------------------------------------------ update

    [Fact]
    public void Update_without_token_uses_affected_rows()
    {
        var plan = Commands<Customer>(SqlDialect.PostgreSql).Update;
        Assert.Equal("UPDATE \"Customer\" SET \"Name\" = @Name, \"Email\" = @Email WHERE \"Id\" = @Id;", plan.Sql);
        Assert.Equal(StatementOutcome.AffectedRows, plan.Outcome);
    }

    [Fact]
    public void Sql_server_never_trusts_the_providers_affected_row_count()
    {
        // NOCOUNT ON reports -1 and triggers inflate the count, so SQL Server always selects @@ROWCOUNT.
        var commands = Commands<Customer>(SqlDialect.SqlServer);
        Assert.Equal("UPDATE [Customer] SET [Name] = @Name, [Email] = @Email WHERE [Id] = @Id;\nSELECT @@ROWCOUNT;", commands.Update.Sql);
        Assert.Equal(StatementOutcome.ScalarRowCount, commands.Update.Outcome);
        Assert.Equal("DELETE FROM [Customer] WHERE [Id] = @Id;\nSELECT @@ROWCOUNT;", commands.Delete.Sql);
    }

    [Fact]
    public void Update_with_rowversion_per_dialect()
    {
        Assert.Equal(
            "UPDATE [sales].[orders] SET [customer_id] = @CustomerId, [Total] = @Total OUTPUT INSERTED.[RowVersion] "
            + "WHERE [OrderNumber] = @OrderNumber AND [RowVersion] = @RowVersion;",
            Commands<Order>(SqlDialect.SqlServer).Update.Sql);
        Assert.Equal(
            "UPDATE \"sales\".\"orders\" SET \"customer_id\" = @CustomerId, \"Total\" = @Total "
            + "WHERE \"OrderNumber\" = @OrderNumber AND \"RowVersion\" = @RowVersion RETURNING \"RowVersion\";",
            Commands<Order>(SqlDialect.PostgreSql).Update.Sql);
        Assert.Equal(
            "UPDATE `sales`.`orders` SET `customer_id` = @CustomerId, `Total` = @Total WHERE `OrderNumber` = @OrderNumber AND `RowVersion` = @RowVersion;\n"
            + "SELECT `RowVersion` FROM `sales`.`orders` WHERE ROW_COUNT() = 1 AND `OrderNumber` = @OrderNumber;",
            Commands<Order>(SqlDialect.MySql).Update.Sql);
    }

    [Fact]
    public void Update_with_version_counter_increments_in_sql()
    {
        var plan = Commands<OrderLine>(SqlDialect.PostgreSql).Update;
        Assert.Equal(
            "UPDATE \"OrderLine\" SET \"Product\" = @Product, \"Version\" = \"Version\" + 1 "
            + "WHERE \"OrderId\" = @OrderId AND \"LineNumber\" = @LineNumber AND \"Version\" = @Version;",
            plan.Sql);
        Assert.Equal(StatementOutcome.AffectedRows, plan.Outcome);
    }

    [Fact]
    public void Update_sql_server_triggers_reselects_and_counts_explicitly()
    {
        var commands = Commands<Triggered>(SqlDialect.SqlServer);
        Assert.Equal(
            "UPDATE [Triggered] SET [Name] = @Name WHERE [Id] = @Id;\n"
            + "SELECT [ModifiedAt] FROM [Triggered] WHERE @@ROWCOUNT = 1 AND [Id] = @Id;",
            commands.Update.Sql);

        var delete = commands.Delete;
        Assert.Equal("DELETE FROM [Triggered] WHERE [Id] = @Id;\nSELECT @@ROWCOUNT;", delete.Sql);
        Assert.Equal(StatementOutcome.ScalarRowCount, delete.Outcome);
    }

    [Fact]
    public void Partial_update_sets_only_selected_columns_and_is_cached()
    {
        var commands = Commands<Customer>(SqlDialect.PostgreSql);
        var email = commands.Map.FindByProperty("Email")!;
        var plan = commands.PartialUpdate(new[] { email });

        Assert.Equal("UPDATE \"Customer\" SET \"Email\" = @Email WHERE \"Id\" = @Id;", plan.Sql);
        Assert.Same(plan, commands.PartialUpdate(new[] { email }));
    }

    [Fact]
    public void Entity_with_only_key_and_generated_columns_cannot_be_updated()
    {
        var ex = Assert.Throws<WritebackException>(() => Commands<OnlyGenerated>(SqlDialect.PostgreSql).Update);
        Assert.Contains("no updatable columns", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ delete / select

    [Fact]
    public void Delete_includes_concurrency_token_but_delete_by_key_does_not()
    {
        var commands = Commands<Order>(SqlDialect.PostgreSql);
        Assert.Equal("DELETE FROM \"sales\".\"orders\" WHERE \"OrderNumber\" = @OrderNumber AND \"RowVersion\" = @RowVersion;", commands.Delete.Sql);
        Assert.Equal("DELETE FROM \"sales\".\"orders\" WHERE \"OrderNumber\" = @OrderNumber;", commands.DeleteByKey.Sql);
    }

    [Fact]
    public void Select_by_key_aliases_renamed_columns()
    {
        Assert.Equal(
            "SELECT [OrderNumber], [customer_id] AS [CustomerId], [Total], [RowVersion] FROM [sales].[orders] WHERE [OrderNumber] = @OrderNumber;",
            Commands<Order>(SqlDialect.SqlServer).SelectByKey);
    }

    [Fact]
    public void Select_by_composite_key()
    {
        Assert.Equal(
            "SELECT \"OrderId\", \"LineNumber\", \"Product\", \"Version\" FROM \"OrderLine\" WHERE \"OrderId\" = @OrderId AND \"LineNumber\" = @LineNumber;",
            Commands<OrderLine>(SqlDialect.Sqlite).SelectByKey);
    }

    [Fact]
    public void Exists_per_dialect()
    {
        Assert.Equal("SELECT CASE WHEN EXISTS (SELECT 1 FROM [Customer] WHERE [Id] = @Id) THEN 1 ELSE 0 END;", Commands<Customer>(SqlDialect.SqlServer).Exists);
        Assert.Equal("SELECT EXISTS (SELECT 1 FROM \"Customer\" WHERE \"Id\" = @Id);", Commands<Customer>(SqlDialect.PostgreSql).Exists);
    }

    [Fact]
    public void Select_and_count_prefixes()
    {
        var commands = Commands<Order>(SqlDialect.MySql);
        Assert.Equal("SELECT `OrderNumber`, `customer_id` AS `CustomerId`, `Total`, `RowVersion` FROM `sales`.`orders`", commands.SelectFrom);
        Assert.Equal("SELECT COUNT(*) FROM `sales`.`orders`", commands.CountFrom);
    }

    // ------------------------------------------------------------------ batches

    [Fact]
    public void Batch_insert_sql_server_with_generated_values_uses_merge_with_positions()
    {
        var plan = Commands<Customer>(SqlDialect.SqlServer).InsertBatch(2, cache: false);
        Assert.Equal(
            "MERGE INTO [Customer] USING (VALUES (@p0, @p1, 0), (@p2, @p3, 1)) AS i ([Name], [Email], [__wb_position]) ON 1 = 0 "
            + "WHEN NOT MATCHED THEN INSERT ([Name], [Email]) VALUES (i.[Name], i.[Email]) "
            + "OUTPUT INSERTED.[Id], INSERTED.[CreatedAt], i.[__wb_position];",
            plan.Sql);
        Assert.Equal(StatementOutcome.PositionedRows, plan.Outcome);
    }

    [Fact]
    public void Batch_insert_returning_databases_use_one_statement_per_row_for_exact_correlation()
    {
        var plan = Commands<Customer>(SqlDialect.PostgreSql).InsertBatch(2, cache: false);
        Assert.Equal(
            "INSERT INTO \"Customer\" (\"Name\", \"Email\") VALUES (@p0, @p1) RETURNING \"Id\", \"CreatedAt\";\n"
            + "INSERT INTO \"Customer\" (\"Name\", \"Email\") VALUES (@p2, @p3) RETURNING \"Id\", \"CreatedAt\";",
            plan.Sql);
        Assert.Equal(StatementOutcome.ResultSetPerRow, plan.Outcome);
    }

    [Fact]
    public void Batch_insert_without_generated_values_is_one_multi_row_statement()
    {
        var plan = Commands<GuidKeyed>(SqlDialect.SqlServer).InsertBatch(3, cache: false);
        Assert.Equal("INSERT INTO [GuidKeyed] ([Id], [Name]) VALUES (@p0, @p1), (@p2, @p3), (@p4, @p5);", plan.Sql);
        Assert.Equal(StatementOutcome.AffectedRows, plan.Outcome);
    }

    [Fact]
    public void Batch_insert_sql_server_with_triggers_falls_back_to_statement_per_row()
    {
        var plan = Commands<Triggered>(SqlDialect.SqlServer).InsertBatch(2, cache: false);
        Assert.Equal(StatementOutcome.ResultSetPerRow, plan.Outcome);
        Assert.DoesNotContain("OUTPUT", plan.Sql, StringComparison.Ordinal);
        Assert.Equal(2, plan.Sql.Split("scope_identity()").Length - 1);
    }

    [Fact]
    public void Batch_update_returns_a_row_per_statement_to_detect_misses()
    {
        var plan = Commands<Customer>(SqlDialect.PostgreSql).UpdateBatch(2, cache: false);
        Assert.Equal(
            "UPDATE \"Customer\" SET \"Name\" = @p0, \"Email\" = @p1 WHERE \"Id\" = @p2 RETURNING \"Id\";\n"
            + "UPDATE \"Customer\" SET \"Name\" = @p3, \"Email\" = @p4 WHERE \"Id\" = @p5 RETURNING \"Id\";",
            plan.Sql);
        Assert.Equal(StatementOutcome.ResultSetPerRow, plan.Outcome);
    }

    [Fact]
    public void Batch_update_sql_server_signals_with_output()
    {
        var plan = Commands<OrderLine>(SqlDialect.SqlServer).UpdateBatch(1, cache: false);
        Assert.Equal(
            "UPDATE [OrderLine] SET [Product] = @p0, [Version] = [Version] + 1 OUTPUT INSERTED.[OrderId], INSERTED.[LineNumber] "
            + "WHERE [OrderId] = @p1 AND [LineNumber] = @p2 AND [Version] = @p3;",
            plan.Sql);
    }

    [Fact]
    public void Batch_delete_single_key_uses_in_list()
    {
        Assert.Equal("DELETE FROM [Customer] WHERE [Id] IN (@p0, @p1, @p2);\nSELECT @@ROWCOUNT;", Commands<Customer>(SqlDialect.SqlServer).DeleteBatch(3, cache: false).Sql);
        Assert.Equal("DELETE FROM `Customer` WHERE `Id` IN (@p0, @p1, @p2);", Commands<Customer>(SqlDialect.MySql).DeleteBatch(3, cache: false).Sql);
    }

    [Fact]
    public void Batch_delete_composite_key_with_token_uses_or_groups()
    {
        Assert.Equal(
            "DELETE FROM \"OrderLine\" WHERE (\"OrderId\" = @p0 AND \"LineNumber\" = @p1 AND \"Version\" = @p2) "
            + "OR (\"OrderId\" = @p3 AND \"LineNumber\" = @p4 AND \"Version\" = @p5);",
            Commands<OrderLine>(SqlDialect.PostgreSql).DeleteBatch(2, cache: false).Sql);
    }

    [Fact]
    public void Full_size_batches_are_cached_remainders_are_not()
    {
        var commands = Commands<Customer>(SqlDialect.PostgreSql);
        Assert.Same(commands.InsertBatch(10, cache: true), commands.InsertBatch(10, cache: true));
        Assert.NotSame(commands.InsertBatch(3, cache: false), commands.InsertBatch(3, cache: false));
    }

    [Fact]
    public void Chunk_size_respects_parameter_limits()
    {
        var model = new EntityModel(new WritebackOptions());
        Assert.Equal(1000, Executor.ChunkSize(model, SqlDialect.PostgreSql, 10));
        Assert.Equal(209, Executor.ChunkSize(model, SqlDialect.SqlServer, 10)); // 2098 / 10
        Assert.Equal(1, Executor.ChunkSize(model, SqlDialect.SqlServer, 5000));

        var small = new EntityModel(new WritebackOptions { MaxBatchSize = 50 });
        Assert.Equal(50, Executor.ChunkSize(small, SqlDialect.PostgreSql, 2));
    }

    // ------------------------------------------------------------------ quoting

    [Fact]
    public void Identifiers_are_always_quoted_and_escaped()
    {
        Assert.Equal("[select], [bad]]name], [bad\"name], [bad`name]", Columns(SqlDialect.SqlServer));
        Assert.Equal("\"select\", \"bad]name\", \"bad\"\"name\", \"bad`name\"", Columns(SqlDialect.PostgreSql));
        Assert.Equal("`select`, `bad]name`, `bad\"name`, `bad``name`", Columns(SqlDialect.MySql));

        static string Columns(SqlDialect dialect)
        {
            var map = new EntityModel(new WritebackOptions()).GetMap(typeof(Weird));
            return new SqlBuilder(dialect).ColumnList(new[] { map.Columns[1], map.Columns[2], map.Columns[3], map.Columns[4] }).ToString();
        }
    }

    [Fact]
    public void Injection_through_names_stays_inside_the_identifier()
    {
        const string hostile = "x]; DROP TABLE users; --";
        Assert.Equal("[x]]; DROP TABLE users; --]", SqlDialect.SqlServer.QuoteIdentifier(hostile));
        Assert.Throws<ArgumentException>(() => SqlDialect.PostgreSql.QuoteIdentifier("a\0b"));
        Assert.Throws<ArgumentException>(() => SqlDialect.PostgreSql.QuoteIdentifier(""));
    }

    [Fact]
    public void Select_list_with_table_alias()
    {
        var map = new EntityModel(new WritebackOptions()).GetMap(typeof(Order));
        Assert.Equal(
            "\"o\".\"OrderNumber\", \"o\".\"customer_id\" AS \"CustomerId\", \"o\".\"Total\", \"o\".\"RowVersion\"",
            SqlDialect.PostgreSql.SelectList(map, "o"));
    }

    private static SqlDialect Dialect(string name) => name switch
    {
        "SqlServer" => SqlDialect.SqlServer,
        "PostgreSql" => SqlDialect.PostgreSql,
        "MySql" => SqlDialect.MySql,
        _ => SqlDialect.Sqlite,
    };
}

public class DbGeneratedGuid
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}
