using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace Writeback.Mapping;

/// <summary>
/// Decides which CLR types can be columns. Mirrors what Dapper can bind as a parameter: anything else would fail at
/// execution time with a less helpful error, so it is rejected when the mapping is built.
/// </summary>
internal static class TypeSupport
{
    private static readonly Dictionary<Type, DbType> DbTypes = new()
    {
        [typeof(byte)] = DbType.Byte,
        [typeof(sbyte)] = DbType.SByte,
        [typeof(short)] = DbType.Int16,
        [typeof(ushort)] = DbType.UInt16,
        [typeof(int)] = DbType.Int32,
        [typeof(uint)] = DbType.UInt32,
        [typeof(long)] = DbType.Int64,
        [typeof(ulong)] = DbType.UInt64,
        [typeof(float)] = DbType.Single,
        [typeof(double)] = DbType.Double,
        [typeof(decimal)] = DbType.Decimal,
        [typeof(bool)] = DbType.Boolean,
        [typeof(string)] = DbType.String,
        [typeof(char)] = DbType.StringFixedLength,
        [typeof(Guid)] = DbType.Guid,
        [typeof(DateTime)] = DbType.DateTime,
        [typeof(DateTimeOffset)] = DbType.DateTimeOffset,
        [typeof(TimeSpan)] = DbType.Time,
        [typeof(DateOnly)] = DbType.Date,
        [typeof(TimeOnly)] = DbType.Time,
        [typeof(byte[])] = DbType.Binary,
    };

    private static readonly HashSet<Type> IntegerTypes = new()
    {
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
    };

    public static bool IsSupported(Type type)
    {
        if (SqlMapper.HasTypeHandler(type))
        {
            return true;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (SqlMapper.HasTypeHandler(underlying) || underlying.IsEnum || DbTypes.ContainsKey(underlying)
            || underlying == typeof(object))
        {
            return true;
        }

        // Spatial/UDT types that Dapper special-cases by name.
        return underlying.FullName is "Microsoft.SqlServer.Types.SqlGeography"
            or "Microsoft.SqlServer.Types.SqlGeometry"
            or "Microsoft.SqlServer.Types.SqlHierarchyId";
    }

    public static bool HasTypeHandler(Type type) =>
        SqlMapper.HasTypeHandler(type) || (Nullable.GetUnderlyingType(type) is { } underlying && SqlMapper.HasTypeHandler(underlying));

    public static bool IsCollection(Type type) =>
        type != typeof(string) && type != typeof(byte[]) && typeof(IEnumerable).IsAssignableFrom(type);

    public static bool IsInteger(Type type) => IntegerTypes.Contains(type);

    /// <summary>
    /// DbType to declare for NULL values in batch parameters, as Dapper would declare it for the property type
    /// (honoring <c>SqlMapper.AddTypeMap</c>). Without it some providers type NULL as nvarchar, which SQL Server
    /// refuses to convert into e.g. varbinary columns.
    /// </summary>
    public static DbType? GetDbTypeForNull(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (HasTypeHandler(type))
        {
            return null;
        }

        object? sample = underlying == typeof(string) ? string.Empty
            : underlying == typeof(byte[]) ? Array.Empty<byte>()
            : underlying.IsValueType ? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(underlying)
            : null;
        if (sample is null)
        {
            return null;
        }

        var probe = new ProbeParameter();
        Execution.DapperTyping.SetDbType(probe, sample);
        return probe.WasSet ? probe.DbType : null;
    }

    private sealed class ProbeParameter : IDbDataParameter
    {
        private DbType _dbType;

        public bool WasSet { get; private set; }

        public DbType DbType
        {
            get => _dbType;
            set
            {
                _dbType = value;
                WasSet = true;
            }
        }

        public ParameterDirection Direction { get; set; }

        public bool IsNullable => true;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string ParameterName { get; set; } = "";

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string SourceColumn { get; set; } = "";

        public DataRowVersion SourceVersion { get; set; }

        public object? Value { get; set; }

        public byte Precision { get; set; }

        public byte Scale { get; set; }

        public int Size { get; set; }
    }

    /// <summary>Returns <paramref name="value"/> + 1 for any integer type, preserving the type.</summary>
    public static object Increment(object value) => value switch
    {
        int v => checked(v + 1),
        long v => checked(v + 1),
        short v => checked((short)(v + 1)),
        byte v => checked((byte)(v + 1)),
        uint v => checked(v + 1),
        ulong v => checked(v + 1),
        ushort v => checked((ushort)(v + 1)),
        sbyte v => checked((sbyte)(v + 1)),
        _ => throw new InvalidOperationException($"Version counter of type {value.GetType()} cannot be incremented."),
    };
}
