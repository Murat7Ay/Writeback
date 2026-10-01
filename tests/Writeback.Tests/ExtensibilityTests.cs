using System;
using System.Collections.Generic;
using Writeback.Execution;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback.Tests;

/// <summary>The example from docs/architecture/ARCHITECTURE.md §7, compiled and asserted so the docs cannot rot.</summary>
public sealed class MariaDbDialect : SqlDialect
{
    public override string Name => "MariaDB";

    public override int MaxParameters => 65535;

    public override string QuoteIdentifier(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    protected override ReadbackMethod GetReadbackMethod(EntityMap entity) => ReadbackMethod.Reselect;

    protected override string IdentityFunction => "LAST_INSERT_ID()";

    protected override string RowCountFunction => "ROW_COUNT()";

    protected override string DefaultValuesClause => "() VALUES ()";

    protected override void AppendInsertRow(SqlBuilder sql, EntityMap entity, IReadOnlyList<ColumnMap> columns,
        IReadOnlyList<string> parameters, IReadOnlyList<ColumnMap> readback)
    {
        sql.Append("INSERT INTO ").Table(entity).Append(" (").ColumnList(columns)
            .Append(") VALUES (").ParameterList(parameters).Append(")");
        if (readback.Count > 0)
        {
            sql.Append(" RETURNING ").ColumnList(readback);
        }

        sql.Append(";");
    }
}

public class ExtensibilityTests
{
    private static EntityCommands Commands<T>(SqlDialect dialect) => new EntityModel(new WritebackOptions()).GetCommands(typeof(T), dialect);

    [Fact]
    public void Custom_dialect_overrides_insert_and_inherits_update_reselect()
    {
        var commands = Commands<Order>(new MariaDbDialect());

        Assert.Equal(
            "INSERT INTO `sales`.`orders` (`customer_id`, `Total`) VALUES (@CustomerId, @Total) RETURNING `OrderNumber`, `RowVersion`;",
            commands.Insert.Sql);
        Assert.Equal(StatementOutcome.ReturnedRow, commands.Insert.Outcome);
        Assert.EndsWith("SELECT `RowVersion` FROM `sales`.`orders` WHERE ROW_COUNT() = 1 AND `OrderNumber` = @OrderNumber;", commands.Update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_dialect_batches_reuse_the_row_hook()
    {
        var plan = Commands<Customer>(new MariaDbDialect()).InsertBatch(2, cache: false);
        Assert.Equal(StatementOutcome.ResultSetPerRow, plan.Outcome);
        Assert.Equal(2, plan.Sql.Split("RETURNING").Length - 1);
    }
}

public class ValueConverterTests
{
    private static readonly EntityModel Model = new(new WritebackOptions());

    private static ColumnMap Column<T>(string property) => Model.GetMap(typeof(T)).FindByProperty(property)!;

    [Fact]
    public void Converts_provider_types_to_property_types()
    {
        Assert.Equal(42L, ValueConverter.ConvertTo(42m, Column<Customer>("Id")));                         // SQL Server scope_identity() is decimal
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0), ValueConverter.ConvertTo("2026-10-01 12:00:00", Column<Customer>("CreatedAt"))); // SQLite TEXT
        var guid = Guid.NewGuid();
        Assert.Equal(guid, ValueConverter.ConvertTo(guid.ToString(), Column<GuidKeyed>("Id")));
        Assert.Equal(guid, ValueConverter.ConvertTo(guid.ToByteArray(), Column<GuidKeyed>("Id")));
        Assert.Equal(Status.Archived, ValueConverter.ConvertTo(1L, Column<WithEnum>("Status")));
        Assert.Equal(Status.Archived, ValueConverter.ConvertTo(1, Column<WithEnum>("PreviousStatus")));
        Assert.Null(ValueConverter.ConvertTo(DBNull.Value, Column<WithEnum>("PreviousStatus")));
    }

    [Fact]
    public void Unconvertible_values_name_the_column_and_types()
    {
        var ex = Assert.Throws<WritebackException>(() => ValueConverter.ConvertTo("not a date", Column<Customer>("CreatedAt")));
        Assert.Contains("'CreatedAt'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("String", ex.Message, StringComparison.Ordinal);
        Assert.Contains("type handler", ex.Message, StringComparison.Ordinal);
    }
}
