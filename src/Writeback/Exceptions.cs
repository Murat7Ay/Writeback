using System;
using System.Collections.Generic;
using System.Linq;

namespace Writeback;

/// <summary>Base type for every exception thrown by Writeback itself.</summary>
public class WritebackException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WritebackException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner exception.</summary>
    public WritebackException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// The entity type cannot be mapped. Thrown the first time a type is used, listing every problem found
/// (not just the first) together with how to fix it.
/// </summary>
public sealed class EntityMappingException : WritebackException
{
    /// <summary>Creates the exception.</summary>
    public EntityMappingException(Type entityType, IReadOnlyList<string> problems)
        : base(BuildMessage(entityType, problems))
    {
        EntityType = entityType;
        Problems = problems;
    }

    /// <summary>The entity type that failed to map.</summary>
    public Type EntityType { get; }

    /// <summary>Every problem found, one actionable sentence each.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(Type entityType, IReadOnlyList<string> problems)
    {
        if (problems.Count == 1)
        {
            return $"Cannot map entity '{entityType.FullName}': {problems[0]}";
        }

        return $"Cannot map entity '{entityType.FullName}' ({problems.Count} problems):"
               + string.Concat(problems.Select(p => Environment.NewLine + "  - " + p));
    }
}

/// <summary>No <see cref="SqlDialect"/> could be determined for a connection.</summary>
public sealed class DialectNotFoundException : WritebackException
{
    /// <summary>Creates the exception.</summary>
    public DialectNotFoundException(Type connectionType)
        : base($"No SQL dialect is registered for connection type '{connectionType.FullName}'. "
               + "Built-in detection covers SqlConnection (Microsoft.Data.SqlClient / System.Data.SqlClient), NpgsqlConnection, "
               + "MySqlConnection (MySqlConnector / MySql.Data) and SqliteConnection (Microsoft.Data.Sqlite / System.Data.SQLite). "
               + "For other or wrapping connection types call WritebackConfig.Configure(o => o.UseDialect<"
               + connectionType.Name + ">(SqlDialect.PostgreSql)) (or the matching dialect) at startup.")
    {
        ConnectionType = connectionType;
    }

    /// <summary>The connection type that could not be resolved.</summary>
    public Type ConnectionType { get; }
}

/// <summary>
/// An update or delete guarded by a concurrency token (<c>[Timestamp]</c>/<c>[ConcurrencyCheck]</c>) matched no row:
/// either another writer changed the row since it was read, or the row was deleted.
/// </summary>
public sealed class ConcurrencyConflictException : WritebackException
{
    /// <summary>Creates the exception.</summary>
    public ConcurrencyConflictException(string message, IReadOnlyList<object> entities) : base(message)
    {
        Entities = entities;
    }

    /// <summary>
    /// The entities whose statement matched no row. For <c>DeleteManyAsync</c> the database cannot tell which rows
    /// were missing, so this contains every entity of the failed batch.
    /// </summary>
    public IReadOnlyList<object> Entities { get; }
}
