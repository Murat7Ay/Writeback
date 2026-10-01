using System;
using System.Collections.Generic;
using System.Linq;

namespace Writeback.Mapping;

/// <summary>
/// Immutable, cached description of how an entity type maps to a table. Built once per type from fluent configuration,
/// DataAnnotations attributes and conventions (in that order of precedence).
/// </summary>
public sealed class EntityMap
{
    private readonly Dictionary<string, ColumnMap> _byProperty;

    internal EntityMap(Type entityType, string tableName, string? schema, IReadOnlyList<ColumnMap> columns, bool hasTriggers)
    {
        EntityType = entityType;
        TableName = tableName;
        Schema = schema;
        Columns = columns;
        HasTriggers = hasTriggers;
        _byProperty = columns.ToDictionary(c => c.PropertyName, StringComparer.Ordinal);

        Keys = columns.Where(c => c.IsKey).ToArray();
        ConcurrencyToken = columns.FirstOrDefault(c => c.ConcurrencyToken != Mapping.ConcurrencyToken.None);
        InsertColumns = columns.Where(c => c.IsInsertable).ToArray();
        UpdateColumns = columns.Where(c => c.IsUpdatable).ToArray();
        InsertReadback = columns.Where(c => c.Generation != ValueGeneration.None).ToArray();
        UpdateReadback = columns.Where(c => c.Generation == ValueGeneration.OnInsertAndUpdate).ToArray();
        Identity = Keys.Count == 1 && Keys[0].IsIdentity ? Keys[0] : null;
    }

    /// <summary>The mapped CLR type.</summary>
    public Type EntityType { get; }

    /// <summary>Unquoted table name.</summary>
    public string TableName { get; }

    /// <summary>Unquoted schema name, or <see langword="null"/> for the connection default.</summary>
    public string? Schema { get; }

    /// <summary>
    /// The table has triggers. On SQL Server this switches generated-value read-back from <c>OUTPUT</c>
    /// (which SQL Server rejects on tables with triggers) to a trigger-safe re-select.
    /// </summary>
    public bool HasTriggers { get; }

    /// <summary>All mapped columns in property declaration order.</summary>
    public IReadOnlyList<ColumnMap> Columns { get; }

    /// <summary>Primary key columns (empty for keyless entities, which support insert and select only).</summary>
    public IReadOnlyList<ColumnMap> Keys { get; }

    /// <summary>The database-generated single key, if any.</summary>
    public ColumnMap? Identity { get; }

    /// <summary>The optimistic concurrency token, if any.</summary>
    public ColumnMap? ConcurrencyToken { get; }

    /// <summary>Columns written by INSERT.</summary>
    public IReadOnlyList<ColumnMap> InsertColumns { get; }

    /// <summary>Columns written by a full UPDATE.</summary>
    public IReadOnlyList<ColumnMap> UpdateColumns { get; }

    /// <summary>Columns read back into the entity after INSERT.</summary>
    public IReadOnlyList<ColumnMap> InsertReadback { get; }

    /// <summary>Columns read back into the entity after UPDATE.</summary>
    public IReadOnlyList<ColumnMap> UpdateReadback { get; }

    /// <summary>Finds a column by CLR property name (ordinal comparison).</summary>
    public ColumnMap? FindByProperty(string propertyName) =>
        _byProperty.TryGetValue(propertyName, out var column) ? column : null;

    internal void EnsureHasKey(string operation)
    {
        if (Keys.Count == 0)
        {
            throw new EntityMappingException(EntityType, new[]
            {
                $"{operation} requires a primary key but none was found. Mark the key property with [Key], name it 'Id' or "
                + $"'{EntityType.Name}Id', or configure it with options.Entity<{EntityType.Name}>(e => e.HasKey(...)).",
            });
        }
    }
}
