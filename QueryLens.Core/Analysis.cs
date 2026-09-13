namespace QueryLens.Core;

public sealed record PlanChange(string Kind, string Description, Severity Severity);
public static class PlanComparer
{
    public static IReadOnlyList<PlanChange> Compare(ExecutionPlan before, ExecutionPlan after)
    {
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

public static class SlowQueryAggregator
{
    public static IReadOnlyList<SlowQueryGroup> Group(IEnumerable<SlowQuery> queries) => queries.GroupBy(q => q.Fingerprint).Select(g => new SlowQueryGroup(g.Key, g.Count(), g.Sum(q => q.TotalDuration?.TotalMilliseconds ?? 0), g.Average(q => q.TotalDuration?.TotalMilliseconds ?? 0), g.Min(q => q.MinDuration?.TotalMilliseconds), g.Max(q => q.MaxDuration?.TotalMilliseconds), g.First().Dialect())).ToList();
}
public sealed record SlowQueryGroup(string Fingerprint, int Samples, double TotalMilliseconds, double AverageMilliseconds, double? MinMilliseconds, double? MaxMilliseconds, DatabaseDialect Dialect);
file static class SlowQueryExtensions { public static DatabaseDialect Dialect(this SlowQuery q) => DatabaseDialect.Unknown; }
