using System.Collections.ObjectModel;
using QueryLens.Core;

namespace QueryLens.Desktop.ViewModels;

public sealed class PlanNodeItem
{
    public PlanNodeItem(PlanNode node, int depth = 0)
    {
        Node = node;
        Depth = depth;
        Children = new ObservableCollection<PlanNodeItem>(node.Children.Select(c => new PlanNodeItem(c, depth + 1)));
    }

    public PlanNode Node { get; }
    public int Depth { get; }
    public ObservableCollection<PlanNodeItem> Children { get; }
    public string Label => string.IsNullOrWhiteSpace(Node.Relation) ? Node.NodeType : $"{Node.NodeType} · {Node.Relation}";
    public string Metrics => $"估算行数 {Format(Node.EstimatedRows)} · 实际行数 {Format(Node.ActualRows)} · 成本 {Format(Node.EstimatedCost)}";
    private static string Format(double? value) => value?.ToString("g") ?? "不可用";
}
