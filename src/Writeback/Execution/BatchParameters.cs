using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Dapper;
using Writeback.Mapping;

namespace Writeback.Execution;

/// <summary>
/// Parameters for batch commands, added in O(n). Dapper's <see cref="DynamicParameters"/> checks
/// <c>command.Parameters.Contains(name)</c> before every add, which is O(n²) on providers with linear parameter
/// collections (SqlClient compares names culture-aware: ~2M comparisons for one 2,098-parameter chunk).
/// Values of types with a Dapper type handler are still bound by Dapper so handlers apply.
/// </summary>
internal sealed class BatchParameters : SqlMapper.IDynamicParameters
{
    private const int DapperDefaultStringLength = 4000; // DbString.DefaultLength: stable sizes keep SQL Server plan reuse

    private readonly List<(string Name, ColumnMap Column, object? Value)> _values;
    private DynamicParameters? _handled;

    public BatchParameters(int capacity)
    {
        _values = new List<(string, ColumnMap, object?)>(capacity);
    }

    public void Add(string name, ColumnMap column, object? value)
    {
        if (value is not null && column.UsesTypeHandler)
        {
            (_handled ??= new DynamicParameters()).Add(name, value);
            return;
        }

        _values.Add((name, column, value));
    }

    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
    {
        var parameters = command.Parameters;
        foreach (var (name, column, value) in _values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Direction = ParameterDirection.Input;
            Assign(parameter, column, value);
            parameters.Add(parameter);
        }

        if (_handled is not null)
        {
            ((SqlMapper.IDynamicParameters)_handled).AddParameters(command, identity);
        }
    }

    /// <summary>Sets a parameter's value the way Dapper would: enums as integers, typed NULLs, stable string sizes.</summary>
    public static void Assign(IDbDataParameter parameter, ColumnMap column, object? value)
    {
        switch (value)
        {
            case null:
                parameter.Value = DBNull.Value;
                if (column.DbTypeForNull is { } dbType)
                {
                    parameter.DbType = dbType;
                }

                break;
            case Enum e:
                DapperTyping.SetDbType(parameter, e);
                parameter.Value = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture);
                break;
            case string s:
                DapperTyping.SetDbType(parameter, s);
                parameter.Value = s;
                if (s.Length <= DapperDefaultStringLength)
                {
                    parameter.Size = DapperDefaultStringLength;
                }

                break;
            default:
                // Same DbType Dapper would choose, including SqlMapper.AddTypeMap overrides (e.g. DateTime -> DateTime2).
                DapperTyping.SetDbType(parameter, value);
                parameter.Value = value;
                break;
        }
    }
}
