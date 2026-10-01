using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Writeback.Mapping;

/// <summary>
/// Builds property accessors once per mapped property. Compiled expression trees where dynamic code is available,
/// plain reflection otherwise (Native AOT interprets expression trees, which is slower than reflection).
/// </summary>
internal static class Accessors
{
    public static Func<object, object?> CreateGetter(PropertyInfo property)
    {
        if (!RuntimeFeature.IsDynamicCodeCompiled)
        {
            return property.GetValue;
        }

        var instance = Expression.Parameter(typeof(object), "instance");
        var body = Expression.Convert(
            Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
            typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, instance).Compile();
    }

    /// <summary>
    /// Returns a setter that assigns <c>default</c> when given <see langword="null"/> (database NULL into a value type),
    /// or <see langword="null"/> when the property has no setter. Init-only setters are supported.
    /// </summary>
    public static Action<object, object?>? CreateSetter(PropertyInfo property)
    {
        var setMethod = property.SetMethod;
        if (setMethod is null || !setMethod.IsPublic)
        {
            return null;
        }

        var propertyType = property.PropertyType;
        if (!RuntimeFeature.IsDynamicCodeCompiled)
        {
            var defaultValue = propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) is null
                ? RuntimeHelpers.GetUninitializedObject(propertyType)
                : null;
            return (instance, value) => property.SetValue(instance, value ?? defaultValue);
        }

        var target = Expression.Parameter(typeof(object), "instance");
        var input = Expression.Parameter(typeof(object), "value");
        var converted = Expression.Condition(
            Expression.Equal(input, Expression.Constant(null)),
            Expression.Default(propertyType),
            Expression.Convert(input, propertyType));
        var body = Expression.Call(Expression.Convert(target, property.DeclaringType!), setMethod, converted);
        return Expression.Lambda<Action<object, object?>>(body, target, input).Compile();
    }
}
