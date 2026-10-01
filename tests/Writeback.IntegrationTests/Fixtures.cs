using System;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Writeback.IntegrationTests;

/// <summary>Configures Writeback once for the whole test assembly.</summary>
public static class IntegrationSetup
{
    private static readonly Lazy<bool> Configured = new(() =>
    {
        // SQLite stores Guids as TEXT, which plain Dapper cannot materialize; SQLite apps register a handler like this.
        // It only changes parsing, so it is harmless for the other providers sharing this test process.
        SqlMapper.AddTypeHandler(new LenientGuidHandler());
        WritebackConfig.Configure(o => o
            .Entity<FluentProduct>(e =>
            {
                e.ToTable("dx_fluent_product");
                e.Property(p => p.Name).HasColumnName("product_name");
                e.Property(p => p.Price).HasColumnName("unit_price");
            }));
        return true;
    });

    public static void EnsureConfigured() => _ = Configured.Value;

    private sealed class LenientGuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, Guid value) => parameter.Value = value;

        public override Guid Parse(object value) => value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes => new Guid(bytes),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Guid."),
        };
    }
}

/// <summary>
/// A real database. Docker-backed fixtures start a Testcontainers container unless an environment variable supplies a
/// connection string (for CI service containers); when neither works the tests are skipped, not failed.
/// </summary>
public abstract class DatabaseFixture : IAsyncLifetime
{
    protected DatabaseFixture()
    {
        IntegrationSetup.EnsureConfigured();
    }

    public abstract string Name { get; }

    public string? SkipReason { get; private set; }

    protected string ConnectionString { get; set; } = "";

    public abstract DbConnection CreateConnection();

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            foreach (var statement in SchemaStatements)
            {
                await connection.ExecuteAsync(statement);
            }
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            SkipReason = $"{Name} unavailable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
        }
    }

    protected abstract Task StartAsync();

    protected abstract string[] SchemaStatements { get; }

    private static bool IsInfrastructureFailure(Exception ex) =>
        ex is not DbException || ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase);
}

public sealed class SqliteFixture : DatabaseFixture
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dx-tests-{Guid.NewGuid():N}.db");

    public override string Name => "SQLite";

    public override DbConnection CreateConnection() => new SqliteConnection(ConnectionString);

    protected override Task StartAsync()
    {
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // best effort
        }

        return base.DisposeAsync();
    }

    protected override string[] SchemaStatements =>
    [
        """
        CREATE TABLE dx_customer (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Email TEXT NULL,
            Status INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            NameUpper TEXT GENERATED ALWAYS AS (upper(Name)) STORED)
        """,
        "CREATE TABLE dx_doc (Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Version INTEGER NOT NULL)",
        "CREATE TABLE dx_order_line (OrderId INTEGER NOT NULL, LineNumber INTEGER NOT NULL, Product TEXT NOT NULL, Quantity INTEGER NOT NULL, PRIMARY KEY (OrderId, LineNumber))",
        "CREATE TABLE dx_fluent_product (Id INTEGER PRIMARY KEY AUTOINCREMENT, product_name TEXT NOT NULL, unit_price NUMERIC NOT NULL)",
        "CREATE TABLE \"order\" (Id INTEGER PRIMARY KEY AUTOINCREMENT, \"select\" TEXT NOT NULL, \"from\" TEXT NULL)",
        "CREATE TABLE dx_audit (Message TEXT NOT NULL, Level INTEGER NOT NULL)",
        "CREATE TABLE dx_product_record (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)",
    ];
}

public sealed class PostgreSqlFixture : DatabaseFixture
{
    private PostgreSqlContainer? _container;

    public override string Name => "PostgreSQL";

    public override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable("WRITEBACK_POSTGRES") is { Length: > 0 } cs)
        {
            ConnectionString = cs;
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override string[] SchemaStatements =>
    [
        "DROP TABLE IF EXISTS dx_customer, dx_doc, dx_order_line, dx_fluent_product, \"order\", dx_audit, dx_product_record",
        """
        CREATE TABLE dx_customer (
            "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "Name" varchar(200) NOT NULL,
            "Email" varchar(200) NULL,
            "Status" int NOT NULL,
            "CreatedAt" timestamp NOT NULL DEFAULT now(),
            "NameUpper" varchar(200) GENERATED ALWAYS AS (upper("Name")) STORED)
        """,
        "CREATE TABLE dx_doc (\"Id\" uuid PRIMARY KEY, \"Title\" text NOT NULL, \"Version\" int NOT NULL)",
        "CREATE TABLE dx_order_line (\"OrderId\" int NOT NULL, \"LineNumber\" int NOT NULL, \"Product\" text NOT NULL, \"Quantity\" int NOT NULL, PRIMARY KEY (\"OrderId\", \"LineNumber\"))",
        "CREATE TABLE dx_fluent_product (\"Id\" int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, product_name text NOT NULL, unit_price numeric(18,2) NOT NULL)",
        "CREATE TABLE \"order\" (\"Id\" int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, \"select\" text NOT NULL, \"from\" text NULL)",
        "CREATE TABLE dx_audit (\"Message\" text NOT NULL, \"Level\" int NOT NULL)",
        "CREATE TABLE dx_product_record (\"Id\" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, \"Name\" text NOT NULL)",
    ];
}

public sealed class SqlServerFixture : DatabaseFixture
{
    private MsSqlContainer? _container;

    public override string Name => "SQL Server";

    public override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable("WRITEBACK_SQLSERVER") is { Length: > 0 } cs)
        {
            ConnectionString = cs;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override string[] SchemaStatements =>
    [
        "DROP TABLE IF EXISTS dx_customer, dx_doc, dx_order_line, dx_fluent_product, [order], dx_audit, dx_product_record, dx_triggered, dx_trigger_audit, dx_rowversioned",
        """
        CREATE TABLE dx_customer (
            Id bigint IDENTITY(1,1) PRIMARY KEY,
            Name nvarchar(200) NOT NULL,
            Email nvarchar(200) NULL,
            Status int NOT NULL,
            CreatedAt datetime2 NOT NULL DEFAULT sysutcdatetime(),
            NameUpper AS UPPER(Name))
        """,
        "CREATE TABLE dx_doc (Id uniqueidentifier PRIMARY KEY, Title nvarchar(200) NOT NULL, Version int NOT NULL)",
        "CREATE TABLE dx_order_line (OrderId int NOT NULL, LineNumber int NOT NULL, Product nvarchar(200) NOT NULL, Quantity int NOT NULL, PRIMARY KEY (OrderId, LineNumber))",
        "CREATE TABLE dx_fluent_product (Id int IDENTITY(1,1) PRIMARY KEY, product_name nvarchar(200) NOT NULL, unit_price decimal(18,2) NOT NULL)",
        "CREATE TABLE [order] (Id int IDENTITY(1,1) PRIMARY KEY, [select] nvarchar(200) NOT NULL, [from] nvarchar(200) NULL)",
        "CREATE TABLE dx_audit (Message nvarchar(200) NOT NULL, Level int NOT NULL)",
        "CREATE TABLE dx_product_record (Id bigint IDENTITY(1,1) PRIMARY KEY, Name nvarchar(200) NOT NULL)",
        "CREATE TABLE dx_trigger_audit (Id int IDENTITY(1,1) PRIMARY KEY, TriggeredId int NOT NULL)",
        "CREATE TABLE dx_triggered (Id int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(200) NOT NULL, ModifiedAt datetime2 NOT NULL DEFAULT '2000-01-01')",
        // An AFTER trigger that writes other rows (distorting the provider's affected-row count) and modifies the row itself
        // (so OUTPUT would report a stale value even if it were allowed).
        """
        CREATE TRIGGER dx_triggered_touch ON dx_triggered AFTER INSERT, UPDATE AS
        BEGIN
            UPDATE t SET ModifiedAt = sysutcdatetime() FROM dx_triggered t JOIN inserted i ON t.Id = i.Id;
            INSERT INTO dx_trigger_audit (TriggeredId) SELECT Id FROM inserted;
            INSERT INTO dx_trigger_audit (TriggeredId) SELECT Id FROM inserted;
        END
        """,
        "CREATE TABLE dx_rowversioned (Id int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(200) NOT NULL, RowVersion rowversion)",
    ];
}

public sealed class MySqlFixture : DatabaseFixture
{
    private MySqlContainer? _container;

    public override string Name => "MySQL";

    public override DbConnection CreateConnection() => new MySqlConnection(ConnectionString);

    protected override async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable("WRITEBACK_MYSQL") is { Length: > 0 } cs)
        {
            ConnectionString = cs;
            return;
        }

        _container = new MySqlBuilder("mysql:8.4").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override string[] SchemaStatements =>
    [
        "DROP TABLE IF EXISTS dx_customer, dx_doc, dx_order_line, dx_fluent_product, `order`, dx_audit, dx_product_record",
        """
        CREATE TABLE dx_customer (
            Id bigint AUTO_INCREMENT PRIMARY KEY,
            Name varchar(200) NOT NULL,
            Email varchar(200) NULL,
            Status int NOT NULL,
            CreatedAt datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
            NameUpper varchar(200) GENERATED ALWAYS AS (upper(Name)) STORED)
        """,
        "CREATE TABLE dx_doc (Id char(36) PRIMARY KEY, Title varchar(200) NOT NULL, Version int NOT NULL)",
        "CREATE TABLE dx_order_line (OrderId int NOT NULL, LineNumber int NOT NULL, Product varchar(200) NOT NULL, Quantity int NOT NULL, PRIMARY KEY (OrderId, LineNumber))",
        "CREATE TABLE dx_fluent_product (Id int AUTO_INCREMENT PRIMARY KEY, product_name varchar(200) NOT NULL, unit_price decimal(18,2) NOT NULL)",
        "CREATE TABLE `order` (Id int AUTO_INCREMENT PRIMARY KEY, `select` varchar(200) NOT NULL, `from` varchar(200) NULL)",
        "CREATE TABLE dx_audit (Message varchar(200) NOT NULL, Level int NOT NULL)",
        "CREATE TABLE dx_product_record (Id bigint AUTO_INCREMENT PRIMARY KEY, Name varchar(200) NOT NULL)",
    ];
}
