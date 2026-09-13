using QueryLens.Core;

namespace QueryLens.Infrastructure;

/// <summary>Explicit PostgreSQL-protocol GaussDB/openGauss product layer. No PostgreSQL statistics assumptions.</summary>
public sealed class GaussDbAdapter(ISecretStore? secretStore = null) : PostgreSqlDatabaseAdapter(secretStore)
{
    public override DatabaseDialect Dialect => DatabaseDialect.GaussDb;

    public override Task<DatabaseInfo> DetectAsync(ConnectionProfile p, CancellationToken token = default) => GuardAsync(async () =>
    {
        await using var c = await OpenAsync(p, token).ConfigureAwait(false);
        await using var cmd = Command(c, p, "SELECT version()");
        var version = Convert.ToString(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false)) ?? "unknown";
        var product = version.Contains("openGauss", StringComparison.OrdinalIgnoreCase) ? "openGauss"
            : version.Contains("GaussDB", StringComparison.OrdinalIgnoreCase) ? "GaussDB (PostgreSQL protocol)"
            : "Unrecognized PostgreSQL-protocol product";
        string? mode = null;
        try
        {
            await using var compatibility = Command(c, p, "SHOW sql_compatibility");
            mode = Convert.ToString(await compatibility.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (AdapterErrors.Classify(ex).Kind is AdapterErrorKind.Unsupported or AdapterErrorKind.PermissionDenied) { mode = "无法读取；待验证"; }
        return new DatabaseInfo(product, version, mode);
    });

    public override Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile p, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Capability>>(new[]
        {
            new Capability("gaussdb.statement_history", CapabilityStatus.Unverified, "PostgreSQL 协议 GaussDB/openGauss：历史视图、字段及权限需按产品版本验证", "依目标产品官方文档及管理员授予的只读权限", "协议兼容不代表系统视图兼容。GaussDB(for MySQL) 应选择 MySQL 协议连接并单独核实产品。"),
            new Capability("plan-import", CapabilityStatus.Available, "可导入 JSON/XML 计划；字段不匹配时报告解析错误", "无需数据库权限")
        });
    }

    public override Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile p, DateTimeOffset? since = null, int limit = 100, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        throw new AdapterException(AdapterErrorKind.Unsupported, "尚未验证该 GaussDB 产品形态的统计视图。请使用官方导出的统计快照；不会尝试 PostgreSQL 视图。");
    }
}
