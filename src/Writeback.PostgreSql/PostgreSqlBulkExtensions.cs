using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Writeback.PostgreSql;

/// <summary>
/// True bulk loading through PostgreSQL's binary <c>COPY ... FROM STDIN</c>. Orders of magnitude faster than INSERT
/// statements for large volumes, but database-generated values are <b>not</b> read back. Use <c>InsertManyAsync</c>
/// when you need them.
/// </summary>
public static class PostgreSqlBulkExtensions
{
    /// <summary>
    /// Streams <paramref name="entities"/> into the mapped table with binary COPY, writing the same columns
    /// <c>InsertAsync</c> would. Participates in the connection's current transaction, if any. Returns the number of rows copied.
    /// </summary>
    public static async Task<ulong> BulkCopyAsync<T>(
        this NpgsqlConnection connection,
        IEnumerable<T> entities,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entities);

        var sql = EntitySql.For<T>(SqlDialect.PostgreSql);
        var columns = sql.Map.InsertColumns;
        var copy = $"COPY {sql.Table} ({string.Join(", ", columns.Select(c => SqlDialect.PostgreSql.QuoteIdentifier(c.ColumnName)))}) FROM STDIN (FORMAT BINARY)";

        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken).ConfigureAwait(false);
            await using (importer.ConfigureAwait(false))
            {
                using var reader = new Writeback.Bulk.EntityDataReader<T>(entities, columns);
                var values = new object[columns.Count];
                while (reader.Read())
                {
                    reader.GetValues(values);
                    await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var value in values)
                    {
                        if (value is DBNull)
                        {
                            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await importer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                return await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
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
