namespace QueryLens.Core;

public sealed record PlanChange(string Kind, string Description, Severity Severity);
public sealed class IncompatiblePlanException(string message) : ArgumentException(message);
public static class PlanComparer
{
    public static IReadOnlyList<PlanChange> Compare(ExecutionPlan before, ExecutionPlan after)
    {
        if (before.Dialect != after.Dialect) throw new IncompatiblePlanException("只能比较相同数据库方言的执行计划。");
        if (before.ConnectionId.HasValue && after.ConnectionId.HasValue && before.ConnectionId != after.ConnectionId) throw new IncompatiblePlanException("执行计划来自不同连接，来源不兼容。");
        if (string.IsNullOrWhiteSpace(before.QueryFingerprint) || string.IsNullOrWhiteSpace(after.QueryFingerprint)) throw new IncompatiblePlanException("缺少 SQL 指纹，无法证明两份计划属于同一语义查询。");
        if (!string.Equals(before.QueryFingerprint, after.QueryFingerprint, StringComparison.OrdinalIgnoreCase)) throw new IncompatiblePlanException("执行计划的 SQL 指纹不同，拒绝比较。");
        if (!string.IsNullOrWhiteSpace(before.SourceOrigin) && !string.IsNullOrWhiteSpace(after.SourceOrigin) && !string.Equals(before.SourceOrigin, after.SourceOrigin, StringComparison.OrdinalIgnoreCase)) throw new IncompatiblePlanException("执行计划来源不兼容。");
        var changes = new List<PlanChange>(); CompareNode(before.Root, after.Root, changes); return changes;
    }
    static void CompareNode(PlanNode a, PlanNode b, List<PlanChange> c)
    {
        if (!string.Equals(a.NodeType, b.NodeType, StringComparison.OrdinalIgnoreCase)) c.Add(new("operator", $"操作符从 {a.NodeType} 变为 {b.NodeType}", Severity.Medium));
        if (!string.Equals(a.Index, b.Index, StringComparison.OrdinalIgnoreCase)) c.Add(new("index", $"索引从 {a.Index ?? "无"} 变为 {b.Index ?? "无"}", Severity.Medium));
        if (a.EstimatedRows is > 0 && b.EstimatedRows is > 0 && Math.Max(a.EstimatedRows.Value / b.EstimatedRows.Value, b.EstimatedRows.Value / a.EstimatedRows.Value) >= 10) c.Add(new("cardinality", "估算行数发生显著变化", Severity.High));
        var n = Math.Max(a.Children.Length, b.Children.Length); for (var i = 0; i < n; i++) { if (i >= a.Children.Length || i >= b.Children.Length) c.Add(new("join-order", "计划节点数量或连接顺序变化", Severity.High)); else CompareNode(a.Children[i], b.Children[i], c); }
    }
}
public sealed record SlowQueryGroup(string Fingerprint, int Samples, double TotalMilliseconds, double AverageMilliseconds, double? MinMilliseconds, double? MaxMilliseconds, DatabaseDialect Dialect, long Calls = 0, SnapshotSemantics Semantics = SnapshotSemantics.Window);
public static class SlowQueryAggregator
{
    public static IReadOnlyList<SlowQueryGroup> Group(IEnumerable<SlowQuery> queries)
    {
        return queries.GroupBy(q => q.Fingerprint, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var items = g.ToList(); var cumulative = items.Any(q => q.Semantics == SnapshotSemantics.Cumulative);
            var total = cumulative ? items.Where(q => q.TotalDuration.HasValue).Select(q => q.TotalDuration!.Value.TotalMilliseconds).DefaultIfEmpty().Max() : items.Sum(q => q.TotalDuration?.TotalMilliseconds ?? 0);
            var calls = cumulative ? items.Select(q => q.Calls).DefaultIfEmpty().Max() : items.Sum(q => Math.Max(0, q.Calls));
            double? Min() { var x = items.Select(q => q.MinDuration?.TotalMilliseconds).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return x.Count == 0 ? null : x.Min(); }
            double? Max() { var x = items.Select(q => q.MaxDuration?.TotalMilliseconds).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return x.Count == 0 ? null : x.Max(); }
            return new SlowQueryGroup(g.Key, items.Count, total, calls > 0 ? total / calls : 0, Min(), Max(), items.Select(q => q.Dialect).FirstOrDefault(d => d != DatabaseDialect.Unknown), calls, cumulative ? SnapshotSemantics.Cumulative : SnapshotSemantics.Window);
        }).ToList();
    }
}
public sealed record CounterDelta(long Calls, TimeSpan? TotalDuration, bool Comparable, string? Reason = null);
public static class SnapshotDelta
{
    public static CounterDelta Compute(SlowQuery previous, SlowQuery current)
    {
        if (previous.Semantics != SnapshotSemantics.Cumulative || current.Semantics != SnapshotSemantics.Cumulative) return new(current.Calls, current.TotalDuration, true);
        if (previous.Fingerprint != current.Fingerprint || (previous.ConnectionId.HasValue && current.ConnectionId.HasValue && previous.ConnectionId != current.ConnectionId)) return new(0, null, false, "指纹或连接来源不同");
        if (current.Calls < previous.Calls || (current.TotalDuration.HasValue && previous.TotalDuration.HasValue && current.TotalDuration < previous.TotalDuration)) return new(0, null, false, "检测到统计重置、重启或条目淘汰");
        return new(current.Calls - previous.Calls, current.TotalDuration.HasValue && previous.TotalDuration.HasValue ? current.TotalDuration - previous.TotalDuration : null, true);
    }
}
