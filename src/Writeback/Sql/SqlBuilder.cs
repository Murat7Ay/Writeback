using System.Collections.Generic;
using System.Text;
using Writeback.Mapping;

namespace Writeback.Sql;

/// <summary>
/// Minimal SQL writer used by dialects. Every identifier goes through <see cref="SqlDialect.QuoteIdentifier"/> and every
/// value is a parameter placeholder, so no code path can splice a value or an unquoted name into SQL.
/// </summary>
public sealed class SqlBuilder
{
    private readonly StringBuilder _text = new(256);

    /// <summary>Creates a builder for <paramref name="dialect"/>.</summary>
    public SqlBuilder(SqlDialect dialect)
    {
        Dialect = dialect;
    }

    /// <summary>The dialect identifiers and placeholders are rendered for.</summary>
    public SqlDialect Dialect { get; }

    /// <summary>Appends SQL keywords/punctuation. Never pass names or values here.</summary>
    public SqlBuilder Append(string sql)
    {
        _text.Append(sql);
        return this;
    }

    /// <summary>Appends an integer literal (used for batch row positions).</summary>
    public SqlBuilder Append(int value)
    {
        _text.Append(value);
        return this;
    }

    /// <summary>Appends a quoted identifier.</summary>
    public SqlBuilder Identifier(string name)
    {
        _text.Append(Dialect.QuoteIdentifier(name));
        return this;
    }

    /// <summary>Appends a quoted column name.</summary>
    public SqlBuilder Column(ColumnMap column) => Identifier(column.ColumnName);

    /// <summary>Appends a quoted column name qualified by a quoted alias/table.</summary>
    public SqlBuilder Column(string qualifier, ColumnMap column) => Append(qualifier).Append(".").Column(column);

    /// <summary>Appends the quoted, schema-qualified table name.</summary>
    public SqlBuilder Table(EntityMap entity)
    {
        _text.Append(Dialect.QuoteTable(entity));
        return this;
    }

    /// <summary>Appends a parameter placeholder.</summary>
    public SqlBuilder Parameter(string name)
    {
        _text.Append(Dialect.ParameterPrefix).Append(name);
        return this;
    }

    /// <summary>Appends <c>"a", "b", "c"</c>.</summary>
    public SqlBuilder ColumnList(IReadOnlyList<ColumnMap> columns, string? qualifier = null)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                _text.Append(", ");
            }

            if (qualifier is not null)
            {
                Column(qualifier, columns[i]);
            }
            else
            {
                Column(columns[i]);
            }
        }

        return this;
    }

    /// <summary>Appends <c>@a, @b, @c</c>.</summary>
    public SqlBuilder ParameterList(IReadOnlyList<string> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                _text.Append(", ");
            }

            Parameter(parameters[i]);
        }

        return this;
    }

    /// <summary>Appends <c>"a" = @a AND "b" = @b</c>.</summary>
    public SqlBuilder Predicates(IReadOnlyList<ColumnValue> predicates)
    {
        for (var i = 0; i < predicates.Count; i++)
        {
            if (i > 0)
            {
                _text.Append(" AND ");
            }

            Column(predicates[i].Column).Append(" = ").Parameter(predicates[i].Parameter);
        }

        return this;
    }

    /// <summary>Appends a <see cref="RowFilter"/> (no leading WHERE).</summary>
    public SqlBuilder Filter(RowFilter filter)
    {
        var groups = filter.AnyOf;
        if (groups.Count == 1)
        {
            return Predicates(groups[0]);
        }

        if (IsSingleColumnInList(groups))
        {
            Column(groups[0][0].Column).Append(" IN (");
            for (var i = 0; i < groups.Count; i++)
            {
                if (i > 0)
                {
                    _text.Append(", ");
                }

                Parameter(groups[i][0].Parameter);
            }

            return Append(")");
        }

        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                _text.Append(" OR ");
            }

            Append("(").Predicates(groups[i]).Append(")");
        }

        return this;
    }

    /// <inheritdoc />
    public override string ToString() => _text.ToString();

    private static bool IsSingleColumnInList(IReadOnlyList<IReadOnlyList<ColumnValue>> groups)
    {
        var first = groups[0];
        if (first.Count != 1)
        {
            return false;
        }

        for (var i = 1; i < groups.Count; i++)
        {
            if (groups[i].Count != 1 || !ReferenceEquals(groups[i][0].Column, first[0].Column))
            {
                return false;
            }
        }

        return true;
    }
}
