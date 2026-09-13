using MySqlConnector;
using QueryLens.Core;

namespace QueryLens.Infrastructure;

public sealed class MySqlDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.MySql;
    internal const string StatisticsSql = """
        SELECT DIGEST_TEXT, COUNT_STAR, SUM_TIMER_WAIT, MIN_TIMER_WAIT, MAX_TIMER_WAIT,
               SUM_ROWS_SENT, DIGEST, SCHEMA_NAME
        FROM performance_schema.events_statements_summary_by_digest
        WHERE DIGEST_TEXT IS NOT NULL AND SCHEMA_NAME = @database
        ORDER BY SUM_TIMER_WAIT DESC LIMIT @limit
        """;

    private async Task<MySqlConnection> OpenAsync(ConnectionProfile p, CancellationToken token)
    {
        Validate(p, token);
        if (p.Authentication is not (AuthenticationMode.Password or AuthenticationMode.None))
            throw new AdapterException(AdapterErrorKind.Configuration, "MySQL 适配器支持密码认证；请按实际账号选择认证方式。");
        var builder = new MySqlConnectionStringBuilder
        {
            Server = p.Host, Port = (uint)p.Port, Database = p.Database, UserID = p.UserName,
            Password = await ResolveSecretAsync(p, token).ConfigureAwait(false), ConnectionTimeout = (uint)Timeout(p),
            DefaultCommandTimeout = (uint)Timeout(p), SslMode = p.UseTls ? MySqlSslMode.VerifyFull : MySqlSslMode.None,
            Pooling = true, MaximumPoolSize = 5, ConnectionReset = true, AllowUserVariables = false
        };
        var connection = new MySqlConnection(builder.ConnectionString);
        try { await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public override Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, "SELECT VERSION(), @@version_comment, @@sql_mode");
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        await r.ReadAsync(token).ConfigureAwait(false);
        var version = r.GetString(0);
        var comment = Text(r, 1) ?? "";
        var product = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "MariaDB (MySQL protocol)" : "MySQL";
        return new DatabaseInfo(product, version, $"{comment}; sql_mode={Text(r, 2)}");
    });

    public override Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync<IReadOnlyList<Capability>>(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        var list = new List<Capability>();
        await using (var cmd = Command(c, p, "SELECT @@performance_schema"))
        {
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false)) == 0)
                list.Add(new("mysql.performance_schema", CapabilityStatus.Unavailable, "语句摘要累计统计", "SELECT on performance_schema.events_statements_summary_by_digest", "performance_schema 已关闭；应用不会自动开启。"));
            else
                list.Add(await ProbeAsync(c, p, "mysql.performance_schema", "当前数据库的语句摘要累计统计（并非窗口统计）", "SELECT on performance_schema.events_statements_summary_by_digest",
                    "SELECT DIGEST_TEXT, COUNT_STAR, SUM_TIMER_WAIT, MIN_TIMER_WAIT, MAX_TIMER_WAIT, SUM_ROWS_SENT FROM performance_schema.events_statements_summary_by_digest LIMIT 0", token));
        }
        list.Add(new("plan-import", CapabilityStatus.Available, "MySQL EXPLAIN FORMAT=JSON 文件导入", "无需数据库权限"));
        return list;
    });

    public override Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default) => GuardAsync<IReadOnlyList<SlowQuery>>(async () =>
    {
        TakeSample(p, token);
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, StatisticsSql);
        Parameter(cmd, "@limit", SafeLimit(limit)); Parameter(cmd, "@database", p.Database);
        var result = new List<SlowQuery>(); var observed = DateTimeOffset.UtcNow;
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false))
        {
            var sql = Text(r, 0); if (string.IsNullOrWhiteSpace(sql)) continue;
            result.Add(Query(p, sql, Int64(r, 1) ?? 0, Number(r, 2) / 1e9, Number(r, 3) / 1e9, Number(r, 4) / 1e9, Int64(r, 5), Text(r, 6), observed, Text(r, 7)));
        }
        return result;
    });
}
