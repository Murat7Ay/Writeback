using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Writeback.Mapping;

/// <summary>
/// Fluent mapping for one entity type. Use it for types you cannot (or prefer not to) annotate, and for
/// settings attributes cannot express. Fluent settings override attributes, which override conventions.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EntityBuilder<T> where T : class
{
    internal EntityBuilder(EntityConfiguration configuration)
    {
        Configuration = configuration;
    }

    internal EntityConfiguration Configuration { get; }

    /// <summary>Maps the entity to <paramref name="name"/> in <paramref name="schema"/> (or the default schema).</summary>
    public EntityBuilder<T> ToTable(string name, string? schema = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name must not be empty.", nameof(name));
        }

        Configuration.TableName = name;
        Configuration.Schema = schema;
        Configuration.TableConfigured = true;
        return this;
    }

    /// <summary>
    /// Sets the primary key: <c>e.HasKey(x =&gt; x.Id)</c>, or a composite key with
    /// <c>e.HasKey(x =&gt; new { x.OrderId, x.LineNumber })</c>.
    /// </summary>
    public EntityBuilder<T> HasKey<TKey>(Expression<Func<T, TKey>> key)
    {
        Configuration.KeyProperties = MemberSelector.GetPropertyNames(key, nameof(key));
        return this;
    }

    /// <summary>Configures a mapped property.</summary>
    public PropertyBuilder Property<TProperty>(Expression<Func<T, TProperty>> property)
    {
        var names = MemberSelector.GetPropertyNames(property, nameof(property));
        if (names.Count != 1)
        {
            throw new ArgumentException("Property(...) selects exactly one property, e.g. e.Property(x => x.Name).", nameof(property));
        }

        if (!Configuration.Properties.TryGetValue(names[0], out var config))
        {
            config = new PropertyConfiguration();
            Configuration.Properties.Add(names[0], config);
        }

        return new PropertyBuilder(config);
    }

    /// <summary>Excludes a property from persistence (same as <c>[NotMapped]</c>).</summary>
    public EntityBuilder<T> Ignore<TProperty>(Expression<Func<T, TProperty>> property)
    {
        foreach (var name in MemberSelector.GetPropertyNames(property, nameof(property)))
        {
            Configuration.Ignored.Add(name);
        }

        return this;
    }

    /// <summary>
    /// Declares that the table has triggers. SQL Server rejects <c>OUTPUT</c> without <c>INTO</c> on such tables,
    /// so generated values are read back with a trigger-safe re-select instead. Same as <c>[HasTriggers]</c>.
    /// </summary>
    public EntityBuilder<T> HasTriggers(bool hasTriggers = true)
    {
        Configuration.HasTriggers = hasTriggers;
        return this;
    }
}

/// <summary>Fluent configuration for one property.</summary>
public sealed class PropertyBuilder
{
    private readonly PropertyConfiguration _configuration;

    internal PropertyBuilder(PropertyConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>Maps the property to a differently named column (same as <c>[Column("name")]</c>).</summary>
    public PropertyBuilder HasColumnName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Column name must not be empty.", nameof(name));
        }

        _configuration.ColumnName = name;
        return this;
    }

    /// <summary>The database generates the value on insert (identity, defaults). Read back after insert.</summary>
    public PropertyBuilder ValueGeneratedOnInsert()
    {
        _configuration.Generation = ValueGeneration.OnInsert;
        return this;
    }

    /// <summary>The database generates the value on insert and update (computed columns). Read back after every write.</summary>
    public PropertyBuilder ValueGeneratedOnInsertAndUpdate()
    {
        _configuration.Generation = ValueGeneration.OnInsertAndUpdate;
        return this;
    }

    /// <summary>The application always supplies the value (disables the integer-key identity convention).</summary>
    public PropertyBuilder ValueGeneratedNever()
    {
        _configuration.Generation = ValueGeneration.None;
        return this;
    }

    /// <summary>Written on insert, never updated.</summary>
    public PropertyBuilder IsInsertOnly()
    {
        _configuration.InsertOnly = true;
        return this;
    }

    /// <summary>Selected but never written or read back.</summary>
    public PropertyBuilder IsReadOnly()
    {
        _configuration.ReadOnly = true;
        return this;
    }

    /// <summary>A database-maintained concurrency token such as SQL Server <c>rowversion</c> (same as <c>[Timestamp]</c>).</summary>
    public PropertyBuilder IsRowVersion()
    {
        _configuration.Token = ConcurrencyToken.RowVersion;
        _configuration.Generation = ValueGeneration.OnInsertAndUpdate;
        return this;
    }

    /// <summary>
    /// An integer version counter that Writeback compares and increments on every update
    /// (same as <c>[ConcurrencyCheck]</c> on an integer property).
    /// </summary>
    public PropertyBuilder IsVersionCounter()
    {
        _configuration.Token = ConcurrencyToken.VersionCounter;
        return this;
    }
}

internal sealed class EntityConfiguration
{
    public EntityConfiguration(Type entityType)
    {
        EntityType = entityType;
    }

    public Type EntityType { get; }

    public bool TableConfigured { get; set; }

    public string? TableName { get; set; }

    public string? Schema { get; set; }

    public IReadOnlyList<string>? KeyProperties { get; set; }

    public bool? HasTriggers { get; set; }

    public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PropertyConfiguration> Properties { get; } = new(StringComparer.Ordinal);
}

internal sealed class PropertyConfiguration
{
    public string? ColumnName { get; set; }

    public ValueGeneration? Generation { get; set; }

    public bool? InsertOnly { get; set; }

    public bool? ReadOnly { get; set; }

    public ConcurrencyToken? Token { get; set; }
}
