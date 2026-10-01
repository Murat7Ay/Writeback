using System.Data;
using Dapper;

namespace Writeback.Execution;

/// <summary>
/// The single place that asks Dapper which DbType it would give a value. Dapper marks
/// <see cref="SqlMapper.SetDbType"/> "for internal use only", but it is the only way to honor the user's
/// <c>SqlMapper.AddTypeMap</c> overrides in batch parameters, which keeps <c>InsertManyAsync</c> storing values exactly
/// like <c>InsertAsync</c> (whose parameters Dapper binds itself). Pinned by BatchParameterTypingTests.
/// </summary>
internal static class DapperTyping
{
    public static void SetDbType(IDataParameter parameter, object value)
    {
#pragma warning disable CS0618 // see type remarks
        SqlMapper.SetDbType(parameter, value);
#pragma warning restore CS0618
    }
}
