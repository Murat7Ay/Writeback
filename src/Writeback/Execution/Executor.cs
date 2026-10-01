using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback.Execution;

/// <summary>Runs rendered plans through Dapper and interprets their outcome.</summary>
internal static class Executor
{
    public static DbConnection AsDbConnection(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection as DbConnection
               ?? throw new ArgumentException(
                   $"Writeback requires a System.Data.Common.DbConnection for true async I/O; '{connection.GetType().FullName}' is only an IDbConnection. "
                   + "Every mainstream ADO.NET provider derives from DbConnection.",
                   nameof(connection));
    }

    public static CommandDefinition Command(
        EntityModel model, string sql, object? parameters, IDbTransaction? transaction, CancellationToken cancellationToken, bool cacheable = true) =>
        new(sql, parameters, transaction, model.CommandTimeout, CommandType.Text,
            cacheable ? CommandFlags.Buffered : CommandFlags.Buffered | CommandFlags.NoCache, cancellationToken);

    /// <summary>Runs a single-row plan. Returns whether the row was written and the read-back values, if any.</summary>
    public static async Task<RowResult> RunSingleAsync(DbConnection connection, EntityCommands commands, CommandPlan plan, CommandDefinition command)
    {
        try
        {
            switch (plan.Outcome)
            {
                case StatementOutcome.AffectedRows:
                    return new RowResult(await connection.ExecuteAsync(command).ConfigureAwait(false) > 0, null);
                case StatementOutcome.ScalarRowCount:
                    return new RowResult(await connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false) > 0, null);
                case StatementOutcome.ReturnedRow:
                    var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        object?[]? values = null;
                        if (await reader.ReadAsync(command.CancellationToken).ConfigureAwait(false))
                        {
                            values = ReadRow(reader, CreateParsers(reader, plan.Readback), plan.Readback);
                        }

                        await DrainAsync(reader, command.CancellationToken).ConfigureAwait(false);
                        return new RowResult(values is not null, values);
                    }

                default:
                    throw new InvalidOperationException($"Outcome {plan.Outcome} is not valid for a single-row command.");
            }
        }
        catch (DbException exception) when (commands.Dialect.TranslateException(exception, commands.Map) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Runs a batch plan for <paramref name="rows"/> input rows. Fills <paramref name="results"/> (indexed by row) with
    /// read-back values; returns the affected-row count reported by the database.
    /// </summary>
    public static async Task<int> RunBatchAsync(
        DbConnection connection, EntityCommands commands, CommandPlan plan, CommandDefinition command, int rows, object?[]?[] results)
    {
        var cancellationToken = command.CancellationToken;
        try
        {
            switch (plan.Outcome)
            {
                case StatementOutcome.AffectedRows:
                    return await connection.ExecuteAsync(command).ConfigureAwait(false);
                case StatementOutcome.ScalarRowCount:
                    // one SELECT @@ROWCOUNT per statement; sum them
                    var total = 0;
                    var countReader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
                    await using (countReader.ConfigureAwait(false))
                    {
                        do
                        {
                            while (await countReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                total += Convert.ToInt32(countReader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                        while (await countReader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                    }

                    return total;
            }

            var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                Func<DbDataReader, object>[]? parsers = null;
                var written = 0;
                switch (plan.Outcome)
                {
                    case StatementOutcome.ReturnedRow:
                    case StatementOutcome.ResultSetPerRow:
                        for (var row = 0; row < rows; row++)
                        {
                            if (row > 0 && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                            {
                                throw new WritebackException($"Expected {rows} result sets from a batch but got {row}.");
                            }

                            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                parsers ??= CreateParsers(reader, plan.Readback);
                                results[row] = ReadRow(reader, parsers, plan.Readback);
                                written++;
                            }
                        }

                        break;
                    case StatementOutcome.PositionedRows:
                        var positionOrdinal = plan.Readback.Count;
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            parsers ??= CreateParsers(reader, plan.Readback);
                            var position = Convert.ToInt32(reader.GetValue(positionOrdinal), System.Globalization.CultureInfo.InvariantCulture);
                            results[position] = ReadRow(reader, parsers, plan.Readback);
                            written++;
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected outcome {plan.Outcome}.");
                }

                await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
                return written;
            }
        }
        catch (DbException exception) when (commands.Dialect.TranslateException(exception, commands.Map) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Executes a single-row <paramref name="plan"/> once per entity on <b>one prepared command</b>, rebinding parameter
    /// values each time. Used by dialects that do not batch statements (SQLite), where re-preparing the statement per
    /// row would dominate. Returns <see langword="false"/> (doing nothing) when a parameter needs a Dapper type handler,
    /// so the caller falls back to Dapper's binder.
    /// </summary>
    public static async Task<bool> TryRunPreparedAsync(
        DbConnection connection,
        IDbTransaction? transaction,
        EntityModel model,
        EntityCommands commands,
        CommandPlan plan,
        IReadOnlyList<ColumnMap> parameterColumns,
        IReadOnlyList<object> entities,
        RowResult[] results,
        CancellationToken cancellationToken)
    {
        if ((transaction is not null && transaction is not DbTransaction) || parameterColumns.Any(c => c.UsesTypeHandler))
        {
            return false;
        }

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = plan.Sql;
            command.Transaction = (DbTransaction?)transaction;
            if (model.CommandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            var parameters = new DbParameter[parameterColumns.Count];
            for (var p = 0; p < parameters.Length; p++)
            {
                parameters[p] = command.CreateParameter();
                parameters[p].ParameterName = parameterColumns[p].PropertyName;
                command.Parameters.Add(parameters[p]);
            }

            Func<DbDataReader, object>[]? parsers = null;
            try
            {
                for (var i = 0; i < entities.Count; i++)
                {
                    for (var p = 0; p < parameters.Length; p++)
                    {
                        BatchParameters.Assign(parameters[p], parameterColumns[p], parameterColumns[p].Getter(entities[i]));
                    }

                    if (i == 0)
                    {
                        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    switch (plan.Outcome)
                    {
                        case StatementOutcome.AffectedRows:
                            results[i] = new RowResult(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0, null);
                            break;
                        case StatementOutcome.ScalarRowCount:
                            var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                            results[i] = new RowResult(Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) > 0, null);
                            break;
                        default:
                            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                            await using (reader.ConfigureAwait(false))
                            {
                                object?[]? values = null;
                                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                {
                                    parsers ??= CreateParsers(reader, plan.Readback);
                                    values = ReadRow(reader, parsers, plan.Readback);
                                }

                                await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
                                results[i] = new RowResult(values is not null, values);
                            }

                            break;
                    }
                }
            }
            catch (DbException exception) when (commands.Dialect.TranslateException(exception, commands.Map) is { } translated)
            {
                throw translated;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs a multi-command operation on one open connection (opened here if closed, so chunks never churn the pool or
    /// re-enlist in an ambient transaction). When <paramref name="required"/> and the caller supplied no transaction and
    /// no ambient <see cref="System.Transactions.TransactionScope"/> exists, a transaction makes the batch all-or-nothing.
    /// </summary>
    public static async Task<TResult> InTransactionAsync<TResult>(
        DbConnection connection, IDbTransaction? transaction, bool required, Func<IDbTransaction?, Task<TResult>> work, CancellationToken cancellationToken)
    {
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (transaction is not null || !required || System.Transactions.Transaction.Current is not null)
            {
                return await work(transaction).ConfigureAwait(false);
            }

            var owned = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (owned.ConfigureAwait(false))
            {
                var result = await work(owned).ConfigureAwait(false);
                // Once every statement succeeded, commit even if cancellation was requested meanwhile.
                await owned.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return result;
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

    /// <summary>Writes read-back values into the entity. Key columns are skipped unless <paramref name="includeKeys"/>.</summary>
    public static void Apply(object entity, IReadOnlyList<ColumnMap> columns, object?[] values, bool includeKeys)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var column = columns[i];
            if (column.Generation == ValueGeneration.None || (column.IsKey && !includeKeys))
            {
                continue;
            }

            column.Setter!(entity, values[i]);
        }
    }

    public static void IncrementVersion(EntityMap map, object entity)
    {
        if (map.ConcurrencyToken is { ConcurrencyToken: ConcurrencyToken.VersionCounter } version)
        {
            version.Setter!(entity, TypeSupport.Increment(version.Getter(entity)!));
        }
    }

    public static int ChunkSize(EntityModel model, SqlDialect dialect, int parametersPerRow) =>
        Math.Max(1, Math.Min(Math.Min(model.MaxBatchSize, dialect.MaxRowsPerStatement), dialect.MaxParameters / Math.Max(1, parametersPerRow)));

    public static void AddParameter(BatchParameters parameters, string name, ColumnMap column, object? value) =>
        parameters.Add(name, column, value);

    private static Func<DbDataReader, object>[] CreateParsers(DbDataReader reader, IReadOnlyList<ColumnMap> columns)
    {
        // Dapper's own per-column deserializers: same conversions and type handlers as any Dapper query.
        var parsers = new Func<DbDataReader, object>[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            parsers[i] = reader.GetRowParser<object>(columns[i].Property.PropertyType, i, 1);
        }

        return parsers;
    }

    private static object?[] ReadRow(DbDataReader reader, Func<DbDataReader, object>[] parsers, IReadOnlyList<ColumnMap> columns)
    {
        var values = new object?[parsers.Length];
        for (var i = 0; i < parsers.Length; i++)
        {
            values[i] = ValueConverter.ConvertTo(parsers[i](reader), columns[i]);
        }

        return values;
    }

    /// <summary>Consumes remaining result sets so errors raised by later statements in the batch surface here.</summary>
    private static async Task DrainAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
}

internal readonly record struct RowResult(bool Written, object?[]? Values);
