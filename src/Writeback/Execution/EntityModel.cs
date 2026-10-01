using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Writeback.Mapping;

namespace Writeback.Execution;

/// <summary>Frozen options plus the caches derived from them: entity maps, rendered commands and dialect resolution.</summary>
internal sealed class EntityModel
{
    private static readonly Dictionary<string, SqlDialect> KnownConnections = new(StringComparer.Ordinal)
    {
        ["Microsoft.Data.SqlClient.SqlConnection"] = SqlDialect.SqlServer,
        ["System.Data.SqlClient.SqlConnection"] = SqlDialect.SqlServer,
        ["Npgsql.NpgsqlConnection"] = SqlDialect.PostgreSql,
        ["MySqlConnector.MySqlConnection"] = SqlDialect.MySql,
        ["MySql.Data.MySqlClient.MySqlConnection"] = SqlDialect.MySql,
        ["Microsoft.Data.Sqlite.SqliteConnection"] = SqlDialect.Sqlite,
        ["System.Data.SQLite.SQLiteConnection"] = SqlDialect.Sqlite,
    };

    private static readonly string[] WrapperProperties = { "WrappedConnection", "InnerConnection" };

    private readonly ConcurrentDictionary<Type, EntityMap> _maps = new();
    private readonly ConcurrentDictionary<(Type, SqlDialect), EntityCommands> _commands = new();
    private readonly ConcurrentDictionary<Type, Func<IDbConnection, SqlDialect>> _byConnectionType = new();
    private readonly Dictionary<Type, EntityConfiguration> _entities;
    private readonly Dictionary<Type, SqlDialect> _explicitDialects;
    private readonly Func<IDbConnection, SqlDialect?>[] _resolvers;

    public EntityModel(WritebackOptions options)
    {
        Naming = options.NamingConvention ?? NamingConvention.AsIs;
        MaxBatchSize = options.MaxBatchSize;
        CommandTimeout = options.CommandTimeout;
        _entities = new Dictionary<Type, EntityConfiguration>(options.Entities);
        _explicitDialects = new Dictionary<Type, SqlDialect>(options.DialectsByConnectionType);
        _resolvers = options.DialectResolvers.ToArray();
    }

    public NamingConvention Naming { get; }

    public int MaxBatchSize { get; }

    public int? CommandTimeout { get; }

    public EntityMap GetMap(Type entityType) =>
        _maps.GetOrAdd(entityType, type => EntityMapBuilder.Build(type, _entities.GetValueOrDefault(type), Naming));

    public EntityCommands GetCommands(Type entityType, SqlDialect dialect) =>
        _commands.GetOrAdd((entityType, dialect), key => new EntityCommands(GetMap(key.Item1), key.Item2));

    public EntityCommands GetCommands(Type entityType, IDbConnection connection) => GetCommands(entityType, ResolveDialect(connection));

    public SqlDialect ResolveDialect(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        foreach (var resolver in _resolvers)
        {
            if (resolver(connection) is { } resolved)
            {
                return resolved;
            }
        }

        return _byConnectionType.GetOrAdd(connection.GetType(), CreateTypeResolver)(connection);
    }

    private Func<IDbConnection, SqlDialect> CreateTypeResolver(Type connectionType)
    {
        for (var type = connectionType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (_explicitDialects.TryGetValue(type, out var dialect))
            {
                return _ => dialect;
            }
        }

        foreach (var (registeredType, dialect) in _explicitDialects)
        {
            if (registeredType.IsAssignableFrom(connectionType))
            {
                return _ => dialect;
            }
        }

        for (var type = connectionType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.FullName is { } name && KnownConnections.TryGetValue(name, out var dialect))
            {
                return _ => dialect;
            }
        }

        // Profiling/logging wrappers (e.g. MiniProfiler's ProfiledDbConnection) expose the real connection.
        foreach (var propertyName in WrapperProperties)
        {
            var property = connectionType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && typeof(IDbConnection).IsAssignableFrom(property.PropertyType))
            {
                return connection => property.GetValue(connection) is IDbConnection inner
                    ? ResolveDialect(inner)
                    : throw new DialectNotFoundException(connectionType);
            }
        }

        return _ => throw new DialectNotFoundException(connectionType);
    }
}
