using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Writeback.Mapping;

namespace Writeback.Execution;

/// <summary>
/// Turns a caller-supplied key into key values. Accepted shapes: the raw value (single keys), an instance of the entity,
/// or any object with properties named like the key properties (e.g. <c>new { OrderId = 1, LineNumber = 2 }</c>).
/// </summary>
internal static class KeyBinder
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type KeyType, string Property), PropertyInfo?> Properties = new();

    public static object?[] GetKeyValues(EntityMap map, object key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        var keys = map.Keys;
        var keyType = key.GetType();

        if (map.EntityType.IsAssignableFrom(keyType))
        {
            var fromEntity = new object?[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                fromEntity[i] = keys[i].Getter(key);
            }

            return fromEntity;
        }

        if (keys.Count == 1 && !IsAnonymous(keyType))
        {
            return new[] { key };
        }

        var values = new object?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var property = Properties.GetOrAdd((keyType, keys[i].PropertyName), k => k.KeyType.GetProperty(k.Property, BindingFlags.Public | BindingFlags.Instance));
            if (property is null)
            {
                throw new ArgumentException(DescribeExpectedKey(map) + $" '{keyType.Name}' has no property '{keys[i].PropertyName}'.", parameterName);
            }

            values[i] = property.GetValue(key);
        }

        return values;
    }

    public static BatchParameters ToParameters(EntityMap map, object key, string parameterName)
    {
        var values = GetKeyValues(map, key, parameterName);
        var parameters = new BatchParameters(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            Executor.AddParameter(parameters, map.Keys[i].PropertyName, map.Keys[i], values[i]);
        }

        return parameters;
    }

    public static List<object> ToList(System.Collections.IEnumerable keys, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(keys, parameterName);
        var list = new List<object>();
        foreach (var key in keys)
        {
            list.Add(key ?? throw new ArgumentException("Keys must not contain null.", parameterName));
        }

        return list;
    }

    private static string DescribeExpectedKey(EntityMap map)
    {
        var names = string.Join(", ", map.Keys.Select(k => k.PropertyName));
        var example = string.Join(", ", map.Keys.Select(k => k.PropertyName + " = ..."));
        return map.Keys.Count == 1
            ? $"'{map.EntityType.Name}' has key {names}; pass the key value, an entity, or new {{ {example} }}."
            : $"'{map.EntityType.Name}' has a composite key ({names}); pass an entity or an object such as new {{ {example} }}.";
    }

    private static bool IsAnonymous(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && type.Name.Contains("AnonymousType", StringComparison.Ordinal);
}
