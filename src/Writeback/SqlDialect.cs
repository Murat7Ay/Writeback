using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Writeback.Dialects;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback;

/// <summary>
/// Renders statements for one database engine. Dialects are pure SQL text: they reference no ADO.NET provider, so all
/// built-in dialects live in the core package. Override the small capability members to add another database; override
/// the rendering methods only where its syntax genuinely differs.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Microsoft SQL Server and Azure SQL.</summary>
    public static SqlDialect SqlServer { get; } = new SqlServerDialect();

    /// <summary>PostgreSQL.</summary>
    public static SqlDialect PostgreSql { get; } = new PostgreSqlDialect();

    /// <summary>MySQL 8.0+ (generated values are read back with <c>LAST_INSERT_ID()</c>).</summary>
    public static SqlDialect MySql { get; } = new MySqlDialect();

    /// <summary>SQLite 3.35+ (uses <c>RETURNING</c>).</summary>
    public static SqlDialect Sqlite { get; } = new SqliteDialect();

    /// <summary>Display name, e.g. "PostgreSQL".</summary>
    public abstract string Name { get; }

    /// <summary>Parameter placeholder prefix.</summary>
    public virtual string ParameterPrefix => "@";

    /// <summary>Maximum number of parameters in one command; batches are chunked to stay below it.</summary>
    public abstract int MaxParameters { get; }

    /// <summary>Maximum rows in one multi-row statement (SQL Server's VALUES limit is 1000).</summary>
    public virtual int MaxRowsPerStatement => 1000;

    /// <summary>
    /// Whether batches that need one statement per row are sent as one multi-statement command (saving round trips).
    /// In-process engines (SQLite) return <see langword="false"/>: round trips are free there, and re-executing the cached
    /// single-row statement is faster than one large command.
    /// </summary>
    public virtual bool BatchesStatements => true;

    /// <summary>Quotes one identifier, escaping embedded quote characters.</summary>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>The quoted, schema-qualified table name of <paramref name="entity"/>.</summary>
    public string QuoteTable(EntityMap entity) =>
        entity.Schema is null ? QuoteIdentifier(entity.TableName) : QuoteIdentifier(entity.Schema) + "." + QuoteIdentifier(entity.TableName);

    /// <summary>How generated values are returned for <paramref name="entity"/>.</summary>
    protected abstract ReadbackMethod GetReadbackMethod(EntityMap entity);

    /// <summary>For <see cref="ReadbackMethod.Reselect"/>: SQL returning the identity just generated in this scope.</summary>
    protected virtual string IdentityFunction =>
        throw new NotSupportedException($"{Name} does not define an identity function.");

    /// <summary>For <see cref="ReadbackMethod.Reselect"/>: SQL returning the previous statement's row count.</summary>
    protected virtual string RowCountFunction =>
        throw new NotSupportedException($"{Name} does not define a row-count function.");

    /// <summary>
    /// The provider's affected-row count cannot be trusted for this table (SQL Server counts rows written by triggers),
    /// so statements select the row count explicitly.
    /// </summary>
    protected virtual bool RequiresScalarRowCount(EntityMap entity) => false;

    /// <summary>Converts a provider exception into an actionable one, or returns <see langword="null"/>.</summary>
    public virtual Exception? TranslateException(DbException exception, EntityMap entity) => null;

    /// <summary>Renders an INSERT of one or more rows.</summary>
    public virtual CommandPlan Insert(InsertStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var sql = new SqlBuilder(this);
        var readback = statement.Readback;
        if (statement.Rows.Count == 1)
        {
            AppendInsertRow(sql, statement.Entity, statement.Columns, statement.Rows[0], readback);
            return new CommandPlan(sql.ToString(), readback.Count > 0 ? StatementOutcome.ReturnedRow : StatementOutcome.AffectedRows, readback);
        }

        if (readback.Count == 0 && statement.Columns.Count > 0)
        {
            sql.Append("INSERT INTO ").Table(statement.Entity).Append(" (").ColumnList(statement.Columns).Append(") VALUES ");
            for (var row = 0; row < statement.Rows.Count; row++)
            {
                sql.Append(row == 0 ? "(" : ", (").ParameterList(statement.Rows[row]).Append(")");
            }

            sql.Append(";");
            return new CommandPlan(sql.ToString(), StatementOutcome.AffectedRows, readback);
        }

        // Generated values per row: one statement per row in a single command (one round trip). Correlation is exact
        // because each row gets its own result set, instead of relying on the (unguaranteed) order of multi-row RETURNING.
        for (var row = 0; row < statement.Rows.Count; row++)
        {
            if (row > 0)
            {
                sql.Append("\n");
            }

            AppendInsertRow(sql, statement.Entity, statement.Columns, statement.Rows[row], readback);
        }

        return new CommandPlan(sql.ToString(), readback.Count > 0 ? StatementOutcome.ResultSetPerRow : StatementOutcome.AffectedRows, readback);
    }

    /// <summary>Renders an UPDATE of one row.</summary>
    public virtual CommandPlan Update(UpdateStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var entity = statement.Entity;
        var needRow = statement.Readback.Count > 0 || statement.RequireRowSignal;
        var returned = statement.Readback.Count > 0 ? statement.Readback : entity.Keys;
        var method = needRow ? GetReadbackMethod(entity) : ReadbackMethod.Returning;

        var sql = new SqlBuilder(this);
        sql.Append("UPDATE ").Table(entity).Append(" SET ");
        AppendAssignments(sql, statement.Set, statement.Increment);
        if (needRow && method == ReadbackMethod.Output)
        {
            sql.Append(" OUTPUT ").ColumnList(returned, "INSERTED");
        }

        sql.Append(" WHERE ").Predicates(statement.Where);

        if (!needRow)
        {
            sql.Append(";");
            if (RequiresScalarRowCount(entity))
            {
                sql.Append("\nSELECT ").Append(RowCountFunction).Append(";");
                return new CommandPlan(sql.ToString(), StatementOutcome.ScalarRowCount, Array.Empty<ColumnMap>());
            }

            return new CommandPlan(sql.ToString(), StatementOutcome.AffectedRows, Array.Empty<ColumnMap>());
        }

        switch (method)
        {
            case ReadbackMethod.Returning:
                sql.Append(" RETURNING ").ColumnList(returned).Append(";");
                break;
            case ReadbackMethod.Output:
                sql.Append(";");
                break;
            default:
                var keyPredicates = statement.Where.Where(w => w.Column.IsKey).ToArray();
                sql.Append(";\nSELECT ").ColumnList(returned).Append(" FROM ").Table(entity)
                    .Append(" WHERE ").Append(RowCountFunction).Append(" = 1 AND ").Predicates(keyPredicates).Append(";");
                break;
        }

        return new CommandPlan(sql.ToString(), StatementOutcome.ReturnedRow, returned);
    }

    /// <summary>Renders a DELETE of the rows matching <paramref name="filter"/>.</summary>
    public virtual CommandPlan Delete(EntityMap entity, RowFilter filter)
    {
        var sql = new SqlBuilder(this);
        sql.Append("DELETE FROM ").Table(entity).Append(" WHERE ").Filter(filter).Append(";");
        if (RequiresScalarRowCount(entity))
        {
            sql.Append("\nSELECT ").Append(RowCountFunction).Append(";");
            return new CommandPlan(sql.ToString(), StatementOutcome.ScalarRowCount, Array.Empty<ColumnMap>());
        }

        return new CommandPlan(sql.ToString(), StatementOutcome.AffectedRows, Array.Empty<ColumnMap>());
    }

    /// <summary>
    /// The select list for <paramref name="entity"/>, aliasing renamed columns back to property names so Dapper
    /// materializes them: <c>"full_name" AS "Name"</c>. Optionally qualified by a table alias for joins.
    /// </summary>
    public virtual string SelectList(EntityMap entity, string? tableAlias = null)
    {
        var sql = new SqlBuilder(this);
        var qualifier = tableAlias is null ? null : QuoteIdentifier(tableAlias);
        for (var i = 0; i < entity.Columns.Count; i++)
        {
            var column = entity.Columns[i];
            if (i > 0)
            {
                sql.Append(", ");
            }

            if (qualifier is null)
            {
                sql.Column(column);
            }
            else
            {
                sql.Column(qualifier, column);
            }

            if (column.NeedsAlias)
            {
                sql.Append(" AS ").Identifier(column.PropertyName);
            }
        }

        return sql.ToString();
    }

    /// <summary><c>SELECT &lt;list&gt; FROM &lt;table&gt;</c> without a trailing terminator, ready for a caller's clause.</summary>
    public virtual string SelectFrom(EntityMap entity) => "SELECT " + SelectList(entity) + " FROM " + QuoteTable(entity);

    /// <summary>Renders a SELECT of the rows matching <paramref name="filter"/>.</summary>
    public virtual string SelectWhere(EntityMap entity, RowFilter filter) =>
        new SqlBuilder(this).Append(SelectFrom(entity)).Append(" WHERE ").Filter(filter).Append(";").ToString();

    /// <summary>Renders a query returning a boolean-convertible value telling whether a matching row exists.</summary>
    public virtual string Exists(EntityMap entity, RowFilter filter) =>
        new SqlBuilder(this).Append("SELECT EXISTS (SELECT 1 FROM ").Table(entity).Append(" WHERE ").Filter(filter).Append(");").ToString();

    /// <summary><c>SELECT COUNT(*) FROM &lt;table&gt;</c> without a terminator.</summary>
    public virtual string CountFrom(EntityMap entity) => "SELECT COUNT(*) FROM " + QuoteTable(entity);

    /// <summary>Appends one single-row INSERT statement including its read-back mechanism.</summary>
    protected virtual void AppendInsertRow(SqlBuilder sql, EntityMap entity, IReadOnlyList<ColumnMap> columns, IReadOnlyList<string> parameters, IReadOnlyList<ColumnMap> readback)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var method = readback.Count > 0 ? GetReadbackMethod(entity) : ReadbackMethod.Returning;
        if (method == ReadbackMethod.Reselect)
        {
            EnsureReselectable(entity, columns);
        }

        sql.Append("INSERT INTO ").Table(entity);
        if (columns.Count > 0)
        {
            sql.Append(" (").ColumnList(columns).Append(")");
        }

        if (readback.Count > 0 && method == ReadbackMethod.Output)
        {
            sql.Append(" OUTPUT ").ColumnList(readback, "INSERTED");
        }

        if (columns.Count > 0)
        {
            sql.Append(" VALUES (").ParameterList(parameters).Append(")");
        }
        else
        {
            sql.Append(" ").Append(DefaultValuesClause);
        }

        if (readback.Count == 0)
        {
            sql.Append(";");
            return;
        }

        switch (method)
        {
            case ReadbackMethod.Returning:
                sql.Append(" RETURNING ").ColumnList(readback).Append(";");
                break;
            case ReadbackMethod.Output:
                sql.Append(";");
                break;
            default:
                sql.Append(";\nSELECT ").ColumnList(readback).Append(" FROM ").Table(entity)
                    .Append(" WHERE ").Append(RowCountFunction).Append(" = 1 AND ");
                if (entity.Identity is { } identity)
                {
                    sql.Column(identity).Append(" = ").Append(IdentityFunction);
                }
                else
                {
                    var predicates = entity.Keys.Select(k => new ColumnValue(k, parameters[IndexOf(columns, k)])).ToArray();
                    sql.Predicates(predicates);
                }

                sql.Append(";");
                break;
        }
    }

    /// <summary>The clause inserting a row of defaults when no column is written.</summary>
    protected virtual string DefaultValuesClause => "DEFAULT VALUES";

    /// <summary>Appends the SET list.</summary>
    protected static void AppendAssignments(SqlBuilder sql, IReadOnlyList<ColumnValue> set, ColumnMap? increment)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(set);
        for (var i = 0; i < set.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Column(set[i].Column).Append(" = ").Parameter(set[i].Parameter);
        }

        if (increment is not null)
        {
            if (set.Count > 0)
            {
                sql.Append(", ");
            }

            sql.Column(increment).Append(" = ").Column(increment).Append(" + 1");
        }
    }

    private void EnsureReselectable(EntityMap entity, IReadOnlyList<ColumnMap> columns)
    {
        if (entity.Identity is not null)
        {
            return;
        }

        if (entity.Keys.Count > 0 && entity.Keys.All(k => columns.Contains(k)))
        {
            return;
        }

        var reason = entity.Keys.Count == 0
            ? "the entity has no key to find the inserted row by"
            : "its key is generated by the database but is not an identity/auto-increment column";
        throw new WritebackException(
            $"Cannot read back database-generated values of '{entity.EntityType.Name}' on {Name}{(entity.HasTriggers ? " (table has triggers)" : "")}: "
            + $"{reason}. Use an identity key, or generate the key in the application (e.g. Guid.CreateVersion7()) and map it with "
            + "[DatabaseGenerated(DatabaseGeneratedOption.None)].");
    }

    private static int IndexOf(IReadOnlyList<ColumnMap> columns, ColumnMap column)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (ReferenceEquals(columns[i], column))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
