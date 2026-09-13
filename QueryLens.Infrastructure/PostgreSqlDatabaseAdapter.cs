using Npgsql;
using QueryLens.Core;

namespace QueryLens.Infrastructure;

public class PostgreSqlDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
    protected async Task<NpgsqlConnection> OpenAsync(ConnectionProfile p, CancellationToken token)
    {
        Validate(p, token);
        if (p.Authentication is not (AuthenticationMode.Password or AuthenticationMode.None))
            throw new AdapterException(AdapterErrorKind.Configuration, "此适配器支持 PostgreSQL 密码认证；令牌及集成认证尚未提供。");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = p.Host, Port = p.Port, Database = p.Database, Username = p.UserName,
            Password = await ResolveSecretAsync(p, token).ConfigureAwait(false), Timeout = Timeout(p), CommandTimeout = Timeout(p),
            SslMode = p.UseTls ? SslMode.VerifyFull : SslMode.Disable, IncludeErrorDetail = false,
            Pooling = true, MaxPoolSize = 5, ApplicationName = "QueryLens"
        };
        var c = new NpgsqlConnection(builder.ConnectionString);
        try { await c.OpenAsync(token).ConfigureAwait(false); return c; }
        catch { await c.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public override Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, "SELECT version(), current_setting('server_version')");
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); await r.ReadAsync(token).ConfigureAwait(false);
        return new DatabaseInfo("PostgreSQL", r.GetString(1), r.GetString(0));
    });

    private async Task<(string View, HashSet<string> Columns)?> GetViewAsync(NpgsqlConnection c, ConnectionProfile p, CancellationToken token)
    {
        await using var cmd = Command(c, p, """
            SELECT n.nspname, a.attname
            FROM pg_catalog.pg_extension e
            JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace
            JOIN pg_catalog.pg_class v ON v.relnamespace=n.oid AND v.relname='pg_stat_statements'
            JOIN pg_catalog.pg_attribute a ON a.attrelid=v.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE e.extname='pg_stat_statements'
            """);
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        string? schema = null; var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await r.ReadAsync(token).ConfigureAwait(false)) { schema = r.GetString(0); columns.Add(r.GetString(1)); }
        return schema is null ? null : ($"\"{schema.Replace("\"", "\"\"")}\".\"pg_stat_statements\"", columns);
    }

    public override Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync<IReadOnlyList<Capability>>(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        var view = await GetViewAsync(c, p, token).ConfigureAwait(false);
        var list = new List<Capability>();
        if (view is null) list.Add(new("postgresql.pg_stat_statements", CapabilityStatus.Unavailable, "pg_stat_statements 累计统计", "DBA 配置共享预加载并安装扩展；SELECT 及 pg_read_all_stats 可见其他用户 SQL", "当前数据库未安装扩展；应用不会自动安装。"));
        else
        {
            var time = view.Value.Columns.Contains("total_exec_time") ? "total_exec_time" : "total_time";
            var rows = view.Value.Columns.Contains("rows") ? "rows" : "NULL";
            var queryId = view.Value.Columns.Contains("queryid") ? "queryid" : "NULL";
            list.Add(await ProbeAsync(c, p, "postgresql.pg_stat_statements", "按扩展实际列读取累计统计；隐藏 SQL 不采集", "SELECT on extension view；pg_read_all_stats 可见其他用户 SQL", $"SELECT query,calls,{time},{rows},{queryId} FROM {view.Value.View} LIMIT 0", token));
        }
        list.Add(new("plan-import", CapabilityStatus.Available, "PostgreSQL EXPLAIN (FORMAT JSON) 文件导入", "无需数据库权限"));
        return list;
    });

    public override Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default) => GuardAsync<IReadOnlyList<SlowQuery>>(async () =>
    {
        TakeSample(p, token);
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        var view = await GetViewAsync(c, p, token).ConfigureAwait(false)
            ?? throw new AdapterException(AdapterErrorKind.Unsupported, "当前数据库未安装 pg_stat_statements；请由 DBA 配置或导入快照。");
        var execution = view.Columns.Contains("total_exec_time");
        var total = execution ? "total_exec_time" : "total_time";
        var min = execution ? "min_exec_time" : "min_time";
        var max = execution ? "max_exec_time" : "max_time";
        if (!view.Columns.Contains(min)) min = "NULL";
        if (!view.Columns.Contains(max)) max = "NULL";
        var rows = view.Columns.Contains("rows") ? "rows" : "NULL";
        var queryId = view.Columns.Contains("queryid") ? "queryid::text" : "NULL::text";
        await using var cmd = Command(c, p, $"SELECT query,calls,{total},{min},{max},{rows},{queryId} FROM {view.View} WHERE dbid=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database()) AND query IS NOT NULL AND query <> '<insufficient privilege>' ORDER BY {total} DESC LIMIT @limit");
        Parameter(cmd, "@limit", SafeLimit(limit));
        var result = new List<SlowQuery>(); var observed = DateTimeOffset.UtcNow;
        await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false))
        {
            var sql = Text(r, 0); if (string.IsNullOrWhiteSpace(sql)) continue;
            result.Add(Query(p, sql, Int64(r, 1) ?? 0, Number(r, 2), Number(r, 3), Number(r, 4), Int64(r, 5), Text(r, 6), observed));
        }
        return result;
    });
}
