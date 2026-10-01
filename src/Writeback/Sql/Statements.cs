using System;
using System.Collections.Generic;
using Writeback.Mapping;

namespace Writeback.Sql;

/// <summary>A column bound to a parameter name (without prefix).</summary>
public readonly record struct ColumnValue(ColumnMap Column, string Parameter);

/// <summary>
/// INSERT of one or more rows. <see cref="Rows"/> holds, per row, the parameter names aligned with <see cref="Columns"/>.
/// </summary>
public sealed record InsertStatement(
    EntityMap Entity,
    IReadOnlyList<ColumnMap> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<ColumnMap> Readback);

/// <summary>UPDATE of exactly one row identified by <see cref="Where"/>.</summary>
/// <param name="Entity">The entity map.</param>
/// <param name="Set">Columns assigned from parameters.</param>
/// <param name="Increment">A version counter assigned <c>column + 1</c>, if any.</param>
/// <param name="Where">Key (and concurrency token) predicates, combined with AND.</param>
/// <param name="Readback">Columns to return after the update.</param>
/// <param name="RequireRowSignal">
/// Produce a result row on success even when nothing needs reading back, so batched updates can tell which rows matched.
/// </param>
public sealed record UpdateStatement(
    EntityMap Entity,
    IReadOnlyList<ColumnValue> Set,
    ColumnMap? Increment,
    IReadOnlyList<ColumnValue> Where,
    IReadOnlyList<ColumnMap> Readback,
    bool RequireRowSignal);

/// <summary>
/// A row filter: the OR of AND-groups. One group per entity/key. Rendered as <c>col IN (...)</c> when every group
/// is a single predicate on the same column.
/// </summary>
public sealed record RowFilter(IReadOnlyList<IReadOnlyList<ColumnValue>> AnyOf)
{
    /// <summary>A filter matching a single row.</summary>
    public static RowFilter ForRow(IReadOnlyList<ColumnValue> predicates) => new(new[] { predicates });
}

/// <summary>How the executor learns what a rendered statement did.</summary>
public enum StatementOutcome
{
    /// <summary>Use the provider's affected-row count.</summary>
    AffectedRows,

    /// <summary>The statement ends with a SELECT of the row count (used when triggers distort the provider's count).</summary>
    ScalarRowCount,

    /// <summary>One result set; it contains one row (the read-back columns) if and only if the row was written.</summary>
    ReturnedRow,

    /// <summary>One result set per input row, in input order, each with one row on success.</summary>
    ResultSetPerRow,

    /// <summary>One result set whose rows end with an extra integer column holding the zero-based input row position.</summary>
    PositionedRows,
}

/// <summary>A rendered statement plus the information needed to interpret its results.</summary>
public sealed class CommandPlan
{
    /// <summary>Creates a plan.</summary>
    public CommandPlan(string sql, StatementOutcome outcome, IReadOnlyList<ColumnMap> readback)
    {
        Sql = sql;
        Outcome = outcome;
        Readback = readback;
    }

    /// <summary>The SQL text. Contains only quoted identifiers and parameter placeholders, never values.</summary>
    public string Sql { get; }

    /// <summary>How to interpret the results.</summary>
    public StatementOutcome Outcome { get; }

    /// <summary>Columns present (in order) in returned rows.</summary>
    public IReadOnlyList<ColumnMap> Readback { get; }

    /// <inheritdoc />
    public override string ToString() => Sql;
}

/// <summary>How a dialect returns database-generated values from a write.</summary>
public enum ReadbackMethod
{
    /// <summary><c>INSERT ... RETURNING</c> (PostgreSQL, SQLite 3.35+, MariaDB 10.5+).</summary>
    Returning,

    /// <summary><c>INSERT ... OUTPUT INSERTED.*</c> (SQL Server without triggers).</summary>
    Output,

    /// <summary>
    /// Write, then <c>SELECT ... WHERE &lt;row count&gt; = 1 AND key = &lt;identity function or key parameter&gt;</c>
    /// in the same batch (MySQL; SQL Server tables with triggers).
    /// </summary>
    Reselect,
}
