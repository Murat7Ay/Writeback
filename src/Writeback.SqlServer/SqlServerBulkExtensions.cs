using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Writeback.Bulk;
using Writeback.Mapping;
using Microsoft.Data.SqlClient;

namespace Writeback.SqlServer;

/// <summary>Options for <see cref="SqlServerBulkExtensions.BulkCopyAsync{T}"/>.</summary>
public sealed class SqlServerBulkCopyOptions
{
    /// <summary>Rows per batch sent to the server; 0 sends everything as one batch (SqlBulkCopy default).</summary>
    public int BatchSize { get; set; }

    /// <summary>Timeout in seconds; 0 means no limit. Default 0, since bulk loads are long-running by design.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Also copy identity key values from the entities instead of letting the database generate them.</summary>
    public bool KeepIdentity { get; set; }

    /// <summary>Additional SqlBulkCopy flags (e.g. <see cref="SqlBulkCopyOptions.TableLock"/>, <see cref="SqlBulkCopyOptions.CheckConstraints"/>).</summary>
    public SqlBulkCopyOptions CopyOptions { get; set; } = SqlBulkCopyOptions.Default;
}

/// <summary>
/// True bulk loading through SQL Server's bulk-copy protocol. Orders of magnitude faster than INSERT statements for large
/// volumes, but database-generated values are <b>not</b> read back. Use <c>InsertManyAsync</c> when you need them.
/// </summary>
public static class SqlServerBulkExtensions
{
    /// <summary>
    /// Streams <paramref name="entities"/> into the mapped table with <see cref="SqlBulkCopy"/>, writing the same columns
    /// <c>InsertAsync</c> would. Returns the number of rows copied.
    /// </summary>
    public static async Task<long> BulkCopyAsync<T>(
        this SqlConnection connection,
        IEnumerable<T> entities,
        SqlTransaction? transaction = null,
        SqlServerBulkCopyOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entities);
        options ??= new SqlServerBulkCopyOptions();

        var sql = EntitySql.For<T>(SqlDialect.SqlServer);
        var map = sql.Map;
        IReadOnlyList<ColumnMap> columns = options.KeepIdentity && map.Identity is { } identity
            ? new[] { identity }.Concat(map.InsertColumns).ToArray()
            : map.InsertColumns;

        var copyOptions = options.CopyOptions | (options.KeepIdentity ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default);
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var bulk = new SqlBulkCopy(connection, copyOptions, transaction)
            {
                DestinationTableName = sql.Table,
                BatchSize = options.BatchSize,
                BulkCopyTimeout = options.TimeoutSeconds,
                EnableStreaming = true,
            };

            for (var i = 0; i < columns.Count; i++)
            {
                bulk.ColumnMappings.Add(i, columns[i].ColumnName);
            }

            using var reader = new EntityDataReader<T>(entities, columns);
            await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
            return reader.RowsRead;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
