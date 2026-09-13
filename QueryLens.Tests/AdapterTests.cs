using QueryLens.Core;
using QueryLens.Infrastructure;

namespace QueryLens.Tests;

public sealed class AdapterTests
{
    [Theory]
    [InlineData(DatabaseDialect.MySql, typeof(MySqlDatabaseAdapter))]
    [InlineData(DatabaseDialect.PostgreSql, typeof(PostgreSqlDatabaseAdapter))]
    [InlineData(DatabaseDialect.SqlServer, typeof(SqlServerDatabaseAdapter))]
    [InlineData(DatabaseDialect.GaussDb, typeof(GaussDbAdapter))]
    public void FactoryCreatesDialectAdapter(DatabaseDialect dialect, Type expected)
        => Assert.IsType(expected, DatabaseAdapterFactory.Create(dialect));

    [Fact]
    public void ProviderErrorsAreSanitized()
    {
        var error = AdapterErrors.Classify(new Npgsql.PostgresException("password=leak", "FATAL", "ERROR", "28P01"));
        Assert.Equal(AdapterErrorKind.Authentication, error.Kind);
        Assert.DoesNotContain("leak", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GaussAdapterDoesNotAssumePostgresStats()
    {
        var adapter = new GaussDbAdapter();
        var capabilities = await adapter.GetCapabilitiesAsync(new ConnectionProfile(Guid.NewGuid(), "g", DatabaseDialect.GaussDb, "localhost", 1, "db"));
        Assert.Contains(capabilities, capability => capability.Name == "gaussdb.statement_history" && capability.Status == CapabilityStatus.Unverified);
    }
}
