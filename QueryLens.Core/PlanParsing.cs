using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QueryLens.Core;

public static class PlanParser
{
    public const int MaximumInputCharacters = 32 * 1024 * 1024;
    public static ExecutionPlan ParseJson(string json, DatabaseDialect dialect, string version = "unknown", Guid? connectionId = null)
    {
        CheckSize(json);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        var root = ParseElement(doc.RootElement);
        return new(Guid.NewGuid(), connectionId, dialect, version, "JSON import", DateTimeOffset.UtcNow, json, root, false, null, true);
    }

    static PlanNode ParseElement(JsonElement e, string? propertyName = null)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            var children = e.EnumerateArray().Select(x => ParseElement(x)).ToImmutableArray();
            if (children.IsEmpty) throw new FormatException("执行计划数组为空。");
            return children.Length == 1 ? children[0] : new("Plan collection", Children: children);
        }
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("执行计划节点必须为 JSON 对象。");
        if (e.TryGetProperty("Plan", out var pgPlan)) return ParseElement(pgPlan);
        if (e.TryGetProperty("query_block", out var mysqlPlan)) return ParseElement(mysqlPlan, "Query block");

        var type = GetString(e, "Node Type", "node_type", "operator", "type");
        var access = GetString(e, "access_type");
        type ??= access is null ? propertyName ?? "Unknown operator" : access == "ALL" ? "Table Scan (ALL)" : $"Index access ({access})";
        var attrs = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array and not JsonValueKind.Null)
                attrs[p.Name] = p.Value.ToString();
        double? cost = GetDouble(e, "Total Cost", "total_cost", "EstimatedCost", "estimated_cost");
        if (e.TryGetProperty("cost_info", out var costInfo))
        {
            cost ??= GetDouble(costInfo, "query_cost", "prefix_cost");
            foreach (var p in costInfo.EnumerateObject()) attrs["cost_info." + p.Name] = p.Value.ToString();
        }
        var childrenBuilder = ImmutableArray.CreateBuilder<PlanNode>();
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name is "cost_info" or "Output" or "Sort Key" or "Group Key" or "possible_keys" or "used_key_parts" or "used_columns" or "ref") continue;
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in p.Value.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object) childrenBuilder.Add(ParseElement(child, p.Name == "nested_loop" ? "Nested loop input" : null));
            }
            else if (p.Value.ValueKind == JsonValueKind.Object && p.Name != "JIT") childrenBuilder.Add(ParseElement(p.Value, p.Name));
        }
        var actualMs = GetDouble(e, "Actual Total Time", "actual_total_time", "actual_duration_ms");
        return new(type, GetString(e, "Relation Name", "relation_name", "table_name", "table", "object"),
            GetString(e, "Index Name", "index_name", "index", "key"), GetString(e, "Join Type", "join_type"),
            GetDouble(e, "Plan Rows", "plan_rows", "EstimatedRows", "estimated_rows", "rows_examined_per_scan"),
            GetDouble(e, "Actual Rows", "actual_rows"), cost, actualMs.HasValue ? TimeSpan.FromMilliseconds(actualMs.Value) : null,
            childrenBuilder.ToImmutable(), attrs.ToImmutable());
    }

    static string? GetString(JsonElement e, params string[] names)
    {
        foreach (var name in names) if (e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }
    static double? GetDouble(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var value) && ((value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) || double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number)) return number;
        return null;
    }

    public static ExecutionPlan ParseXml(string xml, DatabaseDialect dialect, string version = "unknown", Guid? connectionId = null)
    {
        CheckSize(xml);
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumInputCharacters });
        var doc = XDocument.Load(reader, LoadOptions.None);
        var root = doc.Root ?? throw new FormatException("执行计划 XML 缺少根节点。");
        if (!root.DescendantsAndSelf().Any(x => x.Name.LocalName == "RelOp")) throw new FormatException("XML 中未找到 Showplan RelOp 节点。");
        return new(Guid.NewGuid(), connectionId, dialect, version, "Showplan XML import", DateTimeOffset.UtcNow, xml, ParseXmlElement(root), false, null, true);
    }

    static PlanNode ParseXmlElement(XElement e)
    {
        var attrs = e.Attributes().GroupBy(a => a.Name.LocalName).ToImmutableDictionary(g => g.Key, g => g.First().Value);
        var owned = e.Descendants().Where(x => x.Ancestors().FirstOrDefault(a => a.Name.LocalName == "RelOp") == e).ToList();
        var objectElement = owned.FirstOrDefault(x => x.Name.LocalName == "Object");
        var counters = owned.Where(x => x.Name.LocalName == "RunTimeCountersPerThread").ToList();
        var actualRows = counters.Select(x => Number(x, "ActualRows")).Where(x => x.HasValue).ToList();
        var elapsed = counters.Select(x => Number(x, "ActualElapsedms")).Where(x => x.HasValue).ToList();
        var enriched = attrs.ToBuilder();
        foreach (var warning in owned.Where(x => x.Name.LocalName.Contains("Spill", StringComparison.OrdinalIgnoreCase) || x.Name.LocalName is "Warnings" or "MissingIndex" or "ColumnsWithNoStatistics"))
            enriched[warning.Name.LocalName] = warning.ToString(SaveOptions.DisableFormatting);
        var children = e.Elements().Where(x => x.HasElements || x.Name.LocalName == "RelOp").Select(ParseXmlElement).ToImmutableArray();
        return new((string?)e.Attribute("PhysicalOp") ?? e.Name.LocalName,
            (string?)objectElement?.Attribute("Table") ?? (string?)e.Attribute("Table"),
            (string?)objectElement?.Attribute("Index") ?? (string?)e.Attribute("Index"),
            (string?)e.Attribute("LogicalOp"), Number(e, "EstimateRows"), actualRows.Count > 0 ? actualRows.Sum() : Number(e, "ActualRows"),
            Number(e, "EstimatedTotalSubtreeCost"), elapsed.Count > 0 ? TimeSpan.FromMilliseconds(elapsed.Max()!.Value) : null, children, enriched.ToImmutable());
    }
    static double? Number(XElement e, string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    static void CheckSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("执行计划文件为空。");
        if (text.Length > MaximumInputCharacters) throw new FormatException("执行计划超过 32 MiB 限制。");
    }
}
