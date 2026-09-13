namespace QueryLens.Core;

public interface IDatabaseAdapter
{
    DatabaseDialect Dialect { get; }
    Task<DatabaseInfo> DetectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile profile, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);
}
public sealed class OfflineSampleAdapter : IDatabaseAdapter
{
    public DatabaseDialect Dialect { get; }
    public OfflineSampleAdapter(DatabaseDialect dialect = DatabaseDialect.PostgreSql) => Dialect = dialect;
    public Task<DatabaseInfo> DetectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(new DatabaseInfo($"{Dialect} (离线样例)", "sample", "offline"));
    public Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Capability>>(new[] { new Capability("plan-import", CapabilityStatus.Available, "支持导入执行计划", "无需数据库权限"), new Capability("live-collection", CapabilityStatus.Unverified, "离线模式不可连接实例", "只读账号") });
    public Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile profile, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default)
    { var sql = "SELECT * FROM orders WHERE customer_id = 42 AND created_at > '2024-01-01'"; var f = SqlFingerprinter.Fingerprint(sql, Dialect); IReadOnlyList<SlowQuery> x = new[] { new SlowQuery(Guid.NewGuid(), profile.Id, sql, f.RedactedSql, f.NormalizedSql, f.Fingerprint, DateTimeOffset.UtcNow, 12, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(90), TimeSpan.FromSeconds(1), 1200, "offline-1", profile.Database, true, Dialect) }; return Task.FromResult(x); }
}
