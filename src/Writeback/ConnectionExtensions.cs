using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Writeback.Execution;
using Writeback.Mapping;
using Writeback.Sql;

namespace Writeback;

/// <summary>
/// Persistence commands on any ADO.NET connection. Every method issues exactly one command (or, for the <c>*ManyAsync</c>
/// methods, one command per chunk inside one transaction) and nothing else: no tracking, no caching of entities, no
/// lazy loading. The SQL each method runs is available from <see cref="Sql{T}(IDbConnection)"/>.
/// </summary>
/// <remarks>
/// The entity type argument <c>T</c> selects the mapping. The connection may be closed (it is opened
/// for the duration of the call) or open; transactions are the caller's, except that multi-statement batches create
/// their own when none is supplied.
/// </remarks>
public static class ConnectionExtensions
{
    // ---------------------------------------------------------------- single-row writes

    /// <summary>
    /// Inserts <paramref name="entity"/> and writes every database-generated value (identity key, defaults, computed
    /// columns, row version) back into it, using <c>RETURNING</c>, <c>OUTPUT</c> or a same-batch re-select depending on the database.
    /// </summary>
    public static async Task InsertAsync<T>(
        this IDbConnection connection, T entity, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (model, db, commands) = Prepare<T>(connection);
        var plan = commands.Insert;
        var result = await Executor.RunSingleAsync(db, commands, plan, Executor.Command(model, plan.Sql, entity, transaction, cancellationToken))
            .ConfigureAwait(false);

        if (plan.Outcome == StatementOutcome.ReturnedRow)
        {
            if (result.Values is null)
            {
                throw new WritebackException(
                    $"The INSERT into '{commands.Map.TableName}' returned no generated values. If an INSTEAD OF trigger or row-level security "
                    + "hides the inserted row, generated values cannot be read back.");
            }

            Executor.Apply(entity, plan.Readback, result.Values, includeKeys: true);
        }
    }

    /// <summary>
    /// Updates every updatable column of <paramref name="entity"/> by key. Returns <see langword="false"/> when no row has
    /// that key. If the entity has a concurrency token, a missing or concurrently modified row throws
    /// <see cref="ConcurrencyConflictException"/> instead; on success the new token is written back.
    /// </summary>
    public static Task<bool> UpdateAsync<T>(
        this IDbConnection connection, T entity, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (model, db, commands) = Prepare<T>(connection);
        return UpdateCoreAsync(model, db, commands, commands.Update, entity, transaction, cancellationToken);
    }

    /// <summary>
    /// Updates only the selected columns, e.g. <c>UpdateAsync(customer, c =&gt; new { c.Name, c.Email })</c>.
    /// No change tracking is involved: the caller states what changed. Concurrency tokens are still checked.
    /// </summary>
    public static Task<bool> UpdateAsync<T>(
        this IDbConnection connection,
        T entity,
        Expression<Func<T, object?>> columns,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (model, db, commands) = Prepare<T>(connection);
        var selected = ResolveUpdateColumns(commands.Map, columns);
        return UpdateCoreAsync(model, db, commands, commands.PartialUpdate(selected), entity, transaction, cancellationToken);
    }

    /// <summary>
    /// Deletes <paramref name="entity"/> by key. Returns <see langword="false"/> when no row matched; with a concurrency
    /// token a mismatch throws <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    public static async Task<bool> DeleteAsync<T>(
        this IDbConnection connection, T entity, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (model, db, commands) = Prepare<T>(connection);
        var plan = commands.Delete;
        var result = await Executor.RunSingleAsync(db, commands, plan, Executor.Command(model, plan.Sql, entity, transaction, cancellationToken))
            .ConfigureAwait(false);
        if (!result.Written && commands.Map.ConcurrencyToken is not null)
        {
            throw Conflict(commands.Map, "DELETE", new object[] { entity });
        }

        return result.Written;
    }

    /// <summary>
    /// Deletes the row with the given key without loading it. <paramref name="key"/> is the key value, or for composite
    /// keys an object such as <c>new { OrderId = 1, LineNumber = 2 }</c>. Concurrency tokens are not checked.
    /// </summary>
    public static async Task<bool> DeleteByKeyAsync<T>(
        this IDbConnection connection, object key, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        var plan = commands.DeleteByKey;
        var parameters = KeyBinder.ToParameters(commands.Map, key, nameof(key));
        var result = await Executor.RunSingleAsync(db, commands, plan, Executor.Command(model, plan.Sql, parameters, transaction, cancellationToken))
            .ConfigureAwait(false);
        return result.Written;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Loads the row with the given key, or <see langword="null"/>.</summary>
    public static async Task<T?> GetAsync<T>(
        this IDbConnection connection, object key, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        var parameters = KeyBinder.ToParameters(commands.Map, key, nameof(key));
        return await db.QueryFirstOrDefaultAsync<T>(Executor.Command(model, commands.SelectByKey, parameters, transaction, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the rows with the given keys in as few round trips as the parameter limit allows. Missing keys are skipped;
    /// result order is unspecified.
    /// </summary>
    public static async Task<IReadOnlyList<T>> GetManyAsync<T>(
        this IDbConnection connection, System.Collections.IEnumerable keys, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        commands.Map.EnsureHasKey("GetManyAsync");
        var keyList = KeyBinder.ToList(keys, nameof(keys));
        var width = commands.KeyWidth;
        var chunk = Executor.ChunkSize(model, commands.Dialect, width);
        return await Executor.InTransactionAsync<IReadOnlyList<T>>(db, transaction, required: false, async tx =>
        {
            var result = new List<T>(keyList.Count);
            for (var start = 0; start < keyList.Count; start += chunk)
            {
                var rows = Math.Min(chunk, keyList.Count - start);
                var plan = commands.SelectBatch(rows, cache: rows == chunk);
                var parameters = new BatchParameters(rows * width);
                for (var r = 0; r < rows; r++)
                {
                    var values = KeyBinder.GetKeyValues(commands.Map, keyList[start + r], nameof(keys));
                    for (var k = 0; k < width; k++)
                    {
                        Executor.AddParameter(parameters, EntityCommands.ParameterName(r, width, k), commands.Map.Keys[k], values[k]);
                    }
                }

                var page = await db.QueryAsync<T>(Executor.Command(model, plan.Sql, parameters, tx, cancellationToken, cacheable: rows == chunk))
                    .ConfigureAwait(false);
                result.AddRange(page);
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a row with the given key exists.</summary>
    public static async Task<bool> ExistsAsync<T>(
        this IDbConnection connection, object key, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        var parameters = KeyBinder.ToParameters(commands.Map, key, nameof(key));
        return await db.ExecuteScalarAsync<bool>(Executor.Command(model, commands.Exists, parameters, transaction, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>SELECT &lt;mapped columns&gt; FROM &lt;table&gt; </c> followed by your own SQL <paramref name="clause"/>
    /// (WHERE / ORDER BY / paging), e.g. <c>SelectAsync&lt;Customer&gt;("WHERE status = @status ORDER BY name", new { status })</c>.
    /// The clause is raw SQL using column names: parameterize every value, exactly as with Dapper.
    /// </summary>
    public static async Task<IReadOnlyList<T>> SelectAsync<T>(
        this IDbConnection connection,
        string? clause = null,
        object? param = null,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        var rows = await db.QueryAsync<T>(Executor.Command(model, Compose(commands.SelectFrom, clause), param, transaction, cancellationToken))
            .ConfigureAwait(false);
        return rows as IReadOnlyList<T> ?? rows.ToList();
    }

    /// <summary>Streaming variant of <see cref="SelectAsync{T}"/>: rows are materialized as they are read.</summary>
    public static async IAsyncEnumerable<T> SelectUnbufferedAsync<T>(
        this IDbConnection connection,
        string? clause = null,
        object? param = null,
        IDbTransaction? transaction = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        var command = Executor.Command(model, Compose(commands.SelectFrom, clause), param, transaction, cancellationToken);
        var reader = await db.ExecuteReaderAsync(command).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var parse = reader.GetRowParser<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return parse(reader);
            }
        }
    }

    /// <summary>Runs <c>SELECT COUNT(*) FROM &lt;table&gt; </c> followed by an optional SQL <paramref name="clause"/>.</summary>
    public static async Task<long> CountAsync<T>(
        this IDbConnection connection,
        string? clause = null,
        object? param = null,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var (model, db, commands) = Prepare<T>(connection);
        return await db.ExecuteScalarAsync<long>(Executor.Command(model, Compose(commands.CountFrom, clause), param, transaction, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Mapping-aware SQL fragments and the exact statements Writeback runs for <typeparamref name="T"/> on this
    /// connection's database. Use them to write your own queries without repeating table and column names.
    /// </summary>
    public static EntitySql<T> Sql<T>(this IDbConnection connection) where T : class
    {
        var model = WritebackConfig.Model;
        return new EntitySql<T>(model.GetCommands(typeof(T), connection));
    }

    // ---------------------------------------------------------------- batches

    /// <summary>
    /// Inserts many entities with as few round trips as the database's parameter limit allows and writes generated
    /// values back into each entity. Runs in one transaction (the caller's, or its own). This is batched SQL, not a
    /// bulk-load protocol; for millions of rows without read-back use the provider packages' <c>BulkCopyAsync</c>.
    /// </summary>
    /// <returns>The number of entities inserted.</returns>
    public static async Task<int> InsertManyAsync<T>(
        this IDbConnection connection, IEnumerable<T> entities, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var list = Materialize(entities, nameof(entities));
        if (list.Count == 0)
        {
            return 0;
        }

        if (list.Count == 1)
        {
            await connection.InsertAsync(list[0], transaction, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var (model, db, commands) = Prepare<T>(connection);
        var map = commands.Map;
        var width = commands.InsertWidth;
        var chunk = Executor.ChunkSize(model, commands.Dialect, width);
        var staged = new object?[]?[list.Count];
        var readback = map.InsertReadback;

        await Executor.InTransactionAsync(db, transaction, required: true, async tx =>
        {
            if (readback.Count > 0 && !commands.Dialect.BatchesStatements)
            {
                // In-process engine: one prepared single-row statement executed per entity, in one transaction.
                var single = commands.Insert;
                var results = await RunRowByRowAsync(model, db, commands, single, commands.InsertParameters, list, tx, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < list.Count; i++)
                {
                    staged[i] = results[i].Values ?? throw new WritebackException(
                        $"The INSERT into '{map.TableName}' returned no generated values; the batch was rolled back.");
                }

                return 0;
            }

            for (var start = 0; start < list.Count; start += chunk)
            {
                var rows = Math.Min(chunk, list.Count - start);
                var plan = commands.InsertBatch(rows, cache: rows == chunk);
                var parameters = new BatchParameters(rows * width);
                for (var r = 0; r < rows; r++)
                {
                    var entity = list[start + r];
                    for (var c = 0; c < width; c++)
                    {
                        var column = map.InsertColumns[c];
                        Executor.AddParameter(parameters, EntityCommands.ParameterName(r, width, c), column, column.Getter(entity));
                    }
                }

                var results = new object?[]?[rows];
                var command = Executor.Command(model, plan.Sql, parameters, tx, cancellationToken, cacheable: rows == chunk);
                var written = await Executor.RunBatchAsync(db, commands, plan, command, rows, results).ConfigureAwait(false);
                if (readback.Count > 0 && written != rows)
                {
                    throw new WritebackException(
                        $"Batch insert into '{map.TableName}' returned generated values for {written} of {rows} rows; the batch was rolled back.");
                }

                Array.Copy(results, 0, staged, start, rows);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);

        // Only after the transaction committed (or the caller's batch succeeded) are entities modified.
        if (readback.Count > 0)
        {
            for (var i = 0; i < list.Count; i++)
            {
                Executor.Apply(list[i], readback, staged[i]!, includeKeys: true);
            }
        }

        return list.Count;
    }

    /// <summary>
    /// Updates many entities in batched commands (one statement per entity, as few round trips as possible) inside one
    /// transaction. Returns the number of rows updated. If any entity has a concurrency token and does not match, nothing
    /// is applied to the entities and <see cref="ConcurrencyConflictException"/> lists the conflicting ones.
    /// </summary>
    public static async Task<int> UpdateManyAsync<T>(
        this IDbConnection connection, IEnumerable<T> entities, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var list = Materialize(entities, nameof(entities));
        if (list.Count == 0)
        {
            return 0;
        }

        var (model, db, commands) = Prepare<T>(connection);
        var map = commands.Map;
        map.EnsureHasKey("UpdateManyAsync");
        var width = commands.UpdateWidth;
        var chunk = Executor.ChunkSize(model, commands.Dialect, width);
        var staged = new object?[]?[list.Count];
        IReadOnlyList<ColumnMap> readback = Array.Empty<ColumnMap>();

        await Executor.InTransactionAsync(db, transaction, required: true, async tx =>
        {
            if (!commands.Dialect.BatchesStatements)
            {
                var single = commands.Update;
                readback = single.Readback;
                var results = await RunRowByRowAsync(model, db, commands, single, commands.UpdateParameters, list, tx, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < list.Count; i++)
                {
                    staged[i] = results[i].Written ? results[i].Values ?? Array.Empty<object?>() : null;
                }
            }
            else
            {
                for (var start = 0; start < list.Count; start += chunk)
                {
                    var rows = Math.Min(chunk, list.Count - start);
                    var plan = commands.UpdateBatch(rows, cache: rows == chunk);
                    readback = plan.Readback;
                    var parameters = new BatchParameters(rows * width);
                    for (var r = 0; r < rows; r++)
                    {
                        var entity = list[start + r];
                        foreach (var column in map.UpdateColumns)
                        {
                            AddSlot(parameters, commands, r, width, commands.UpdateSlot(column), column, entity);
                        }

                        foreach (var key in map.Keys)
                        {
                            AddSlot(parameters, commands, r, width, commands.UpdateSlot(key), key, entity);
                        }

                        if (map.ConcurrencyToken is { } token)
                        {
                            AddSlot(parameters, commands, r, width, commands.UpdateSlot(token), token, entity);
                        }
                    }

                    var results = new object?[]?[rows];
                    var command = Executor.Command(model, plan.Sql, parameters, tx, cancellationToken, cacheable: rows == chunk);
                    await Executor.RunBatchAsync(db, commands, plan, command, rows, results).ConfigureAwait(false);
                    Array.Copy(results, 0, staged, start, rows);
                }
            }

            if (map.ConcurrencyToken is not null)
            {
                var conflicts = list.Where((_, i) => staged[i] is null).Cast<object>().ToArray();
                if (conflicts.Length > 0)
                {
                    throw Conflict(map, "UPDATE", conflicts);
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);

        var updated = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (staged[i] is { } values)
            {
                Executor.Apply(list[i], readback, values, includeKeys: false);
                Executor.IncrementVersion(map, list[i]);
                updated++;
            }
        }

        return updated;
    }

    /// <summary>
    /// Deletes many entities by key (and concurrency token, if any) with one <c>DELETE ... WHERE key IN (...)</c> per chunk.
    /// Returns the number of rows deleted. With a concurrency token, a chunk that deletes fewer rows than requested throws
    /// <see cref="ConcurrencyConflictException"/> and the transaction is rolled back.
    /// </summary>
    public static async Task<int> DeleteManyAsync<T>(
        this IDbConnection connection, IEnumerable<T> entities, IDbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var list = Materialize(entities, nameof(entities));
        if (list.Count == 0)
        {
            return 0;
        }

        var (model, db, commands) = Prepare<T>(connection);
        var map = commands.Map;
        map.EnsureHasKey("DeleteManyAsync");
        var width = commands.DeleteWidth;
        var chunk = Executor.ChunkSize(model, commands.Dialect, width);

        return await Executor.InTransactionAsync(db, transaction, required: list.Count > chunk, async tx =>
        {
            var total = 0;
            for (var start = 0; start < list.Count; start += chunk)
            {
                var rows = Math.Min(chunk, list.Count - start);
                var plan = commands.DeleteBatch(rows, cache: rows == chunk);
                var parameters = new BatchParameters(rows * width);
                for (var r = 0; r < rows; r++)
                {
                    var entity = list[start + r];
                    foreach (var key in map.Keys)
                    {
                        AddSlot(parameters, commands, r, width, commands.DeleteSlot(key), key, entity);
                    }

                    if (map.ConcurrencyToken is { } token)
                    {
                        AddSlot(parameters, commands, r, width, commands.DeleteSlot(token), token, entity);
                    }
                }

                var command = Executor.Command(model, plan.Sql, parameters, tx, cancellationToken, cacheable: rows == chunk);
                var deleted = await Executor.RunBatchAsync(db, commands, plan, command, rows, Array.Empty<object?[]?>()).ConfigureAwait(false);
                if (map.ConcurrencyToken is not null && deleted < rows)
                {
                    throw Conflict(map, "DELETE", list.Skip(start).Take(rows).Cast<object>().ToArray());
                }

                total += deleted;
            }

            return total;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- helpers

    private static (EntityModel Model, DbConnection Db, EntityCommands Commands) Prepare<T>(IDbConnection connection)
    {
        var db = Executor.AsDbConnection(connection);
        var model = WritebackConfig.Model;
        return (model, db, model.GetCommands(typeof(T), db));
    }

    private static async Task<bool> UpdateCoreAsync<T>(
        EntityModel model, DbConnection db, EntityCommands commands, CommandPlan plan, T entity, IDbTransaction? transaction, CancellationToken cancellationToken)
        where T : class
    {
        var result = await Executor.RunSingleAsync(db, commands, plan, Executor.Command(model, plan.Sql, entity, transaction, cancellationToken))
            .ConfigureAwait(false);
        if (!result.Written)
        {
            if (commands.Map.ConcurrencyToken is not null)
            {
                throw Conflict(commands.Map, "UPDATE", new object[] { entity });
            }

            return false;
        }

        if (result.Values is not null)
        {
            Executor.Apply(entity, plan.Readback, result.Values, includeKeys: false);
        }

        Executor.IncrementVersion(commands.Map, entity);
        return true;
    }

    private static async Task<RowResult[]> RunRowByRowAsync<T>(
        EntityModel model, DbConnection db, EntityCommands commands, CommandPlan plan, IReadOnlyList<ColumnMap> parameterColumns,
        IReadOnlyList<T> list, IDbTransaction? transaction, CancellationToken cancellationToken)
        where T : class
    {
        var results = new RowResult[list.Count];
        if (await Executor.TryRunPreparedAsync(db, transaction, model, commands, plan, parameterColumns, list, results, cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        // A column needs a Dapper type handler: let Dapper bind each row.
        for (var i = 0; i < list.Count; i++)
        {
            results[i] = await Executor.RunSingleAsync(db, commands, plan, Executor.Command(model, plan.Sql, list[i], transaction, cancellationToken))
                .ConfigureAwait(false);
        }

        return results;
    }

    private static List<ColumnMap> ResolveUpdateColumns<T>(EntityMap map, Expression<Func<T, object?>> columns)
    {
        var selected = new List<ColumnMap>();
        foreach (var name in MemberSelector.GetPropertyNames(columns, nameof(columns)))
        {
            var column = map.FindByProperty(name)
                         ?? throw new ArgumentException($"'{name}' is not a mapped property of '{map.EntityType.Name}'.", nameof(columns));
            if (!column.IsUpdatable)
            {
                var reason = column.IsKey ? "it is a key"
                    : column.Generation != ValueGeneration.None ? "it is database-generated"
                    : column.ConcurrencyToken != ConcurrencyToken.None ? "it is a concurrency token (maintained automatically)"
                    : column.IsInsertOnly ? "it is insert-only"
                    : "it is read-only";
                throw new ArgumentException($"'{name}' cannot be updated because {reason}.", nameof(columns));
            }

            if (!selected.Contains(column))
            {
                selected.Add(column);
            }
        }

        selected.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        return selected;
    }

    private static void AddSlot(BatchParameters parameters, EntityCommands commands, int row, int width, int slot, ColumnMap column, object entity) =>
        Executor.AddParameter(parameters, EntityCommands.ParameterName(row, width, slot), column, column.Getter(entity));

    private static IReadOnlyList<T> Materialize<T>(IEnumerable<T> entities, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entities, parameterName);
        var list = entities as IReadOnlyList<T> ?? entities.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
            {
                throw new ArgumentException($"Element {i} is null.", parameterName);
            }
        }

        return list;
    }

    private static string Compose(string prefix, string? clause) =>
        string.IsNullOrWhiteSpace(clause) ? prefix : prefix + " " + clause;

    private static ConcurrencyConflictException Conflict(EntityMap map, string operation, object[] entities)
    {
        var token = map.ConcurrencyToken!;
        var subject = entities.Length == 1 ? "the row was" : $"{entities.Length} rows were";
        return new ConcurrencyConflictException(
            $"{operation} of '{map.EntityType.Name}' matched no row: {subject} changed or deleted since being read "
            + $"('{token.PropertyName}' no longer matches). Reload and retry, or resolve the conflict.",
            entities);
    }
}
