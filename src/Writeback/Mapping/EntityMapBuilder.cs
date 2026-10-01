using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;

namespace Writeback.Mapping;

/// <summary>
/// Builds an <see cref="EntityMap"/> from (highest precedence first) fluent configuration, DataAnnotations
/// attributes and conventions. Collects every problem before throwing so one run reports all of them.
/// </summary>
internal static class EntityMapBuilder
{
    public static EntityMap Build(Type entityType, EntityConfiguration? config, NamingConvention naming)
    {
        var problems = new List<string>();

        if (entityType.IsValueType || entityType.IsAbstract || entityType.IsGenericTypeDefinition)
        {
            problems.Add("entities must be concrete classes (generated values are written back into the instance).");
            throw new EntityMappingException(entityType, problems);
        }

        var tableAttribute = entityType.GetCustomAttribute<TableAttribute>(inherit: true);
        string tableName;
        string? schema;
        if (config?.TableConfigured == true)
        {
            tableName = config.TableName!;
            schema = config.Schema;
        }
        else if (tableAttribute is not null)
        {
            tableName = tableAttribute.Name;
            schema = tableAttribute.Schema;
        }
        else
        {
            tableName = naming.ToTableName(entityType.Name);
            schema = null;
        }

        if (schema is null && tableName.Contains('.', StringComparison.Ordinal))
        {
            var dot = tableName.IndexOf('.', StringComparison.Ordinal);
            problems.Add(
                $"table name '{tableName}' contains a '.'. Names are always quoted, so this would address a table literally named "
                + $"'{tableName}'. Specify the schema separately: [Table(\"{tableName[(dot + 1)..]}\", Schema = \"{tableName[..dot]}\")] "
                + $"or e.ToTable(\"{tableName[(dot + 1)..]}\", \"{tableName[..dot]}\").");
        }

        var hasTriggers = config?.HasTriggers ?? entityType.IsDefined(typeof(HasTriggersAttribute), inherit: true);

        var properties = GetCandidateProperties(entityType);
        var knownNames = new HashSet<string>(properties.Select(p => p.Name), StringComparer.Ordinal);
        if (config is not null)
        {
            foreach (var name in config.Properties.Keys.Concat(config.Ignored).Concat(config.KeyProperties ?? Array.Empty<string>()))
            {
                if (!knownNames.Contains(name))
                {
                    problems.Add($"fluent configuration refers to '{name}', which is not a public readable instance property.");
                }
            }
        }

        var drafts = new List<ColumnDraft>();
        foreach (var property in properties)
        {
            var propertyConfig = config?.Properties.GetValueOrDefault(property.Name);
            var ignoredByAttribute = property.IsDefined(typeof(NotMappedAttribute), inherit: true);
            var ignoredFluently = config?.Ignored.Contains(property.Name) == true;

            if (ignoredByAttribute || ignoredFluently)
            {
                if (propertyConfig is not null)
                {
                    problems.Add($"property '{property.Name}' is ignored but also configured with e.Property(...). Remove one of the two.");
                }

                continue;
            }

            if (!TypeSupport.IsSupported(property.PropertyType))
            {
                var hint = TypeSupport.IsCollection(property.PropertyType)
                    ? "Collections are not columns: relationships are loaded with explicit SQL, not mapped."
                    : "Complex types are not flattened.";
                problems.Add(
                    $"property '{property.Name}' has type '{property.PropertyType.Name}', which Dapper cannot bind as a parameter. "
                    + $"{hint} Mark it [NotMapped] / e.Ignore(x => x.{property.Name}), or register a Dapper type handler "
                    + "(SqlMapper.AddTypeHandler) before first use.");
                continue;
            }

            drafts.Add(CreateDraft(property, propertyConfig, naming, problems));
        }

        if (drafts.Count == 0)
        {
            problems.Add("no mappable properties were found. Mapped properties must be public, readable and non-indexed.");
            throw new EntityMappingException(entityType, problems);
        }

        ResolveKeys(entityType, config, drafts, problems);
        Validate(drafts, problems);

        if (problems.Count > 0)
        {
            throw new EntityMappingException(entityType, problems);
        }

        var columns = drafts
            .Select((d, i) => new ColumnMap(d.Property, d.ColumnName, i, d.IsKey, d.Generation ?? ValueGeneration.None, d.Token, d.InsertOnly, d.ReadOnly))
            .ToArray();
        return new EntityMap(entityType, tableName, schema, columns, hasTriggers);
    }

    private static List<PropertyInfo> GetCandidateProperties(Type type)
    {
        // Base-class properties first, each type in declaration order. A property redeclared lower in the hierarchy
        // ('new' or 'override') keeps the base position but uses the most derived declaration.
        var hierarchy = new Stack<Type>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var result = new List<PropertyInfo>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            var declaring = hierarchy.Pop();
            foreach (var property in declaring.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true })
                {
                    continue;
                }

                if (positions.TryGetValue(property.Name, out var index))
                {
                    result[index] = property;
                }
                else
                {
                    positions[property.Name] = result.Count;
                    result.Add(property);
                }
            }
        }

        return result;
    }

    private static ColumnDraft CreateDraft(PropertyInfo property, PropertyConfiguration? config, NamingConvention naming, List<string> problems)
    {
        var columnAttribute = property.GetCustomAttribute<ColumnAttribute>(inherit: true);
        var columnName = config?.ColumnName ?? columnAttribute?.Name ?? naming.ToColumnName(property.Name);

        ValueGeneration? generation = config?.Generation;
        var databaseGenerated = property.GetCustomAttribute<DatabaseGeneratedAttribute>(inherit: true);
        var isTimestamp = property.IsDefined(typeof(TimestampAttribute), inherit: true);
        if (generation is null && databaseGenerated is not null)
        {
            generation = databaseGenerated.DatabaseGeneratedOption switch
            {
                DatabaseGeneratedOption.Identity => ValueGeneration.OnInsert,
                DatabaseGeneratedOption.Computed => ValueGeneration.OnInsertAndUpdate,
                _ => ValueGeneration.None,
            };
        }

        if (generation is null && isTimestamp)
        {
            generation = ValueGeneration.OnInsertAndUpdate;
        }

        var token = config?.Token ?? ConcurrencyToken.None;
        if (config?.Token is null)
        {
            if (isTimestamp)
            {
                token = ConcurrencyToken.RowVersion;
            }
            else if (property.IsDefined(typeof(ConcurrencyCheckAttribute), inherit: true))
            {
                token = generation == ValueGeneration.OnInsertAndUpdate ? ConcurrencyToken.RowVersion : ConcurrencyToken.VersionCounter;
            }
        }

        var insertOnly = config?.InsertOnly ?? false;
        var readOnly = config?.ReadOnly ?? false;
        var editable = property.GetCustomAttribute<EditableAttribute>(inherit: true);
        if (editable is { AllowEdit: false } && config?.InsertOnly is null && config?.ReadOnly is null)
        {
            insertOnly = editable.AllowInitialValue;
            readOnly = !editable.AllowInitialValue;
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            problems.Add($"property '{property.Name}' maps to an empty column name.");
        }

        return new ColumnDraft(property, columnName)
        {
            Generation = generation,
            Token = token,
            InsertOnly = insertOnly,
            ReadOnly = readOnly,
            IsKeyByAttribute = property.IsDefined(typeof(KeyAttribute), inherit: true),
        };
    }

    private static void ResolveKeys(Type entityType, EntityConfiguration? config, List<ColumnDraft> drafts, List<string> problems)
    {
        List<ColumnDraft> keys;
        if (config?.KeyProperties is { } keyNames)
        {
            keys = new List<ColumnDraft>();
            foreach (var name in keyNames)
            {
                var draft = drafts.Find(d => d.Property.Name == name);
                if (draft is null)
                {
                    problems.Add($"key property '{name}' is not mapped (it is ignored, unsupported or missing).");
                }
                else
                {
                    keys.Add(draft);
                }
            }
        }
        else
        {
            keys = drafts.Where(d => d.IsKeyByAttribute).ToList();
            if (keys.Count == 0)
            {
                var conventional = drafts.Find(d => string.Equals(d.Property.Name, "Id", StringComparison.OrdinalIgnoreCase))
                                   ?? drafts.Find(d => string.Equals(d.Property.Name, entityType.Name + "Id", StringComparison.OrdinalIgnoreCase));
                if (conventional is not null)
                {
                    keys.Add(conventional);
                }
            }
        }

        foreach (var key in keys)
        {
            key.IsKey = true;
        }

        // Convention (same as EF Core): a single integer key without explicit configuration is database-generated.
        if (keys.Count == 1 && keys[0].Generation is null && TypeSupport.IsInteger(keys[0].Property.PropertyType))
        {
            keys[0].Generation = ValueGeneration.OnInsert;
        }
    }

    private static void Validate(List<ColumnDraft> drafts, List<string> problems)
    {
        var keys = drafts.Where(d => d.IsKey).ToList();
        if (keys.Count > 1 && keys.Any(k => k.Generation == ValueGeneration.OnInsert))
        {
            problems.Add("composite keys cannot be database-generated. Remove [DatabaseGenerated(Identity)] from the key parts "
                         + "or use a single surrogate key.");
        }

        foreach (var draft in drafts)
        {
            var name = draft.Property.Name;
            var generation = draft.Generation ?? ValueGeneration.None;

            if (draft.IsKey && generation == ValueGeneration.OnInsertAndUpdate)
            {
                problems.Add($"key '{name}' cannot be generated on update; keys must be stable.");
            }

            if (draft.IsKey && (draft.InsertOnly || draft.ReadOnly))
            {
                problems.Add($"key '{name}' cannot be insert-only or read-only; keys are never updated anyway.");
            }

            if (generation != ValueGeneration.None && draft.Property.SetMethod is not { IsPublic: true })
            {
                problems.Add($"'{name}' is database-generated, so its value is read back into the entity, but it has no public setter. "
                             + "Add a setter (init is fine) or mark it [NotMapped].");
            }

            if (generation != ValueGeneration.None && (draft.InsertOnly || draft.ReadOnly))
            {
                problems.Add($"'{name}' is database-generated and also insert-only/read-only. Choose one.");
            }

            switch (draft.Token)
            {
                case ConcurrencyToken.VersionCounter:
                    if (!TypeSupport.IsInteger(draft.Property.PropertyType))
                    {
                        problems.Add($"'{name}' is a [ConcurrencyCheck] version counter, which must be a non-nullable integer property "
                                     + $"(it is {draft.Property.PropertyType.Name}). For a database-maintained token use [Timestamp] or add "
                                     + "[DatabaseGenerated(DatabaseGeneratedOption.Computed)].");
                    }

                    if (generation != ValueGeneration.None)
                    {
                        problems.Add($"'{name}' is a version counter maintained by Writeback and cannot also be database-generated.");
                    }

                    if (draft.Property.SetMethod is not { IsPublic: true })
                    {
                        problems.Add($"version counter '{name}' needs a public setter so the new version can be written back.");
                    }

                    break;
                case ConcurrencyToken.RowVersion when generation != ValueGeneration.OnInsertAndUpdate:
                    problems.Add($"'{name}' is a row-version token, so the database must generate it on insert and update.");
                    break;
            }

            if (draft.IsKey && draft.Token != ConcurrencyToken.None)
            {
                problems.Add($"key '{name}' cannot also be a concurrency token.");
            }
        }

        if (drafts.Count(d => d.Token != ConcurrencyToken.None) > 1)
        {
            problems.Add("only one concurrency token per entity is supported (found "
                         + string.Join(", ", drafts.Where(d => d.Token != ConcurrencyToken.None).Select(d => d.Property.Name)) + ").");
        }

        foreach (var duplicate in drafts.GroupBy(d => d.ColumnName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"column '{duplicate.Key}' is mapped by more than one property ({string.Join(", ", duplicate.Select(d => d.Property.Name))}); "
                         + "the mapping would be ambiguous.");
        }
    }

    private sealed class ColumnDraft
    {
        public ColumnDraft(PropertyInfo property, string columnName)
        {
            Property = property;
            ColumnName = columnName;
        }

        public PropertyInfo Property { get; }

        public string ColumnName { get; }

        public bool IsKey { get; set; }

        public bool IsKeyByAttribute { get; init; }

        public ValueGeneration? Generation { get; set; }

        public ConcurrencyToken Token { get; init; }

        public bool InsertOnly { get; init; }

        public bool ReadOnly { get; init; }
    }
}
