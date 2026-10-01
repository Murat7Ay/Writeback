using System;
using System.Linq.Expressions;
using Writeback.Execution;
using Writeback.Mapping;

namespace Writeback;

/// <summary>Entry point for <see cref="EntitySql{T}"/> without a connection.</summary>
public static class EntitySql
{
    /// <summary>
    /// Renders the mapping of <typeparamref name="T"/> for an explicit dialect, e.g. to build SQL constants or inspect statements in tests.
    /// </summary>
    public static EntitySql<T> For<T>(SqlDialect dialect) where T : class
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return new EntitySql<T>(WritebackConfig.Model.GetCommands(typeof(T), dialect));
    }
}

/// <summary>
/// The mapping of <typeparamref name="T"/> rendered for one dialect: fragments for hand-written SQL and the exact
/// statements Writeback executes. Fragments are quoted and alias renamed columns, so
/// <c>$"SELECT {sql.Columns} FROM {sql.Table} WHERE {sql.Column(c =&gt; c.Email)} = @email"</c> materializes correctly
/// with plain Dapper even when columns are renamed or snake_cased.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EntitySql<T> where T : class
{
    private readonly EntityCommands _commands;

    internal EntitySql(EntityCommands commands)
    {
        _commands = commands;
    }

    /// <summary>The mapping metadata.</summary>
    public EntityMap Map => _commands.Map;

    /// <summary>The dialect fragments are rendered for.</summary>
    public SqlDialect Dialect => _commands.Dialect;

    /// <summary>Quoted, schema-qualified table name, e.g. <c>"sales"."customer"</c>.</summary>
    public string Table => Dialect.QuoteTable(Map);

    /// <summary>Select list of all mapped columns, aliased to property names where they differ.</summary>
    public string Columns => Dialect.SelectList(Map);

    /// <summary>Select list qualified by a table alias, for joins: <c>c."full_name" AS "Name"</c> (alias quoted).</summary>
    public string ColumnsOf(string tableAlias) => Dialect.SelectList(Map, tableAlias);

    /// <summary>Quoted column name of a property: <c>Column(c =&gt; c.Name)</c> → <c>"full_name"</c>.</summary>
    public string Column(Expression<Func<T, object?>> property)
    {
        var names = MemberSelector.GetPropertyNames(property, nameof(property));
        if (names.Count != 1)
        {
            throw new ArgumentException("Select exactly one property.", nameof(property));
        }

        return Column(names[0]);
    }

    /// <summary>Quoted column name of a property by name.</summary>
    public string Column(string propertyName)
    {
        var column = Map.FindByProperty(propertyName)
                     ?? throw new ArgumentException($"'{propertyName}' is not a mapped property of '{typeof(T).Name}'.", nameof(propertyName));
        return Dialect.QuoteIdentifier(column.ColumnName);
    }

    /// <summary>The statement run by <c>InsertAsync</c>.</summary>
    public string Insert => _commands.Insert.Sql;

    /// <summary>The statement run by <c>UpdateAsync</c> (all columns).</summary>
    public string Update => _commands.Update.Sql;

    /// <summary>The statement run by <c>DeleteAsync</c>.</summary>
    public string Delete => _commands.Delete.Sql;

    /// <summary>The statement run by <c>GetAsync</c>.</summary>
    public string SelectByKey => _commands.SelectByKey;

    /// <summary>The prefix used by <c>SelectAsync</c>: <c>SELECT &lt;columns&gt; FROM &lt;table&gt;</c>.</summary>
    public string SelectFrom => _commands.SelectFrom;

    /// <inheritdoc />
    public override string ToString() => $"{typeof(T).Name} on {Dialect.Name}: {Table}";
}
