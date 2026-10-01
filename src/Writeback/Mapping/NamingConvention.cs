using System.Text;

namespace Writeback.Mapping;

/// <summary>
/// Turns CLR type and property names into table and column names when no explicit name is configured.
/// There is deliberately no pluralization: <c>Customer</c> maps to table <c>Customer</c> (or <c>customer</c>), never a guessed plural.
/// </summary>
public abstract class NamingConvention
{
    /// <summary>Use CLR names unchanged (<c>CreatedAt</c> → <c>CreatedAt</c>). The default.</summary>
    public static NamingConvention AsIs { get; } = new AsIsConvention();

    /// <summary><c>CreatedAt</c> → <c>created_at</c>, <c>HTTPStatus</c> → <c>http_status</c>. The usual PostgreSQL convention.</summary>
    public static NamingConvention SnakeCase { get; } = new SnakeCaseConvention();

    /// <summary><c>CreatedAt</c> → <c>createdat</c>.</summary>
    public static NamingConvention LowerCase { get; } = new LowerCaseConvention();

    /// <summary>Converts a table name derived from a CLR type name.</summary>
    public virtual string ToTableName(string typeName) => Convert(typeName);

    /// <summary>Converts a column name derived from a CLR property name.</summary>
    public virtual string ToColumnName(string propertyName) => Convert(propertyName);

    /// <summary>The conversion applied to both tables and columns unless overridden.</summary>
    protected abstract string Convert(string name);

    private sealed class AsIsConvention : NamingConvention
    {
        protected override string Convert(string name) => name;
    }

    private sealed class LowerCaseConvention : NamingConvention
    {
        protected override string Convert(string name) => name.ToLowerInvariant();
    }

    private sealed class SnakeCaseConvention : NamingConvention
    {
        protected override string Convert(string name) => ToSnakeCase(name);
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_')
                {
                    var previous = name[i - 1];
                    var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    // word boundary: aB -> a_b, 1B -> 1_b, and the last capital of an acronym: HTTPStatus -> http_status
                    if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                    {
                        builder.Append('_');
                    }
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
