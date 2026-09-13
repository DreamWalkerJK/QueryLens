using Microsoft.Data.SqlClient;
using QueryLens.Core;

namespace QueryLens.Infrastructure;

public sealed class SqlServerDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.SqlServer;
    internal const string QueryStoreSql = """
        SELECT TOP (@limit) CONVERT(nvarchar(max), qt.query_sql_text),
               SUM(rs.count_executions),
               SUM(rs.avg_duration * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0) / 1000.0,
               MIN(rs.min_duration) / 1000.0, MAX(rs.max_duration) / 1000.0,
               MAX(rs.last_execution_time), CONVERT(nvarchar(64), q.query_hash, 1)
        FROM sys.query_store_query_text AS qt
        JOIN sys.query_store_query AS q ON q.query_text_id = qt.query_text_id
        JOIN sys.query_store_plan AS pl ON pl.query_id = q.query_id
        JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = pl.plan_id
        GROUP BY qt.query_sql_text, q.query_hash
        ORDER BY SUM(rs.avg_duration * rs.count_executions) DESC
        """;

    private async Task<SqlConnection> OpenAsync(ConnectionProfile p, CancellationToken token)
    {
        Validate(p, token);
        if (p.Authentication is not (AuthenticationMode.Password or AuthenticationMode.Integrated))
            throw new AdapterException(AdapterErrorKind.Configuration, "SQL Server 适配器支持密码或 Windows 集成认证。");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{p.Host},{p.Port}", InitialCatalog = p.Database, ConnectTimeout = Timeout(p),
            Encrypt = p.UseTls, TrustServerCertificate = false, Pooling = true, MaxPoolSize = 5,
            IntegratedSecurity = p.Authentication == AuthenticationMode.Integrated
        };
        if (!builder.IntegratedSecurity) { builder.UserID = p.UserName; builder.Password = await ResolveSecretAsync(p, token).ConfigureAwait(false); }
        var c = new SqlConnection(builder.ConnectionString);
        try { await c.OpenAsync(token).ConfigureAwait(false); return c; }
        catch { await c.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public override Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128),SERVERPROPERTY('Edition')), compatibility_level FROM sys.databases WHERE name=DB_NAME()");
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); await r.ReadAsync(token).ConfigureAwait(false);
        return new DatabaseInfo($"Microsoft SQL Server ({r.GetString(1)})", r.GetString(0), Convert.ToString(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
    });

    public override Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync<IReadOnlyList<Capability>>(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        var list = new List<Capability>
        {
            await ProbeAsync(c, p, "sqlserver.query_store", "读取 Query Store 累计运行时统计", "VIEW DATABASE STATE；Query Store 由 DBA 启用", "SELECT TOP (0) desired_state_desc, actual_state_desc FROM sys.database_query_store_options", token),
            await ProbeAsync(c, p, "sqlserver.dm_exec_query_stats", "读取缓存 DMV（重启或淘汰后数据会消失）", "VIEW SERVER STATE", "SELECT TOP (0) * FROM sys.dm_exec_query_stats", token),
            new("sqlserver.showplan_xml", CapabilityStatus.Unverified, "导入 Showplan XML；显式采集需隔离连接并由用户触发", "SHOWPLAN 权限（目标数据库）")
        };
        return list;
    });

    public override Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default) => GuardAsync<IReadOnlyList<SlowQuery>>(async () =>
    {
        TakeSample(p, token);
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, QueryStoreSql); Parameter(cmd, "@limit", SafeLimit(limit));
        var result = new List<SlowQuery>(); await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false))
        {
            var sql = Text(r, 0); if (string.IsNullOrWhiteSpace(sql)) continue;
            var observed = r.IsDBNull(5) ? DateTimeOffset.UtcNow : r.GetValue(5) switch { DateTimeOffset dto => dto, DateTime dt => new DateTimeOffset(dt), _ => DateTimeOffset.UtcNow };
            result.Add(Query(p, sql, Int64(r, 1) ?? 0, Number(r, 2), Number(r, 3), Number(r, 4), null, Text(r, 6), observed));
        }
        return result;
    });
}
