using System;
using System.Data;
using System.Reflection;

namespace Writeback.Mapping;

/// <summary>When the database, not the application, produces a column's value.</summary>
public enum ValueGeneration
{
    /// <summary>The application supplies the value on insert and update.</summary>
    None,

    /// <summary>
    /// The database produces the value on insert (identity, sequence default, <c>DEFAULT now()</c>...).
    /// The column is never written by Writeback and is read back into the entity after insert.
    /// </summary>
    OnInsert,

    /// <summary>
    /// The database produces the value on insert and on update (computed columns, rowversion, trigger-maintained values).
    /// Never written; read back after insert and after update.
    /// </summary>
    OnInsertAndUpdate,
}

/// <summary>How a column takes part in optimistic concurrency.</summary>
public enum ConcurrencyToken
{
    /// <summary>Not a concurrency token.</summary>
    None,

    /// <summary>
    /// A database-maintained token such as SQL Server <c>rowversion</c> (<c>[Timestamp]</c>). Compared on update/delete
    /// and read back after every write.
    /// </summary>
    RowVersion,

    /// <summary>
    /// An integer version counter maintained by Writeback (<c>[ConcurrencyCheck]</c> on an integer property):
    /// updates compare the current value and write <c>version + 1</c>. Portable to every database, no triggers required.
    /// </summary>
    VersionCounter,
}

/// <summary>Immutable description of how one property maps to one column.</summary>
public sealed class ColumnMap
{
    internal ColumnMap(
        PropertyInfo property,
        string columnName,
        int ordinal,
        bool isKey,
        ValueGeneration generation,
        ConcurrencyToken concurrencyToken,
        bool isInsertOnly,
        bool isReadOnly)
    {
        Property = property;
        PropertyName = property.Name;
        ColumnName = columnName;
        Ordinal = ordinal;
        IsKey = isKey;
        Generation = generation;
        ConcurrencyToken = concurrencyToken;
        IsInsertOnly = isInsertOnly;
        IsReadOnly = isReadOnly;
        Getter = Accessors.CreateGetter(property);
        Setter = Accessors.CreateSetter(property);
        DbTypeForNull = TypeSupport.GetDbTypeForNull(property.PropertyType);
        UsesTypeHandler = TypeSupport.HasTypeHandler(property.PropertyType);
    }

    /// <summary>The mapped CLR property.</summary>
    public PropertyInfo Property { get; }

    /// <summary>The property name; also the parameter name used in single-row statements.</summary>
    public string PropertyName { get; }

    /// <summary>The unquoted database column name.</summary>
    public string ColumnName { get; }

    /// <summary>Position of the column within <see cref="EntityMap.Columns"/>.</summary>
    public int Ordinal { get; }

    /// <summary>Part of the primary key.</summary>
    public bool IsKey { get; }

    /// <summary>Whether and when the database generates the value.</summary>
    public ValueGeneration Generation { get; }

    /// <summary>Optimistic concurrency role.</summary>
    public ConcurrencyToken ConcurrencyToken { get; }

    /// <summary>Written on insert, never on update (e.g. <c>CreatedBy</c>).</summary>
    public bool IsInsertOnly { get; }

    /// <summary>Selected but never written and never read back (maintained by something outside the application).</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// A database-generated integer key (identity / auto-increment / rowid): the only kind of generated key that
    /// <c>scope_identity()</c> / <c>LAST_INSERT_ID()</c> can identify. A database-generated Guid key is not an identity.
    /// </summary>
    public bool IsIdentity => IsKey && Generation == ValueGeneration.OnInsert && TypeSupport.IsInteger(Property.PropertyType);

    /// <summary>Included in the column list of INSERT statements.</summary>
    public bool IsInsertable => Generation == ValueGeneration.None && !IsReadOnly;

    /// <summary>Included in the SET list of full UPDATE statements (version counters are handled separately).</summary>
    public bool IsUpdatable =>
        !IsKey && Generation == ValueGeneration.None && !IsInsertOnly && !IsReadOnly
        && ConcurrencyToken == ConcurrencyToken.None;

    /// <summary>The column name differs from the property name, so SELECT lists must alias it.</summary>
    public bool NeedsAlias => !string.Equals(ColumnName, PropertyName, StringComparison.Ordinal);

    internal Func<object, object?> Getter { get; }

    internal Action<object, object?>? Setter { get; }

    internal DbType? DbTypeForNull { get; }

    internal bool UsesTypeHandler { get; }

    /// <inheritdoc />
    public override string ToString() => $"{PropertyName} -> {ColumnName}";
}
