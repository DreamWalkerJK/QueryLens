namespace QueryLens.Core;

public static class DiagnosticEngine
{
    public static IReadOnlyList<DiagnosticFinding> Analyze(ExecutionPlan plan)
    {
        var result = new List<DiagnosticFinding>(); foreach (var n in Flatten(plan.Root))
        {
            if (n.EstimatedRows is > 0 && n.ActualRows is > 0 && Math.Max(n.EstimatedRows.Value / n.ActualRows.Value, n.ActualRows.Value / n.EstimatedRows.Value) >= 10)
                result.Add(new("row-estimate-skew", "估算行数与实际行数偏差", $"估算 {n.EstimatedRows:g}，实际 {n.ActualRows:g}", plan.Dialect, plan.SourceVersion, Severity.High, .9, "更新统计信息后重新获取计划并检查过滤条件", "统计更新或计划重编译可能增加开销", plan.Id));
            if (n.NodeType.Contains("scan", StringComparison.OrdinalIgnoreCase) && n.Relation is not null && n.EstimatedRows is > 10000)
                result.Add(new("large-scan", "大范围扫描", $"节点 {n.NodeType} 扫描 {n.Relation}，估算行数 {n.EstimatedRows:g}", plan.Dialect, plan.SourceVersion, Severity.Medium, .8, "检查过滤列索引和谓词选择性", "新增索引会增加写入和存储成本", plan.Id));
            if (n.NodeType.Contains("sort", StringComparison.OrdinalIgnoreCase)) result.Add(new("sort-cost", "排序开销", "计划包含排序节点；未提供溢出指标", plan.Dialect, plan.SourceVersion, Severity.Low, .7, "查看内存授予及临时文件/磁盘溢出", "增大内存可能影响并发", plan.Id));
        } return result;
    }
    static IEnumerable<PlanNode> Flatten(PlanNode n) { yield return n; foreach (var c in n.Children.SelectMany(Flatten)) yield return c; }
}
