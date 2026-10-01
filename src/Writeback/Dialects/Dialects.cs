using System;
using System.Collections.Generic;
using System.Data.Common;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback.Dialects;

internal static class Quoting
{
    public static string Quote(string identifier, char open, char close)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Identifiers must not contain NUL characters.", nameof(identifier));
        }

        var closeText = close.ToString();
        return open + identifier.Replace(closeText, closeText + closeText, StringComparison.Ordinal) + close;
    }
}

/// <summary>
/// SQL Server: <c>OUTPUT INSERTED.*</c> for generated values, <c>MERGE</c> with a position column for batched inserts
/// that need generated keys, and a trigger-safe re-select (<c>@@ROWCOUNT</c> / <c>scope_identity()</c>) for tables with triggers.
/// </summary>
public sealed class SqlServerDialect : SqlDialect
{
    /// <summary>Column carrying the input row position through MERGE ... OUTPUT.</summary>
    internal const string PositionColumn = "__wb_position";

    internal SqlServerDialect()
    {
    }

    /// <inheritdoc />
    public override string Name => "SQL Server";

    /// <inheritdoc />
    public override int MaxParameters => 2098; // hard limit is 2100; sp_executesql needs headroom

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => Quoting.Quote(identifier, '[', ']');

    /// <inheritdoc />
    protected override ReadbackMethod GetReadbackMethod(EntityMap entity) =>
        entity.HasTriggers ? ReadbackMethod.Reselect : ReadbackMethod.Output;

    /// <inheritdoc />
    protected override string IdentityFunction => "scope_identity()";

    /// <inheritdoc />
    protected override string RowCountFunction => "@@ROWCOUNT";

    /// <summary>
    /// Always <see langword="true"/>: the affected-row count SqlClient reports includes rows written by triggers and is
    /// -1 under <c>SET NOCOUNT ON</c>, so statements select <c>@@ROWCOUNT</c> explicitly.
    /// </summary>
    protected override bool RequiresScalarRowCount(EntityMap entity) => true;

    /// <inheritdoc />
    public override CommandPlan Insert(InsertStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.Rows.Count == 1 || statement.Readback.Count == 0 || statement.Entity.HasTriggers)
        {
            return base.Insert(statement);
        }

        // Multi-row OUTPUT order is not guaranteed by SQL Server, so carry each row's position through MERGE
        // (the same technique EF Core uses) and correlate on it.
        var entity = statement.Entity;
        var columns = statement.Columns;
        var sql = new SqlBuilder(this);
        sql.Append("MERGE INTO ").Table(entity).Append(" USING (VALUES ");
        for (var row = 0; row < statement.Rows.Count; row++)
        {
            sql.Append(row == 0 ? "(" : ", (");
            if (columns.Count > 0)
            {
                sql.ParameterList(statement.Rows[row]).Append(", ");
            }

            sql.Append(row).Append(")");
        }

        sql.Append(") AS i (");
        if (columns.Count > 0)
        {
            sql.ColumnList(columns).Append(", ");
        }

        sql.Identifier(PositionColumn).Append(") ON 1 = 0 WHEN NOT MATCHED THEN INSERT ");
        if (columns.Count > 0)
        {
            sql.Append("(").ColumnList(columns).Append(") VALUES (").ColumnList(columns, "i").Append(")");
        }
        else
        {
            sql.Append("DEFAULT VALUES");
        }

        sql.Append(" OUTPUT ").ColumnList(statement.Readback, "INSERTED").Append(", i.").Identifier(PositionColumn).Append(";");
        return new CommandPlan(sql.ToString(), StatementOutcome.PositionedRows, statement.Readback);
    }

    /// <inheritdoc />
    public override string Exists(EntityMap entity, RowFilter filter) =>
        new SqlBuilder(this).Append("SELECT CASE WHEN EXISTS (SELECT 1 FROM ").Table(entity).Append(" WHERE ").Filter(filter)
            .Append(") THEN 1 ELSE 0 END;").ToString();

    /// <inheritdoc />
    public override Exception? TranslateException(DbException exception, EntityMap entity)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(entity);
        // 334: "The target table ... of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause."
        if (GetErrorNumber(exception) == 334)
        {
            return new WritebackException(
                $"Table '{entity.TableName}' has triggers, and SQL Server rejects OUTPUT on such tables. Mark '{entity.EntityType.Name}' "
                + $"with [HasTriggers] or configure options.Entity<{entity.EntityType.Name}>(e => e.HasTriggers()) so generated values are "
                + "read back with a trigger-safe re-select.",
                exception);
        }

        return null;
    }

    private static int? GetErrorNumber(DbException exception) =>
        exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
}

/// <summary>PostgreSQL: <c>RETURNING</c> for generated values; double-quoted identifiers (case-sensitive).</summary>
public sealed class PostgreSqlDialect : SqlDialect
{
    internal PostgreSqlDialect()
    {
    }

    /// <inheritdoc />
    public override string Name => "PostgreSQL";

    /// <inheritdoc />
    public override int MaxParameters => 65535;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => Quoting.Quote(identifier, '"', '"');

    /// <inheritdoc />
    protected override ReadbackMethod GetReadbackMethod(EntityMap entity) => ReadbackMethod.Returning;
}

/// <summary>SQLite 3.35+: <c>RETURNING</c> for generated values.</summary>
public sealed class SqliteDialect : SqlDialect
{
    internal SqliteDialect()
    {
    }

    /// <inheritdoc />
    public override string Name => "SQLite";

    /// <inheritdoc />
    public override int MaxParameters => 32766; // SQLITE_MAX_VARIABLE_NUMBER default since 3.32

    /// <summary>
    /// <see langword="false"/>: SQLite runs in-process, so there is no round trip to save, and Microsoft.Data.Sqlite binds
    /// every parameter of a command to every statement in it (quadratic for multi-statement batches).
    /// </summary>
    public override bool BatchesStatements => false;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => Quoting.Quote(identifier, '"', '"');

    /// <inheritdoc />
    protected override ReadbackMethod GetReadbackMethod(EntityMap entity) => ReadbackMethod.Returning;
}

/// <summary>
/// MySQL 8.0+: no RETURNING, so generated values are re-selected in the same batch with
/// <c>WHERE ROW_COUNT() = 1 AND id = LAST_INSERT_ID()</c>. Requires the default "found rows" affected-row semantics
/// (<c>UseAffectedRows=false</c>, the MySqlConnector and MySql.Data default).
/// </summary>
public sealed class MySqlDialect : SqlDialect
{
    internal MySqlDialect()
    {
    }

    /// <inheritdoc />
    public override string Name => "MySQL";

    /// <inheritdoc />
    public override int MaxParameters => 65535;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => Quoting.Quote(identifier, '`', '`');

    /// <inheritdoc />
    protected override ReadbackMethod GetReadbackMethod(EntityMap entity) => ReadbackMethod.Reselect;

    /// <inheritdoc />
    protected override string IdentityFunction => "LAST_INSERT_ID()";

    /// <inheritdoc />
    protected override string RowCountFunction => "ROW_COUNT()";

    /// <inheritdoc />
    protected override string DefaultValuesClause => "() VALUES ()";

    /// <inheritdoc />
    public override CommandPlan Insert(InsertStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        // MySQL accepts "INSERT INTO t () VALUES (), ()": keep zero-column batches as one statement too.
        if (statement.Rows.Count > 1 && statement.Readback.Count == 0 && statement.Columns.Count == 0)
        {
            var sql = new SqlBuilder(this).Append("INSERT INTO ").Table(statement.Entity).Append(" () VALUES ");
            for (var row = 0; row < statement.Rows.Count; row++)
            {
                sql.Append(row == 0 ? "()" : ", ()");
            }

            return new CommandPlan(sql.Append(";").ToString(), StatementOutcome.AffectedRows, Array.Empty<ColumnMap>());
        }

        return base.Insert(statement);
    }
}
