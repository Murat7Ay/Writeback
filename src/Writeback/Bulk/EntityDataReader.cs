using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using Writeback.Mapping;

namespace Writeback.Bulk;

/// <summary>
/// Streams entities as a forward-only <see cref="DbDataReader"/> over the given mapped columns, for provider bulk-load
/// APIs (<c>SqlBulkCopy</c>, <c>MySqlBulkCopy</c>, ...). Entities are read lazily, one row at a time; nothing is buffered.
/// Enum values are exposed as their underlying integer. Dapper type handlers do not apply (bulk protocols bypass parameters).
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "DbDataReader's IEnumerable contract is non-generic.")]
public sealed class EntityDataReader<T> : DbDataReader where T : class
{
    private readonly IEnumerator<T> _entities;
    private readonly IReadOnlyList<ColumnMap> _columns;
    private readonly Dictionary<string, int> _ordinals;
    private T? _current;
    private bool _closed;

    /// <summary>Creates a reader over <paramref name="entities"/> exposing <paramref name="columns"/> in order.</summary>
    public EntityDataReader(IEnumerable<T> entities, IReadOnlyList<ColumnMap> columns)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(columns);
        _entities = entities.GetEnumerator();
        _columns = columns;
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            _ordinals[columns[i].ColumnName] = i;
        }
    }

    /// <summary>Rows read so far.</summary>
    public long RowsRead { get; private set; }

    /// <inheritdoc />
    public override int FieldCount => _columns.Count;

    /// <inheritdoc />
    public override bool HasRows => true;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <inheritdoc />
    public override int RecordsAffected => -1;

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override bool Read()
    {
        if (_closed || !_entities.MoveNext())
        {
            _current = null;
            return false;
        }

        _current = _entities.Current ?? throw new InvalidOperationException($"Row {RowsRead} is null.");
        RowsRead++;
        return true;
    }

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        var current = _current ?? throw new InvalidOperationException("No current row; call Read() first.");
        var value = _columns[ordinal].Getter(current);
        return value switch
        {
            null => DBNull.Value,
            Enum e => Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), System.Globalization.CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    /// <inheritdoc />
    public override string GetName(int ordinal) => _columns[ordinal].ColumnName;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "IDataRecord.GetOrdinal is documented to throw IndexOutOfRangeException.")]
    public override int GetOrdinal(string name) =>
        _ordinals.TryGetValue(name, out var ordinal) ? ordinal : throw new IndexOutOfRangeException($"No column named '{name}'.");

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal)
    {
        var type = Nullable.GetUnderlyingType(_columns[ordinal].Property.PropertyType) ?? _columns[ordinal].Property.PropertyType;
        return type.IsEnum ? Enum.GetUnderlyingType(type) : type;
    }

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null)
        {
            return bytes.Length;
        }

        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <inheritdoc />
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = (string)GetValue(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }

        var count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    /// <inheritdoc />
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Close()
    {
        _closed = true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entities.Dispose();
            _closed = true;
        }

        base.Dispose(disposing);
    }
}
