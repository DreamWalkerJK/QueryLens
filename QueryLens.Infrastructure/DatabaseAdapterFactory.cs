using QueryLens.Core;

namespace QueryLens.Infrastructure;

public static class DatabaseAdapterFactory
{
    public static IDatabaseAdapter Create(DatabaseDialect dialect, ISecretStore? secretStore = null) => dialect switch
    {
        DatabaseDialect.MySql => new MySqlDatabaseAdapter(secretStore),
        DatabaseDialect.PostgreSql => new PostgreSqlDatabaseAdapter(secretStore),
        DatabaseDialect.SqlServer => new SqlServerDatabaseAdapter(secretStore),
        DatabaseDialect.GaussDb => new GaussDbAdapter(secretStore),
        _ => throw new AdapterException(AdapterErrorKind.Unsupported, "未支持的数据库方言。")
    };
}
