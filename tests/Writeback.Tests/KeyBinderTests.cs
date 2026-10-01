using System;
using Writeback.Execution;
using Writeback.Mapping;

namespace Writeback.Tests;

public class KeyBinderTests
{
    private static readonly EntityModel Model = new(new WritebackOptions());

    private static EntityMap Map<T>() => Model.GetMap(typeof(T));

    [Fact]
    public void Single_key_accepts_raw_value_entity_or_anonymous_object()
    {
        Assert.Equal(new object?[] { 5L }, KeyBinder.GetKeyValues(Map<Customer>(), 5L, "key"));
        Assert.Equal(new object?[] { 7L }, KeyBinder.GetKeyValues(Map<Customer>(), new Customer { Id = 7 }, "key"));
        Assert.Equal(new object?[] { 9 }, KeyBinder.GetKeyValues(Map<Customer>(), new { Id = 9 }, "key"));
    }

    [Fact]
    public void Composite_key_accepts_object_with_matching_properties()
    {
        var values = KeyBinder.GetKeyValues(Map<OrderLine>(), new { LineNumber = 2, OrderId = 1 }, "key");
        Assert.Equal(new object?[] { 1, 2 }, values);
    }

    [Fact]
    public void Composite_key_with_wrong_shape_explains_expected_shape()
    {
        var ex = Assert.Throws<ArgumentException>(() => KeyBinder.GetKeyValues(Map<OrderLine>(), 5, "key"));
        Assert.Contains("composite key (OrderId, LineNumber)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("new { OrderId = ..., LineNumber = ... }", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => KeyBinder.GetKeyValues(Map<OrderLine>(), new { OrderId = 1 }, "key"));
    }

    [Fact]
    public void Null_keys_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => KeyBinder.GetKeyValues(Map<Customer>(), null!, "key"));
        Assert.Throws<ArgumentException>(() => KeyBinder.ToList(new object?[] { 1, null }, "keys"));
    }
}

public class EntitySqlTests
{
    [Fact]
    public void Fragments_are_quoted_and_aliased()
    {
        var sql = EntitySql.For<Order>(SqlDialect.PostgreSql);

        Assert.Equal("\"sales\".\"orders\"", sql.Table);
        Assert.Equal("\"customer_id\"", sql.Column(o => o.CustomerId));
        Assert.Equal("\"OrderNumber\", \"customer_id\" AS \"CustomerId\", \"Total\", \"RowVersion\"", sql.Columns);
        Assert.StartsWith("INSERT INTO \"sales\".\"orders\"", sql.Insert, StringComparison.Ordinal);
        Assert.StartsWith("UPDATE", sql.Update, StringComparison.Ordinal);
        Assert.StartsWith("DELETE", sql.Delete, StringComparison.Ordinal);
        Assert.StartsWith("SELECT", sql.SelectByKey, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => sql.Column("Nope"));
    }
}

[Collection("Dapper global type map")]
public class BatchParameterTypingTests
{
    [Fact]
    public void Batch_parameters_follow_Dappers_type_map_including_overrides()
    {
        var map = new Execution.EntityModel(new WritebackOptions()).GetMap(typeof(TypedRow));
        var at = map.FindByProperty(nameof(TypedRow.At))!;

        var parameter = new Microsoft.Data.SqlClient.SqlParameter();
        Execution.BatchParameters.Assign(parameter, at, new DateTime(2026, 1, 1));
        Assert.Equal(System.Data.DbType.DateTime, parameter.DbType);

        Dapper.SqlMapper.AddTypeMap(typeof(DateTime), System.Data.DbType.DateTime2);
        try
        {
            var overridden = new Microsoft.Data.SqlClient.SqlParameter();
            Execution.BatchParameters.Assign(overridden, at, new DateTime(2026, 1, 1));
            Assert.Equal(System.Data.DbType.DateTime2, overridden.DbType);
            Assert.Equal(System.Data.DbType.DateTime2, Mapping.TypeSupport.GetDbTypeForNull(typeof(DateTime?)));
        }
        finally
        {
            Dapper.SqlMapper.AddTypeMap(typeof(DateTime), System.Data.DbType.DateTime);
        }

        var status = new Microsoft.Data.SqlClient.SqlParameter();
        Execution.BatchParameters.Assign(status, map.FindByProperty(nameof(TypedRow.Status))!, Status.Archived);
        Assert.Equal(1, status.Value);
        Assert.Equal(System.Data.DbType.Int32, status.DbType);
    }

    public class TypedRow
    {
        public int Id { get; set; }

        public DateTime At { get; set; }

        public Status Status { get; set; }
    }
}
