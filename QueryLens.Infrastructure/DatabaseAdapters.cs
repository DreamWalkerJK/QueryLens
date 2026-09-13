using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using QueryLens.Core;

namespace QueryLens.Infrastructure;

public abstract class DatabaseAdapterBase(ISecretStore? secretStore = null) : IDatabaseAdapter
{
    protected readonly ISecretStore? SecretStore = secretStore;
    public abstract DatabaseDialect Dialect { get; }
    public abstract Task<DatabaseInfo> DetectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile profile, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);

    protected static int SafeLimit(int limit) => Math.Clamp(limit, 1, 1000);
    protected static void ValidateSamplingInterval(DateTimeOffset? since) { if (since is { } value && value > DateTimeOffset.UtcNow.AddMinutes(1)) throw new AdapterException(AdapterErrorKind.SamplingInterval, "采样起始时间不能晚于当前时间。"); }
    protected async Task<string?> ResolveSecretAsync(ConnectionProfile profile, CancellationToken token)
        => SecretStore is null ? null : await SecretStore.ResolveAsync(profile.SecretReference, token).ConfigureAwait(false);

    protected static SlowQuery ToSlowQuery(ConnectionProfile profile, string sql, long calls, double? totalMs, double? minMs, double? maxMs, long? rows, string? nativeId, DateTimeOffset observed)
    {
        var fingerprint = SqlFingerprinter.Fingerprint(sql, profile.Dialect);
        return new(Guid.NewGuid(), profile.Id, sql, fingerprint.RedactedSql, fingerprint.NormalizedSql, fingerprint.Fingerprint, observed, calls,
            totalMs.HasValue ? TimeSpan.FromMilliseconds(Math.Max(0, totalMs.Value)) : null,
            minMs.HasValue ? TimeSpan.FromMilliseconds(Math.Max(0, minMs.Value)) : null,
            maxMs.HasValue ? TimeSpan.FromMilliseconds(Math.Max(0, maxMs.Value)) : null, rows, nativeId, profile.Database, false);
    }

    protected static void AddTimeout(DbConnection connection, ConnectionProfile profile)
    {
        connection.ConnectionString = connection.ConnectionString; // ensure provider object initialized
    }

    protected static async Task<IReadOnlyList<SlowQuery>> ReadRowsAsync(DbCommand command, ConnectionProfile profile, Func<DbDataReader, SlowQuery?> map, CancellationToken token)
    {
        var result = new List<SlowQuery>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) { var row = map(reader); if (row is not null) result.Add(row); }
        return result;
    }

    protected static double? Number(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return null;
        try { return Convert.ToDouble(r.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
    }
    protected static string? Text(DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Convert.ToString(r.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    protected static long? Int64(DbDataReader r, int ordinal) => Number(r, ordinal) is { } n ? (long)n : null;
    protected static AdapterException Sanitize(Exception ex) => AdapterErrors.Classify(ex);
}

public sealed class MySqlDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.MySql;
    private async Task<MySqlConnection> OpenAsync(ConnectionProfile p, CancellationToken token)
    {
        var cs = new MySqlConnectionStringBuilder { Server = p.Host, Port = (uint)p.Port, Database = p.Database, UserID = p.UserName, ConnectionTimeout = (uint)Math.Clamp(p.TimeoutSeconds, 1, 300), SslMode = p.UseTls ? MySqlSslMode.VerifyFull : MySqlSslMode.None };
        cs.Password = await ResolveSecretAsync(p, token).ConfigureAwait(false);
        var c = new MySqlConnection(cs.ConnectionString);
        try { await c.OpenAsync(token).ConfigureAwait(false); return c; } catch (Exception ex) { await c.DisposeAsync(); throw Sanitize(ex); }
    }
    public override async Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default)
    {
        await using var c = await OpenAsync(p, token); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT VERSION(), @@version_comment, @@sql_mode"; cmd.CommandTimeout = Math.Clamp(p.TimeoutSeconds,1,300);
        try { await using var r = await cmd.ExecuteReaderAsync(token); await r.ReadAsync(token); return new("MySQL", r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)); } catch (OperationCanceledException) { throw; } catch (Exception ex) { throw Sanitize(ex); }
    }
    public override async Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default)
    {
        await using var c = await OpenAsync(p, token); var list = new List<Capability>();
        await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='performance_schema' AND table_name='events_statements_summary_by_digest'";
        try { var present = Convert.ToInt32(await cmd.ExecuteScalarAsync(token)) > 0; list.Add(new("mysql.performance_schema", present ? CapabilityStatus.Available : CapabilityStatus.Unavailable, "读取语句摘要统计", "SELECT on performance_schema.events_statements_summary_by_digest")); }
        catch (Exception ex) { var e = Sanitize(ex); list.Add(new("mysql.performance_schema", e.Kind == AdapterErrorKind.PermissionDenied ? CapabilityStatus.PermissionDenied : CapabilityStatus.Unavailable, "读取语句摘要统计", "SELECT on performance_schema.events_statements_summary_by_digest", e.Message)); }
        list.Add(new("mysql.explain_json", CapabilityStatus.Available, "可导入或由用户触发 EXPLAIN FORMAT=JSON", "无（执行计划动作需用户明确触发）")); return list;
    }
    public override async Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default)
    {
        await using var c = await OpenAsync(p, token); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT DIGEST_TEXT, COUNT_STAR, SUM_TIMER_WAIT, MIN_TIMER_WAIT, MAX_TIMER_WAIT, SUM_ROWS_EXAMINED, DIGEST FROM performance_schema.events_statements_summary_by_digest WHERE DIGEST_TEXT IS NOT NULL ORDER BY SUM_TIMER_WAIT DESC LIMIT @limit"; cmd.Parameters.Add(new MySqlParameter("@limit", SafeLimit(limit))); cmd.CommandTimeout = Math.Clamp(p.TimeoutSeconds,1,300);
        try { return await ReadRowsAsync(cmd, p, r => { var sql = Text(r,0); if (string.IsNullOrWhiteSpace(sql)) return null; return ToSlowQuery(p, sql!, Int64(r,1) ?? 0, Number(r,2) / 1_000_000_000.0, Number(r,3) / 1_000_000_000.0, Number(r,4) / 1_000_000_000.0, Int64(r,5), Text(r,6), DateTimeOffset.UtcNow); }, token); } catch (OperationCanceledException) { throw; } catch (Exception ex) { throw Sanitize(ex); }
    }
}

public class PostgreSqlDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
    protected virtual NpgsqlConnectionStringBuilder Build(ConnectionProfile p, string? password) => new() { Host = p.Host, Port = p.Port, Database = p.Database, Username = p.UserName, Password = password, Timeout = Math.Clamp(p.TimeoutSeconds,1,300), SslMode = p.UseTls ? SslMode.VerifyFull : SslMode.Disable, IncludeErrorDetail = false };
    private async Task<NpgsqlConnection> OpenAsync(ConnectionProfile p, CancellationToken token)
    { var c = new NpgsqlConnection(Build(p, await ResolveSecretAsync(p, token)).ConnectionString); try { await c.OpenAsync(token); return c; } catch (Exception ex) { await c.DisposeAsync(); throw Sanitize(ex); } }
    public override async Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default)
    { await using var c = await OpenAsync(p, token); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT version(), current_setting('server_version')"; try { await using var r=await cmd.ExecuteReaderAsync(token); await r.ReadAsync(token); return new("PostgreSQL", r.GetString(1), r.GetString(0)); } catch(OperationCanceledException){throw;}catch(Exception ex){throw Sanitize(ex);} }
    public override async Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default)
    { await using var c=await OpenAsync(p,token); var list=new List<Capability>(); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname='pg_stat_statements'), to_regclass('pg_stat_statements') IS NOT NULL"; try { await using var r=await cmd.ExecuteReaderAsync(token); await r.ReadAsync(token); var ok=r.GetBoolean(0)&&r.GetBoolean(1); list.Add(new("postgresql.pg_stat_statements",ok?CapabilityStatus.Available:CapabilityStatus.Unavailable,"读取 pg_stat_statements 累计统计","扩展 pg_stat_statements，及对视图的 SELECT 权限")); } catch(Exception ex){var e=Sanitize(ex); list.Add(new("postgresql.pg_stat_statements",e.Kind==AdapterErrorKind.PermissionDenied?CapabilityStatus.PermissionDenied:CapabilityStatus.Unavailable,"读取 pg_stat_statements 累计统计","扩展 pg_stat_statements，及对视图的 SELECT 权限",e.Message));} list.Add(new("postgresql.explain_json",CapabilityStatus.Available,"可导入 EXPLAIN (FORMAT JSON) 文件","无（执行计划动作需用户明确触发）")); return list; }
    public override async Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default)
    { await using var c=await OpenAsync(p,token); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT query, calls, total_exec_time, min_exec_time, max_exec_time, rows, queryid::text FROM pg_stat_statements WHERE query IS NOT NULL ORDER BY total_exec_time DESC LIMIT @limit"; cmd.Parameters.AddWithValue("limit",SafeLimit(limit)); try{return await ReadRowsAsync(cmd,p,r=>{var sql=Text(r,0); return string.IsNullOrWhiteSpace(sql)?null:ToSlowQuery(p,sql!,Int64(r,1)??0,Number(r,2),Number(r,3),Number(r,4),Int64(r,5),Text(r,6),DateTimeOffset.UtcNow);},token);}catch(OperationCanceledException){throw;}catch(Exception ex){throw Sanitize(ex);} }
}

public sealed class GaussDbAdapter(ISecretStore? secretStore = null) : PostgreSqlDatabaseAdapter(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.GaussDb;
    public override async Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default)
    { await using var c=await OpenAsyncForGauss(p,token); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT version(), current_setting('server_version')"; try {await using var r=await cmd.ExecuteReaderAsync(token); await r.ReadAsync(token); var version=r.GetString(0); return new(version.Contains("openGauss",StringComparison.OrdinalIgnoreCase)?"openGauss":"GaussDB",r.GetString(1),version);}catch(OperationCanceledException){throw;}catch(Exception ex){throw Sanitize(ex);} }
    private async Task<NpgsqlConnection> OpenAsyncForGauss(ConnectionProfile p,CancellationToken t){var c=new NpgsqlConnection(Build(p,await ResolveSecretAsync(p,t)).ConnectionString);try{await c.OpenAsync(t);return c;}catch(Exception ex){await c.DisposeAsync();throw Sanitize(ex);}}
    public override Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default) => Task.FromResult<IReadOnlyList<Capability>>(new[]{new Capability("gaussdb.statement_history",CapabilityStatus.Unverified,"产品形态和版本相关的历史 SQL 视图；默认不假设 PostgreSQL 系统视图兼容","请按目标 GaussDB 产品文档授予只读诊断权限")});
    public override Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default) => throw new AdapterException(AdapterErrorKind.Unsupported,"GaussDB 统计视图因产品形态差异未自动读取；请导入官方导出的统计快照。 ");
}

public sealed class SqlServerDatabaseAdapter(ISecretStore? secretStore = null) : DatabaseAdapterBase(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.SqlServer;
    private async Task<SqlConnection> OpenAsync(ConnectionProfile p, CancellationToken t){var b=new SqlConnectionStringBuilder{DataSource=$"{p.Host},{p.Port}",InitialCatalog=p.Database,ConnectTimeout=Math.Clamp(p.TimeoutSeconds,1,300),Encrypt=p.UseTls,TrustServerCertificate=false};if(p.Authentication==AuthenticationMode.Integrated)b.IntegratedSecurity=true;else{b.UserID=p.UserName;b.Password=await ResolveSecretAsync(p,t);}var c=new SqlConnection(b.ConnectionString);try{await c.OpenAsync(t);return c;}catch(Exception ex){await c.DisposeAsync();throw Sanitize(ex);}}
    public override async Task<DatabaseInfo> DetectAsync(ConnectionProfile p,CancellationToken t=default){await using var c=await OpenAsync(p,t);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128),SERVERPROPERTY('ProductName')), CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'CompatibilityLevel'))";try{await using var r=await cmd.ExecuteReaderAsync(t);await r.ReadAsync(t);return new(r.GetString(1),r.GetString(0),r.GetString(2));}catch(OperationCanceledException){throw;}catch(Exception ex){throw Sanitize(ex);}}
    public override async Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p,CancellationToken t=default){await using var c=await OpenAsync(p,t);var list=new List<Capability>();await using var cmd=c.CreateCommand();cmd.CommandText="SELECT desired_state_desc, actual_state_desc FROM sys.database_query_store_options";try{await using var r=await cmd.ExecuteReaderAsync(t);if(await r.ReadAsync(t))list.Add(new("sqlserver.query_store",CapabilityStatus.Available,"读取 Query Store 运行时统计","VIEW DATABASE STATE；Query Store 需由 DBA 启用",$"状态 {r.GetString(0)}/{r.GetString(1)}"));}catch(Exception ex){var e=Sanitize(ex);list.Add(new("sqlserver.query_store",e.Kind==AdapterErrorKind.PermissionDenied?CapabilityStatus.PermissionDenied:CapabilityStatus.Unavailable,"读取 Query Store 运行时统计","VIEW DATABASE STATE；Query Store 需由 DBA 启用",e.Message));}list.Add(new("sqlserver.dm_exec_query_stats",CapabilityStatus.Available,"读取缓存 DMV 摘要（实例重启或淘汰会丢失）","VIEW SERVER STATE"));list.Add(new("sqlserver.showplan_xml",CapabilityStatus.Available,"导入 Showplan XML；实际采集需用户明确动作","SHOWPLAN 权限（隔离连接）"));return list;}
    public override async Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p,DateTimeOffset? since=null,int limit=100,CancellationToken t=default){ ValidateSamplingInterval(since);await using var c=await OpenAsync(p,t);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT TOP (@limit) CONVERT(nvarchar(max),qt.query_sql_text), rs.count_executions, (rs.avg_duration/1000.0), (rs.min_duration/1000.0), (rs.max_duration/1000.0), rs.last_execution_time, CONVERT(nvarchar(64),q.query_hash,1) FROM sys.query_store_query_text qt JOIN sys.query_store_query q ON q.query_text_id=qt.query_text_id JOIN sys.query_store_plan p ON p.query_id=q.query_id JOIN sys.query_store_runtime_stats rs ON rs.plan_id=p.plan_id ORDER BY rs.total_duration DESC";cmd.Parameters.AddWithValue("limit",SafeLimit(limit));try{return await ReadRowsAsync(cmd,p,r=>{var sql=Text(r,0);DateTimeOffset observed=DateTimeOffset.UtcNow;if(!r.IsDBNull(5)){var value=r.GetValue(5);observed=value is DateTimeOffset dto?dto:new DateTimeOffset(Convert.ToDateTime(value),TimeSpan.Zero);}return string.IsNullOrWhiteSpace(sql)?null:ToSlowQuery(p,sql!,Int64(r,1)??0,Number(r,2),Number(r,3),Number(r,4),null,Text(r,6),observed);},t);}catch(OperationCanceledException){throw;}catch(Exception ex){throw Sanitize(ex);}}
}
