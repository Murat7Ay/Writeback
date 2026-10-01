using System;
using System.Globalization;

namespace Writeback.Mapping;

/// <summary>
/// Converts read-back values to the property type when the provider returns a different CLR type
/// (SQLite TEXT timestamps, SQL Server decimal identities, MySQL tinyint booleans, ...), mirroring Dapper's own leniency.
/// </summary>
internal static class ValueConverter
{
    public static object? ConvertTo(object? value, ColumnMap column)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(column.Property.PropertyType) ?? column.Property.PropertyType;
        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            return Convert(value, target);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new WritebackException(
                $"Cannot convert the value read back for column '{column.ColumnName}' ({value.GetType().Name}) to property "
                + $"'{column.Property.DeclaringType?.Name}.{column.PropertyName}' ({column.Property.PropertyType.Name}). "
                + "Register a Dapper type handler for the property type or change the column/property type.",
                ex);
        }
    }

    private static object Convert(object value, Type target)
    {
        if (target.IsEnum)
        {
            return value is string name
                ? Enum.Parse(target, name, ignoreCase: true)
                : Enum.ToObject(target, System.Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture));
        }

        if (target == typeof(Guid))
        {
            return value switch
            {
                string s => Guid.Parse(s),
                byte[] { Length: 16 } bytes => new Guid(bytes),
                _ => throw new InvalidCastException(),
            };
        }

        if (target == typeof(DateTimeOffset))
        {
            return value switch
            {
                DateTime dt => new DateTimeOffset(dt),
                string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(),
            };
        }

        if (target == typeof(DateOnly))
        {
            return value switch
            {
                DateTime dt => DateOnly.FromDateTime(dt),
                string s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(),
            };
        }

        if (target == typeof(TimeOnly))
        {
            return value switch
            {
                TimeSpan ts => TimeOnly.FromTimeSpan(ts),
                string s => TimeOnly.Parse(s, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(),
            };
        }

        if (target == typeof(TimeSpan) && value is string span)
        {
            return TimeSpan.Parse(span, CultureInfo.InvariantCulture);
        }

        if (target == typeof(DateTime) && value is string text)
        {
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);
        }

        return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
