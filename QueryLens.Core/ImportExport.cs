using System.Text;
using System.Text.Json;

namespace QueryLens.Core;
public enum ImportFormat { MySqlSlowLog, JsonSnapshot }
public sealed record ImportError(int? Line, string Message);
public sealed record ImportResult(IReadOnlyList<SlowQuery> Queries, IReadOnlyList<ImportError> Errors);
public static class SlowQueryImporter
{
    public static async Task<ImportResult> ImportAsync(Stream stream, ImportFormat format, DatabaseDialect dialect, Guid? connectionId = null, string? database = null, bool isOfflineSample = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream); using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, leaveOpen: true); var text = await reader.ReadToEndAsync(ct); ct.ThrowIfCancellationRequested();
        return format switch { ImportFormat.MySqlSlowLog => ParseMySql(text, dialect, connectionId, database, isOfflineSample), ImportFormat.JsonSnapshot => ParseJson(text, dialect, connectionId, database, isOfflineSample), _ => new([], [new(null, "不支持的导入格式")]) };
    }
    static ImportResult ParseMySql(string text, DatabaseDialect dialect, Guid? cid, string? db, bool offline)
    {
        var q = new List<SlowQuery>(); var errors = new List<ImportError>(); var lines = text.Replace("\r", "").Split('\n');
        DateTimeOffset observed = DateTimeOffset.UtcNow; double? total = null, min = null, max = null; long? rows = null; var sql = new StringBuilder(); int start = 0;
        void Flush()
        {
            var raw = sql.ToString().Trim(); if (raw.Length == 0) return; try { var f = SqlFingerprinter.Fingerprint(raw, dialect, db); q.Add(new(Guid.NewGuid(), cid, raw, f.RedactedSql, f.NormalizedSql, f.Fingerprint, observed, 1, total.HasValue ? TimeSpan.FromMilliseconds(total.Value * 1000) : null, min.HasValue ? TimeSpan.FromMilliseconds(min.Value * 1000) : null, max.HasValue ? TimeSpan.FromMilliseconds(max.Value * 1000) : null, rows, null, db, offline, dialect)); } catch (Exception ex) { errors.Add(new(start, ex.Message)); }
            sql.Clear(); total = min = max = null; rows = null;
        }
        for (var i = 0; i < lines.Length; i++) { var line = lines[i]; if (line.StartsWith("# Time:", StringComparison.OrdinalIgnoreCase)) { if (sql.Length > 0) Flush(); start = i + 1; if (DateTimeOffset.TryParse(line[7..].Trim(), out var dt)) observed = dt; continue; } if (line.StartsWith("# Query_time:", StringComparison.OrdinalIgnoreCase)) { var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length > 2 && double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) total = v; continue; } if (line.StartsWith("# Rows_sent:", StringComparison.OrdinalIgnoreCase)) { var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length > 2 && long.TryParse(p[2], out var v)) rows = v; continue; } if (line.StartsWith("#", StringComparison.Ordinal)) continue; if (line.Trim().Length > 0) { if (sql.Length > 0) sql.AppendLine(); sql.Append(line); } }
        Flush(); if (q.Count == 0 && errors.Count == 0) errors.Add(new(null, "慢查询日志中没有可识别的 SQL。")); return new(q, errors);
    }
    static ImportResult ParseJson(string text, DatabaseDialect dialect, Guid? cid, string? db, bool offline)
    {
        var queries = new List<SlowQuery>(); var errors = new List<ImportError>(); try { using var doc = JsonDocument.Parse(text); var root = doc.RootElement; var arr = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : root.TryGetProperty("queries", out var x) && x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().ToArray() : new[] { root }; int n = 0; foreach (var item in arr) { try { var raw = Str(item, "rawSql", "raw_sql", "sql", "query") ?? throw new FormatException("缺少 SQL 字段"); var f = SqlFingerprinter.Fingerprint(raw, dialect, db); var calls = Num(item, "calls", "count") ?? 1; var total = Num(item, "totalMs", "total_ms", "totalDurationMs"); var observed = DateTimeOffset.TryParse(Str(item, "observedAt", "observed_at"), out var dt) ? dt : DateTimeOffset.UtcNow; var sem = string.Equals(Str(item, "semantics", "snapshotSemantics"), "cumulative", StringComparison.OrdinalIgnoreCase) ? SnapshotSemantics.Cumulative : SnapshotSemantics.Window; queries.Add(new(Guid.NewGuid(), cid, raw, f.RedactedSql, f.NormalizedSql, f.Fingerprint, observed, (long)Math.Max(0, calls), total.HasValue ? TimeSpan.FromMilliseconds(total.Value) : null, null, null, (long?)Num(item, "rows", "rowCount"), Str(item, "nativeQueryId", "native_id"), Str(item, "database") ?? db, offline, dialect, sem)); } catch (Exception ex) { errors.Add(new(n, ex.Message)); } n++; } } catch (Exception ex) { errors.Add(new(null, $"JSON 解析失败: {ex.Message}")); } return new(queries, errors);
    }
    static string? Str(JsonElement e, params string[] names) { foreach (var n in names) if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString(); return null; }
    static double? Num(JsonElement e, params string[] names) { foreach (var n in names) if (e.TryGetProperty(n, out var v) && v.TryGetDouble(out var x) && double.IsFinite(x)) return x; return null; }
}
public static class ReportExporter
{
    public static async Task ExportJsonAsync(Stream stream, IEnumerable<SlowQuery> queries, IEnumerable<ExecutionPlan> plans, bool includeRawSql = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = new { exportedAt = DateTimeOffset.UtcNow,
            queries = queries.Select(q => new { q.Id, q.ConnectionId, sql = includeRawSql ? SensitiveData.Redact(q.RawSql) : q.RedactedSql, redactedSql = q.RedactedSql, q.NormalizedSql, q.Fingerprint, q.ObservedAt, q.Calls, totalMs = q.TotalDuration?.TotalMilliseconds, minMs = q.MinDuration?.TotalMilliseconds, maxMs = q.MaxDuration?.TotalMilliseconds, q.Rows, q.NativeQueryId, q.Database, q.IsOfflineSample, q.Dialect, q.Semantics }),
            plans = plans.Select(p => new { p.Id, p.ConnectionId, p.Dialect, p.SourceVersion, p.CollectionMethod, p.CollectedAt, p.IsBaseline, p.QueryFingerprint, p.IsOfflineSample, p.SourceOrigin, rawText = includeRawSql ? SensitiveData.Redact(p.RawText) : null }) };
        await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct); await stream.FlushAsync(ct);
    }
}
