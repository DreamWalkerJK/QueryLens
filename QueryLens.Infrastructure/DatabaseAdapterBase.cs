using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using QueryLens.Core;

namespace QueryLens.Infrastructure;

public abstract class DatabaseAdapterBase(ISecretStore? secretStore = null) : IDatabaseAdapter
{
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastSamples = new();
    protected readonly ISecretStore? SecretStore = secretStore;
    public abstract DatabaseDialect Dialect { get; }
    public abstract Task<DatabaseInfo> DetectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<Capability>> GetCapabilitiesAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<SlowQuery>> ReadSlowQueriesAsync(ConnectionProfile profile, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);

    public static readonly TimeSpan MinimumSamplingInterval = TimeSpan.FromSeconds(5);
    protected static int SafeLimit(int limit) => Math.Clamp(limit, 1, 1000);
    protected static int Timeout(ConnectionProfile p) => Math.Clamp(p.TimeoutSeconds, 1, 300);

    protected static void Validate(ConnectionProfile p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(p.Host) || p.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(p.Database)
            || p.TimeoutSeconds is < 1 or > 300)
            throw new AdapterException(AdapterErrorKind.Configuration, "请填写主机、1–65535 端口、数据库及 1–300 秒超时。");
    }

    protected static void TakeSample(ConnectionProfile p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (LastSamples.TryGetValue(p.Id, out var previous))
            {
                if (now - previous < MinimumSamplingInterval)
                    throw new AdapterException(AdapterErrorKind.SamplingInterval, "采集间隔至少 5 秒。请稍后重试；过密采集会增加实例负担。");
                if (LastSamples.TryUpdate(p.Id, now, previous)) return;
            }
            else if (LastSamples.TryAdd(p.Id, now)) return;
        }
    }

    protected async Task<string?> ResolveSecretAsync(ConnectionProfile p, CancellationToken token)
    {
        if (p.SecretReference is null) return null;
        if (SecretStore is null) throw new AdapterException(AdapterErrorKind.SecretUnavailable, "此连接需要本机凭据存储；请重新保存密码。");
        return await SecretStore.ResolveAsync(p.SecretReference, token).ConfigureAwait(false);
    }

    protected static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw AdapterErrors.Classify(ex); }
    }

    protected static DbCommand Command(DbConnection connection, ConnectionProfile profile, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Timeout(profile);
        return command;
    }

    protected static void Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    protected static async Task<Capability> ProbeAsync(DbConnection c, ConnectionProfile p, string name, string description, string privilege, string sql, CancellationToken token)
    {
        await using var command = Command(c, p, sql);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return new(name, CapabilityStatus.Available, description, privilege);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var error = AdapterErrors.Classify(ex);
            if (error.Kind is AdapterErrorKind.Connection or AdapterErrorKind.Authentication or AdapterErrorKind.Timeout or AdapterErrorKind.Tls) throw error;
            return new(name, error.Kind == AdapterErrorKind.PermissionDenied ? CapabilityStatus.PermissionDenied : CapabilityStatus.Unavailable, description, privilege, error.Message);
        }
    }

    protected static SlowQuery Query(ConnectionProfile p, string sql, long calls, double? totalMs, double? minMs, double? maxMs, long? rows, string? nativeId, DateTimeOffset observed, string? database = null)
    {
        if (sql.Length > 1_000_000) throw new AdapterException(AdapterErrorKind.DataQuality, "单条诊断 SQL 超过 1 MB 上限。");
        var fingerprint = SqlFingerprinter.Fingerprint(sql, p.Dialect);
        return new(Guid.NewGuid(), p.Id, sql, fingerprint.RedactedSql, fingerprint.NormalizedSql, fingerprint.Fingerprint,
            observed, calls, Duration(totalMs), Duration(minMs), Duration(maxMs), rows, nativeId, database ?? p.Database, false);
    }

    protected static TimeSpan? Duration(double? ms)
        => ms is { } value && double.IsFinite(value) && value >= 0 && value <= TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(value) : null;
    protected static double? Number(DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Convert.ToDouble(r.GetValue(ordinal), CultureInfo.InvariantCulture);
    protected static long? Int64(DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Convert.ToInt64(r.GetValue(ordinal), CultureInfo.InvariantCulture);
    protected static string? Text(DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Convert.ToString(r.GetValue(ordinal), CultureInfo.InvariantCulture);
}
