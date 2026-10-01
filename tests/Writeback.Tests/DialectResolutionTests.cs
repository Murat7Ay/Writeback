using System;
using System.Data;
using System.Data.Common;
using Writeback.Execution;

namespace Writeback.Tests
{
    public class DialectResolutionTests
    {
        private static SqlDialect Resolve(IDbConnection connection, Action<WritebackOptions>? configure = null)
        {
            var options = new WritebackOptions();
            configure?.Invoke(options);
            return new EntityModel(options).ResolveDialect(connection);
        }

        [Fact]
        public void Known_providers_are_detected_by_exact_type_name()
        {
            Assert.Same(SqlDialect.SqlServer, Resolve(new Microsoft.Data.SqlClient.SqlConnection()));
            Assert.Same(SqlDialect.PostgreSql, Resolve(new Npgsql.NpgsqlConnection()));
            Assert.Same(SqlDialect.MySql, Resolve(new MySqlConnector.MySqlConnection()));
            Assert.Same(SqlDialect.Sqlite, Resolve(new Microsoft.Data.Sqlite.SqliteConnection()));
        }

        [Fact]
        public void Subclasses_of_known_providers_are_detected()
        {
            Assert.Same(SqlDialect.Sqlite, Resolve(new CustomSqlite()));
        }

        [Fact]
        public void Wrapping_connections_are_unwrapped()
        {
            Assert.Same(SqlDialect.Sqlite, Resolve(new ProfiledConnection(new Microsoft.Data.Sqlite.SqliteConnection())));
        }

        [Fact]
        public void Unknown_connections_explain_how_to_register()
        {
            var ex = Assert.Throws<DialectNotFoundException>(() => Resolve(new UnknownConnection()));
            Assert.Contains("UseDialect<UnknownConnection>", ex.Message, StringComparison.Ordinal);
            Assert.Equal(typeof(UnknownConnection), ex.ConnectionType);
        }

        [Fact]
        public void Explicit_registration_and_resolvers_win()
        {
            Assert.Same(SqlDialect.MySql, Resolve(new UnknownConnection(), o => o.UseDialect<UnknownConnection>(SqlDialect.MySql)));
            Assert.Same(SqlDialect.MySql, Resolve(new Npgsql.NpgsqlConnection(), o => o.UseDialect<Npgsql.NpgsqlConnection>(SqlDialect.MySql)));
            Assert.Same(SqlDialect.SqlServer, Resolve(new UnknownConnection(), o => o.UseDialectResolver(_ => SqlDialect.SqlServer)));
            Assert.Same(SqlDialect.Sqlite, Resolve(new Microsoft.Data.Sqlite.SqliteConnection(), o => o.UseDialectResolver(_ => null)));
        }

        [Fact]
        public void Async_api_requires_a_DbConnection()
        {
            var ex = Assert.Throws<ArgumentException>(() => Executor.AsDbConnection(new LegacyConnection()));
            Assert.Contains("DbConnection", ex.Message, StringComparison.Ordinal);
        }

        private sealed class CustomSqlite : Microsoft.Data.Sqlite.SqliteConnection
        {
        }

        private sealed class ProfiledConnection : FakeConnectionBase
        {
            public ProfiledConnection(DbConnection inner)
            {
                WrappedConnection = inner;
            }

            public DbConnection WrappedConnection { get; }
        }

        private sealed class UnknownConnection : FakeConnectionBase
        {
        }
    }

    public abstract class FakeConnectionBase : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "";

        public override string DataSource => "";

        public override string ServerVersion => "";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    public sealed class LegacyConnection : IDbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string ConnectionString { get; set; } = "";

        public int ConnectionTimeout => 0;

        public string Database => "";

        public ConnectionState State => ConnectionState.Closed;

        public IDbTransaction BeginTransaction() => throw new NotSupportedException();

        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();

        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public void Close()
        {
        }

        public IDbCommand CreateCommand() => throw new NotSupportedException();

        public void Open() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
