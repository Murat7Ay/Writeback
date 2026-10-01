using System;
using System.Collections.Generic;
using System.Data;
using Writeback.Mapping;

namespace Writeback;

/// <summary>Startup configuration. Passed to <see cref="WritebackConfig.Configure"/>.</summary>
public sealed class WritebackOptions
{
    private int _maxBatchSize = 1000;

    /// <summary>Derives table and column names from CLR names when none is configured. Default <see cref="NamingConvention.AsIs"/>.</summary>
    public NamingConvention NamingConvention { get; set; } = NamingConvention.AsIs;

    /// <summary>
    /// Upper bound on rows per batch command for the <c>*ManyAsync</c> operations. Batches are additionally limited by the
    /// dialect's parameter limit (e.g. 2098 on SQL Server). Default 1000.
    /// </summary>
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        set => _maxBatchSize = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "MaxBatchSize must be at least 1.");
    }

    /// <summary>Command timeout in seconds for every command, or <see langword="null"/> for the provider default.</summary>
    public int? CommandTimeout { get; set; }

    internal Dictionary<Type, EntityConfiguration> Entities { get; } = new();

    internal Dictionary<Type, SqlDialect> DialectsByConnectionType { get; } = new();

    internal List<Func<IDbConnection, SqlDialect?>> DialectResolvers { get; } = new();

    /// <summary>Configures the mapping of <typeparamref name="T"/> fluently. May be called more than once per type.</summary>
    public WritebackOptions Entity<T>(Action<EntityBuilder<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (!Entities.TryGetValue(typeof(T), out var configuration))
        {
            configuration = new EntityConfiguration(typeof(T));
            Entities.Add(typeof(T), configuration);
        }

        configure(new EntityBuilder<T>(configuration));
        return this;
    }

    /// <summary>
    /// Uses <paramref name="dialect"/> for connections of type <typeparamref name="TConnection"/> (or derived types).
    /// Needed for providers or wrappers that are not detected automatically.
    /// </summary>
    public WritebackOptions UseDialect<TConnection>(SqlDialect dialect) where TConnection : IDbConnection
    {
        ArgumentNullException.ThrowIfNull(dialect);
        DialectsByConnectionType[typeof(TConnection)] = dialect;
        return this;
    }

    /// <summary>
    /// Adds a resolver consulted (in registration order) before automatic detection. Return <see langword="null"/>
    /// to defer. Resolvers run on every call, so keep them cheap.
    /// </summary>
    public WritebackOptions UseDialectResolver(Func<IDbConnection, SqlDialect?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        DialectResolvers.Add(resolver);
        return this;
    }
}
