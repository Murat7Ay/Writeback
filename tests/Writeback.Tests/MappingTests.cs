using System;
using System.Linq;
using Writeback.Execution;
using Writeback.Mapping;

namespace Writeback.Tests;

public class MappingTests
{
    private static EntityMap Map<T>(Action<WritebackOptions>? configure = null)
    {
        var options = new WritebackOptions();
        configure?.Invoke(options);
        return new EntityModel(options).GetMap(typeof(T));
    }

    [Fact]
    public void Conventions_detect_id_key_as_identity_and_table_from_type_name()
    {
        var map = Map<Customer>();

        Assert.Equal("Customer", map.TableName);
        Assert.Null(map.Schema);
        var key = Assert.Single(map.Keys);
        Assert.Equal("Id", key.PropertyName);
        Assert.True(key.IsIdentity);
        Assert.Same(key, map.Identity);
    }

    [Fact]
    public void NotMapped_properties_are_excluded()
    {
        var map = Map<Customer>();

        Assert.Null(map.FindByProperty(nameof(Customer.DisplayName)));
        Assert.Equal(new[] { "Id", "Name", "Email", "CreatedAt" }, map.Columns.Select(c => c.PropertyName));
    }

    [Fact]
    public void DatabaseGenerated_identity_on_non_key_is_read_back_after_insert_and_never_written()
    {
        var map = Map<Customer>();
        var createdAt = map.FindByProperty(nameof(Customer.CreatedAt))!;

        Assert.Equal(ValueGeneration.OnInsert, createdAt.Generation);
        Assert.DoesNotContain(createdAt, map.InsertColumns);
        Assert.DoesNotContain(createdAt, map.UpdateColumns);
        Assert.Equal(new[] { "Id", "CreatedAt" }, map.InsertReadback.Select(c => c.PropertyName));
        Assert.Empty(map.UpdateReadback);
    }

    [Fact]
    public void Table_attribute_schema_column_rename_and_timestamp_token()
    {
        var map = Map<Order>();

        Assert.Equal("orders", map.TableName);
        Assert.Equal("sales", map.Schema);
        Assert.Equal("customer_id", map.FindByProperty(nameof(Order.CustomerId))!.ColumnName);
        Assert.True(map.FindByProperty(nameof(Order.CustomerId))!.NeedsAlias);

        var token = map.ConcurrencyToken!;
        Assert.Equal(nameof(Order.RowVersion), token.PropertyName);
        Assert.Equal(ConcurrencyToken.RowVersion, token.ConcurrencyToken);
        Assert.Equal(ValueGeneration.OnInsertAndUpdate, token.Generation);
        Assert.Contains(token, map.UpdateReadback);
    }

    [Fact]
    public void Explicit_int_key_is_identity_by_convention()
    {
        var key = Assert.Single(Map<Order>().Keys);
        Assert.True(key.IsIdentity);
    }

    [Fact]
    public void Composite_keys_are_never_identity_and_concurrency_check_int_is_version_counter()
    {
        var map = Map<OrderLine>();

        Assert.Equal(new[] { "OrderId", "LineNumber" }, map.Keys.Select(k => k.PropertyName));
        Assert.All(map.Keys, k => Assert.False(k.IsIdentity));
        Assert.Null(map.Identity);
        Assert.Equal(ConcurrencyToken.VersionCounter, map.ConcurrencyToken!.ConcurrencyToken);
        Assert.Contains(map.ConcurrencyToken, map.InsertColumns);
        Assert.DoesNotContain(map.ConcurrencyToken, map.UpdateColumns);
    }

    [Fact]
    public void Guid_key_is_application_assigned()
    {
        var map = Map<GuidKeyed>();
        var key = Assert.Single(map.Keys);
        Assert.Equal(ValueGeneration.None, key.Generation);
        Assert.False(key.IsIdentity);
        Assert.Contains(key, map.InsertColumns);
    }

    [Fact]
    public void Editable_and_computed_attributes()
    {
        var map = Map<Document>();
        var createdBy = map.FindByProperty(nameof(Document.CreatedBy))!;
        var syncedBy = map.FindByProperty(nameof(Document.SyncedBy))!;
        var slug = map.FindByProperty(nameof(Document.Slug))!;

        Assert.True(createdBy.IsInsertOnly);
        Assert.Contains(createdBy, map.InsertColumns);
        Assert.DoesNotContain(createdBy, map.UpdateColumns);

        Assert.True(syncedBy.IsReadOnly);
        Assert.DoesNotContain(syncedBy, map.InsertColumns);
        Assert.DoesNotContain(syncedBy, map.UpdateColumns);
        Assert.DoesNotContain(syncedBy, map.InsertReadback);

        Assert.Equal(ValueGeneration.OnInsertAndUpdate, slug.Generation);
        Assert.Contains(slug, map.UpdateReadback);
    }

    [Fact]
    public void Type_name_id_convention()
    {
        var key = Assert.Single(Map<Widget>().Keys);
        Assert.Equal("WidgetId", key.PropertyName);
    }

    [Fact]
    public void Keyless_entities_map_but_key_operations_explain_how_to_add_a_key()
    {
        var map = Map<AuditLog>();
        Assert.Empty(map.Keys);

        var ex = Assert.Throws<EntityMappingException>(() => map.EnsureHasKey("UpdateAsync"));
        Assert.Contains("[Key]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("HasKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enums_and_nullable_enums_are_supported()
    {
        Assert.Equal(3, Map<WithEnum>().Columns.Count);
    }

    [Fact]
    public void Positional_records_map_with_init_setters()
    {
        var map = Map<ProductRecord>();
        Assert.Equal(new[] { "Id", "Name" }, map.Columns.Select(c => c.PropertyName));

        var record = new ProductRecord(0, "x");
        map.Identity!.Setter!(record, 42);
        Assert.Equal(42, record.Id);
    }

    [Fact]
    public void Derived_new_property_replaces_base_property()
    {
        var map = Map<DerivedEntity>();
        Assert.Equal(new[] { "Id", "Name", "Extra" }, map.Columns.Select(c => c.PropertyName));
        Assert.Equal(typeof(DerivedEntity), map.FindByProperty("Name")!.Property.DeclaringType);
    }

    [Fact]
    public void Snake_case_convention_applies_to_tables_and_columns_but_not_explicit_names()
    {
        var map = Map<SnakeCaseEntity>(o => o.NamingConvention = NamingConvention.SnakeCase);
        Assert.Equal("snake_case_entity", map.TableName);
        Assert.Equal(new[] { "id", "first_name", "created_at" }, map.Columns.Select(c => c.ColumnName));

        var order = Map<Order>(o => o.NamingConvention = NamingConvention.SnakeCase);
        Assert.Equal("orders", order.TableName);
        Assert.Equal("customer_id", order.FindByProperty("CustomerId")!.ColumnName);
        Assert.Equal("order_number", order.FindByProperty("OrderNumber")!.ColumnName);
    }

    [Theory]
    [InlineData("Id", "id")]
    [InlineData("CreatedAt", "created_at")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("CustomerID", "customer_id")]
    [InlineData("Address2Line", "address2_line")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("IOStream", "io_stream")]
    public void Snake_case(string input, string expected) => Assert.Equal(expected, NamingConvention.ToSnakeCase(input));

    [Fact]
    public void Fluent_configuration_overrides_attributes_and_conventions()
    {
        var map = Map<Order>(o => o.Entity<Order>(e =>
        {
            e.ToTable("order_header");
            e.Property(x => x.CustomerId).HasColumnName("cust");
            e.Property(x => x.OrderNumber).ValueGeneratedNever();
            e.Ignore(x => x.Total);
            e.HasTriggers();
        }));

        Assert.Equal("order_header", map.TableName);
        Assert.Null(map.Schema);
        Assert.Equal("cust", map.FindByProperty("CustomerId")!.ColumnName);
        Assert.False(map.Keys[0].IsIdentity);
        Assert.Null(map.FindByProperty("Total"));
        Assert.True(map.HasTriggers);
    }

    [Fact]
    public void Fluent_composite_key_and_version_counter()
    {
        var map = Map<AuditLog>(o => o.Entity<AuditLog>(e =>
        {
            e.HasKey(x => new { x.Message, x.At });
        }));

        Assert.Equal(new[] { "Message", "At" }, map.Keys.Select(k => k.PropertyName));
    }

    [Fact]
    public void HasTriggers_attribute()
    {
        Assert.True(Map<Triggered>().HasTriggers);
        Assert.False(Map<Customer>().HasTriggers);
    }

    [Fact]
    public void Navigation_and_complex_properties_are_rejected_with_guidance_listing_every_problem()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<WithNavigation>());

        Assert.Equal(2, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("'Lines'", StringComparison.Ordinal) && p.Contains("relationships", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("'Customer'", StringComparison.Ordinal) && p.Contains("[NotMapped]", StringComparison.Ordinal));
        Assert.Equal(typeof(WithNavigation), ex.EntityType);
    }

    [Fact]
    public void Ignoring_navigation_fluently_makes_it_valid()
    {
        var map = Map<WithNavigation>(o => o.Entity<WithNavigation>(e =>
        {
            e.Ignore(x => new { x.Lines, x.Customer });
        }));

        Assert.Single(map.Columns);
    }

    [Fact]
    public void Dotted_table_names_suggest_schema()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<DottedTable>());
        Assert.Contains("Schema = \"dbo\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_property_without_setter_is_rejected()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<GeneratedWithoutSetter>());
        Assert.Contains("no public setter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_integer_version_counter_is_rejected()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<StringVersion>());
        Assert.Contains("[Timestamp]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_tokens_are_rejected()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<TwoTokens>());
        Assert.Contains("only one concurrency token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composite_identity_is_rejected()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<CompositeIdentity>());
        Assert.Contains("composite keys cannot be database-generated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_column_names_are_ambiguous()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<DuplicateColumns>());
        Assert.Contains("ambiguous", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluent_reference_to_unknown_property_and_ignored_configured_conflict()
    {
        var ex = Assert.Throws<EntityMappingException>(() => Map<Customer>(o => o.Entity<Customer>(e =>
        {
            e.Property(x => x.DisplayName).HasColumnName("display");
        })));

        Assert.Contains("ignored but also configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_must_reference_direct_properties()
    {
        var options = new WritebackOptions();
        Assert.Throws<ArgumentException>(() => options.Entity<Customer>(e => e.Property(x => x.Name.Length)));
        Assert.Throws<ArgumentException>(() => options.Entity<Customer>(e => e.HasKey(x => x.Name + "1")));
    }

    [Fact]
    public void Maps_are_cached_per_model()
    {
        var model = new EntityModel(new WritebackOptions());
        Assert.Same(model.GetMap(typeof(Customer)), model.GetMap(typeof(Customer)));
    }
}
