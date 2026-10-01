using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback.Execution;

/// <summary>
/// Every statement for one entity on one dialect, rendered once and cached. Single-row statements bind parameters by
/// property name, so the entity instance itself is the Dapper parameter object (Dapper's cached IL binder, type handlers included).
/// Batch statements use positional names <c>p0..pN</c>.
/// </summary>
internal sealed class EntityCommands
{
    private readonly Lazy<CommandPlan> _insert;
    private readonly Lazy<CommandPlan> _update;
    private readonly Lazy<CommandPlan> _delete;
    private readonly Lazy<CommandPlan> _deleteByKey;
    private readonly Lazy<string> _selectByKey;
    private readonly Lazy<string> _exists;
    private readonly Lazy<IReadOnlyList<ColumnMap>> _updateParameters;
    private readonly ConcurrentDictionary<string, CommandPlan> _partialUpdates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(BatchKind Kind, int Rows), CommandPlan> _fullBatches = new();

    public EntityCommands(EntityMap map, SqlDialect dialect)
    {
        Map = map;
        Dialect = dialect;
        SelectFrom = dialect.SelectFrom(map);
        CountFrom = dialect.CountFrom(map);
        _insert = new Lazy<CommandPlan>(() => dialect.Insert(new InsertStatement(
            map, map.InsertColumns, new[] { map.InsertColumns.Select(c => c.PropertyName).ToArray() }, map.InsertReadback)));
        _update = new Lazy<CommandPlan>(() => BuildUpdate(map.UpdateColumns, requireRowSignal: false, parameterOf: c => c.PropertyName));
        _delete = new Lazy<CommandPlan>(() => dialect.Delete(map, RowFilter.ForRow(KeyAndTokenPredicates(c => c.PropertyName))));
        _deleteByKey = new Lazy<CommandPlan>(() => dialect.Delete(map, RowFilter.ForRow(KeyPredicates())));
        _selectByKey = new Lazy<string>(() => dialect.SelectWhere(map, RowFilter.ForRow(KeyPredicates())));
        _exists = new Lazy<string>(() => dialect.Exists(map, RowFilter.ForRow(KeyPredicates())));
        _updateParameters = new Lazy<IReadOnlyList<ColumnMap>>(() =>
            map.UpdateColumns.Concat(map.Keys).Concat(map.ConcurrencyToken is { } t ? new[] { t } : Array.Empty<ColumnMap>()).ToArray());
    }

    private enum BatchKind
    {
        Insert,
        Update,
        Delete,
        Select,
    }

    public EntityMap Map { get; }

    /// <summary>Columns whose values the single-row INSERT binds (by property name).</summary>
    public IReadOnlyList<ColumnMap> InsertParameters => Map.InsertColumns;

    /// <summary>Columns whose values the full single-row UPDATE binds (by property name).</summary>
    public IReadOnlyList<ColumnMap> UpdateParameters => _updateParameters.Value;

    public SqlDialect Dialect { get; }

    public string SelectFrom { get; }

    public string CountFrom { get; }

    public CommandPlan Insert => _insert.Value;

    public CommandPlan Update
    {
        get
        {
            Map.EnsureHasKey("UpdateAsync");
            return _update.Value;
        }
    }

    public CommandPlan Delete
    {
        get
        {
            Map.EnsureHasKey("DeleteAsync");
            return _delete.Value;
        }
    }

    public CommandPlan DeleteByKey
    {
        get
        {
            Map.EnsureHasKey("DeleteByKeyAsync");
            return _deleteByKey.Value;
        }
    }

    public string SelectByKey
    {
        get
        {
            Map.EnsureHasKey("GetAsync");
            return _selectByKey.Value;
        }
    }

    public string Exists
    {
        get
        {
            Map.EnsureHasKey("ExistsAsync");
            return _exists.Value;
        }
    }

    /// <summary>Update of an explicit column subset (cached per subset).</summary>
    public CommandPlan PartialUpdate(IReadOnlyList<ColumnMap> columns)
    {
        Map.EnsureHasKey("UpdateAsync");
        var cacheKey = string.Join(",", columns.Select(c => c.Ordinal));
        return _partialUpdates.GetOrAdd(cacheKey, _ => BuildUpdate(columns, requireRowSignal: false, parameterOf: c => c.PropertyName));
    }

    /// <summary>Parameters per row for batched inserts.</summary>
    public int InsertWidth => Map.InsertColumns.Count;

    /// <summary>Parameters per row for batched updates.</summary>
    public int UpdateWidth => Map.UpdateColumns.Count + Map.Keys.Count + (Map.ConcurrencyToken is null ? 0 : 1);

    /// <summary>Parameters per row for batched deletes/selects.</summary>
    public int DeleteWidth => Map.Keys.Count + (Map.ConcurrencyToken is null ? 0 : 1);

    public int KeyWidth => Map.Keys.Count;

    public CommandPlan InsertBatch(int rows, bool cache) => Batch(BatchKind.Insert, rows, cache, () =>
    {
        var width = InsertWidth;
        var parameterRows = new IReadOnlyList<string>[rows];
        for (var r = 0; r < rows; r++)
        {
            var names = new string[width];
            for (var c = 0; c < width; c++)
            {
                names[c] = ParameterName(r, width, c);
            }

            parameterRows[r] = names;
        }

        return Dialect.Insert(new InsertStatement(Map, Map.InsertColumns, parameterRows, Map.InsertReadback));
    });

    public CommandPlan UpdateBatch(int rows, bool cache) => Batch(BatchKind.Update, rows, cache, () =>
    {
        var builder = new System.Text.StringBuilder();
        IReadOnlyList<ColumnMap> readback = Array.Empty<ColumnMap>();
        for (var r = 0; r < rows; r++)
        {
            var row = r;
            var setColumns = Map.UpdateColumns;
            var plan = BuildUpdate(setColumns, requireRowSignal: true, parameterOf: c => ParameterName(row, UpdateWidth, UpdateSlot(c)));
            readback = plan.Readback;
            if (r > 0)
            {
                builder.Append('\n');
            }

            builder.Append(plan.Sql);
        }

        return new CommandPlan(builder.ToString(), StatementOutcome.ResultSetPerRow, readback);
    });

    public CommandPlan DeleteBatch(int rows, bool cache) => Batch(BatchKind.Delete, rows, cache, () =>
    {
        var groups = new IReadOnlyList<ColumnValue>[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = r;
            groups[r] = KeyAndTokenPredicates(c => ParameterName(row, DeleteWidth, DeleteSlot(c)));
        }

        return Dialect.Delete(Map, new RowFilter(groups));
    });

    public CommandPlan SelectBatch(int rows, bool cache) => Batch(BatchKind.Select, rows, cache, () =>
    {
        var groups = new IReadOnlyList<ColumnValue>[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = r;
            groups[r] = Map.Keys.Select((k, i) => new ColumnValue(k, ParameterName(row, KeyWidth, i))).ToArray();
        }

        return new CommandPlan(Dialect.SelectWhere(Map, new RowFilter(groups)), StatementOutcome.ReturnedRow, Array.Empty<ColumnMap>());
    });

    /// <summary>Slot of a column within one row of an update batch: SET columns, then keys, then the token.</summary>
    public int UpdateSlot(ColumnMap column)
    {
        var set = Map.UpdateColumns;
        for (var i = 0; i < set.Count; i++)
        {
            if (ReferenceEquals(set[i], column))
            {
                return i;
            }
        }

        return set.Count + DeleteSlot(column);
    }

    /// <summary>Slot of a column within one row of a delete batch: keys, then the token.</summary>
    public int DeleteSlot(ColumnMap column)
    {
        var keys = Map.Keys;
        for (var i = 0; i < keys.Count; i++)
        {
            if (ReferenceEquals(keys[i], column))
            {
                return i;
            }
        }

        if (ReferenceEquals(Map.ConcurrencyToken, column))
        {
            return keys.Count;
        }

        throw new InvalidOperationException($"Column {column} has no slot.");
    }

    public static string ParameterName(int row, int width, int slot) => "p" + ((row * width) + slot).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Only the full-size chunk is cached: remainder chunks vary in size and would grow the cache without bound.
    private CommandPlan Batch(BatchKind kind, int rows, bool cache, Func<CommandPlan> build) =>
        cache ? _fullBatches.GetOrAdd((kind, rows), _ => build()) : build();

    private CommandPlan BuildUpdate(IReadOnlyList<ColumnMap> setColumns, bool requireRowSignal, Func<ColumnMap, string> parameterOf)
    {
        var token = Map.ConcurrencyToken;
        var increment = token?.ConcurrencyToken == ConcurrencyToken.VersionCounter ? token : null;
        if (setColumns.Count == 0 && increment is null)
        {
            throw new WritebackException(
                $"'{Map.EntityType.Name}' has no updatable columns: every mapped column is a key, generated, insert-only or read-only.");
        }

        var set = setColumns.Select(c => new ColumnValue(c, parameterOf(c))).ToArray();
        var where = KeyAndTokenPredicates(parameterOf);
        return Dialect.Update(new UpdateStatement(Map, set, increment, where, Map.UpdateReadback, requireRowSignal));
    }

    private ColumnValue[] KeyPredicates() => Map.Keys.Select(k => new ColumnValue(k, k.PropertyName)).ToArray();

    private ColumnValue[] KeyAndTokenPredicates(Func<ColumnMap, string> parameterOf)
    {
        var predicates = Map.Keys.Select(k => new ColumnValue(k, parameterOf(k))).ToList();
        if (Map.ConcurrencyToken is { } token)
        {
            predicates.Add(new ColumnValue(token, parameterOf(token)));
        }

        return predicates.ToArray();
    }
}
