using System.Collections.Immutable;
using QueryLens.Core;

namespace QueryLens.Tests;

public sealed class FingerprintingTests
{
    [Theory]
    [InlineData(DatabaseDialect.MySql)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.GaussDb)]
    public void Equivalent_literals_and_comments_share_fingerprint(DatabaseDialect dialect)
    {
        var first = SqlFingerprinter.Fingerprint("SELECT * FROM orders WHERE customer_id = 42 AND status = 'paid' -- one", dialect);
        var second = SqlFingerprinter.Fingerprint("/* another */ select * from orders where customer_id=7 and status='pending'", dialect);

        Assert.Equal(first.NormalizedSql, second.NormalizedSql);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain("paid", first.RedactedSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending", second.RedactedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(dialect, first.Dialect);
    }

    [Fact]
    public void Fingerprint_preserves_quoted_identifiers_and_parameterizes_numbers()
    {
        var result = SqlFingerprinter.Fingerprint("SELECT `Order Id`, \"Status\" FROM t WHERE id = @customerId AND amount > 12.50");

        Assert.Contains("`Order Id`", result.RedactedSql);
        Assert.Contains("\"Status\"", result.RedactedSql);
        Assert.Contains("?", result.NormalizedSql);
        Assert.DoesNotContain("12.50", result.RedactedSql);
        Assert.Single(result.Statements);
    }

    [Fact]
    public void Fingerprint_tracks_multiple_statements()
    {
        var result = SqlFingerprinter.Fingerprint("SELECT 1 AS first_value; SELECT 2 AS second_value; SELECT 3 AS final_value");

        Assert.Equal(3, result.Statements.Length);
        Assert.Contains("first_value", result.Statements[0]);
        Assert.DoesNotContain("first_value", result.Statements[1]);
        Assert.Contains("second_value", result.Statements[1]);
        Assert.DoesNotContain("second_value", result.Statements[2]);
        Assert.Contains("final_value", result.Statements[2]);
    }
}

public sealed class PlanParsingTests
{
    [Fact]
    public void Json_parser_reads_node_metrics_and_children()
    {
        const string json = """
            {
              "Node Type": "Seq Scan",
              "Relation Name": "orders",
              "Plan Rows": 100,
              "Actual Rows": 2500,
              "Total Cost": 42.5,
              "Plans": [{ "Node Type": "Index Scan", "Index Name": "ix_orders_customer", "Plan Rows": 10 }]
            }
            """;

        var plan = PlanParser.ParseJson(json, DatabaseDialect.PostgreSql, "16");

        Assert.Equal("Seq Scan", plan.Root.NodeType);
        Assert.Equal("orders", plan.Root.Relation);
        Assert.Equal(100, plan.Root.EstimatedRows);
        Assert.Equal(2500, plan.Root.ActualRows);
        Assert.Equal(42.5, plan.Root.EstimatedCost);
        var child = Assert.Single(plan.Root.Children);
        Assert.Equal("Index Scan", child.NodeType);
        Assert.Equal("ix_orders_customer", child.Index);
        Assert.True(plan.IsOfflineSample);
    }

    [Fact]
    public void Json_parser_accepts_unknown_nodes_without_fabricating_metrics()
    {
        var plan = PlanParser.ParseJson("{\"type\":\"FutureOperator\",\"mystery\":true}", DatabaseDialect.MySql);

        Assert.Equal("FutureOperator", plan.Root.NodeType);
        Assert.Null(plan.Root.EstimatedRows);
        Assert.Empty(plan.Root.Children);
    }

    [Fact]
    public void Xml_parser_reads_showplan_operator_and_nested_relop()
    {
        const string xml = """
            <ShowPlanXML Version="1.0">
              <RelOp PhysicalOp="Index Scan" EstimateRows="12" EstimatedTotalSubtreeCost="1.5">
                <IndexScan><Object Table="orders" Index="ix_orders_customer" /></IndexScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var plan = PlanParser.ParseXml(xml, DatabaseDialect.SqlServer, "2022");

        var relOp = Assert.Single(PlanTestNodes.Flatten(plan.Root), n => n.NodeType == "Index Scan");
        Assert.Equal("Index Scan", relOp.NodeType);
        Assert.Equal(12, relOp.EstimatedRows);
        Assert.Equal(1.5, relOp.EstimatedCost);
        Assert.Equal("orders", relOp.Relation);
        Assert.Equal("ix_orders_customer", relOp.Index);
    }

    [Fact]
    public void Parsers_reject_malformed_documents()
    {
        Assert.ThrowsAny<Exception>(() => PlanParser.ParseJson("{", DatabaseDialect.MySql));
        Assert.ThrowsAny<Exception>(() => PlanParser.ParseXml("<ShowPlanXML>", DatabaseDialect.SqlServer));
    }
}

public sealed class DiagnosticsTests
{
    [Fact]
    public void Diagnostics_report_skew_large_scan_and_sort_with_evidence()
    {
        var root = new PlanNode("Seq Scan", "orders", EstimatedRows: 20_000, ActualRows: 400_000,
            Children: ImmutableArray.Create(
                new PlanNode("Sort", EstimatedRows: 20_000),
                new PlanNode("Index Scan", "customers", EstimatedRows: 2)));
        var plan = new ExecutionPlan(Guid.NewGuid(), null, DatabaseDialect.PostgreSql, "16", "JSON import",
            DateTimeOffset.UtcNow, "{}", root);

        var findings = DiagnosticEngine.Analyze(plan);

        Assert.Contains(findings, f => f.RuleId == "row-estimate-skew" && f.Evidence.Contains("估算"));
        Assert.Contains(findings, f => f.RuleId == "large-scan" && f.Evidence.Contains("orders"));
        Assert.Contains(findings, f => f.RuleId == "sort-cost");
        Assert.All(findings, f => Assert.InRange(f.Confidence, 0, 1));
        Assert.All(findings, f => Assert.Equal(plan.Id, f.PlanId));
    }
}

public sealed class PlanComparisonTests
{
    [Fact]
    public void Comparison_requires_same_query_semantics()
    {
        var root = new PlanNode("Index Scan");
        var first = new ExecutionPlan(Guid.NewGuid(), null, DatabaseDialect.PostgreSql, "16", "JSON", DateTimeOffset.UtcNow, "{}", root);
        var second = first with { Id = Guid.NewGuid() };
        Assert.Throws<IncompatiblePlanException>(() => PlanComparer.Compare(first, second));
    }
}

public sealed class LocalStoreTests
{
    [Fact]
    public async Task Connections_round_trip_and_survive_reopening_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-{Guid.NewGuid():N}.db");
        try
        {
            ConnectionProfile profile;
            await using (var store = new LocalStore(path))
            {
                await store.InitializeAsync();
                profile = new ConnectionProfile(Guid.NewGuid(), "dev", DatabaseDialect.MySql, "localhost", 3306,
                    "shop", AuthenticationMode.Password, UseTls: true, TimeoutSeconds: 15,
                    UserName: "reader", SecretReference: "credential-manager:querylens/dev");

                await store.SaveConnectionAsync(profile);
                var saved = Assert.Single(await store.GetConnectionsAsync());
                Assert.Equal(profile, saved);
            }
            await using (var reopenedStore = new LocalStore(path))
            {
                await reopenedStore.InitializeAsync();
                Assert.Equal(profile, Assert.Single(await reopenedStore.GetConnectionsAsync()));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        }
    }
}

public sealed class SnapshotDeltaTests
{
    private static SlowQuery Query(string key, long calls, double total, string epoch = "e1") =>
        new(Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), "select 1", "select ?", "select ?", key, DateTimeOffset.UtcNow, calls, TimeSpan.FromMilliseconds(total), null, null, null, key, "db", false, DatabaseDialect.MySql, SnapshotSemantics.Cumulative, CounterEpoch: epoch);

    [Fact]
    public void Verified_complete_snapshots_compute_delta_and_flag_eviction()
    {
        var cid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var first = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "performance_schema", DateTimeOffset.UtcNow, 2, true, "db", "instance-1", "stats-1", true), [Query("a", 10, 100), Query("gone", 4, 20)]);
        var second = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "performance_schema", DateTimeOffset.UtcNow.AddMinutes(1), 2, true, "db", "instance-1", "stats-1", true), [Query("a", 15, 175)]);
        var deltas = SnapshotDelta.Compute(first, second);
        Assert.Equal(5, Assert.Single(deltas, x => x.Key == "native:a").Delta.Calls);
        Assert.True(Assert.Single(deltas, x => x.Key == "native:gone").MissingInCurrent);
    }

    [Fact]
    public void Unverified_epoch_and_window_metrics_are_not_presented_as_deltas()
    {
        var cid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var a = Query("a", 10, 100) with { Semantics = SnapshotSemantics.Window };
        var b = Query("a", 12, 120) with { Semantics = SnapshotSemantics.Window };
        var first = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "source", DateTimeOffset.UtcNow, 10, true), [a]);
        var second = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "source", DateTimeOffset.UtcNow.AddMinutes(1), 10, true), [b]);
        var delta = Assert.Single(SnapshotDelta.Compute(first, second));
        Assert.False(delta.Delta.Comparable);
        Assert.Contains("epoch", delta.Delta.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Counter_reset_or_changed_batch_is_rejected()
    {
        var before = Query("a", 10, 100);
        var after = Query("a", 2, 20);
        var reset = SnapshotDelta.Compute(before, after);
        Assert.False(reset.Comparable);
        Assert.Contains("重置", reset.Reason!);

        var cid = before.ConnectionId!.Value;
        var first = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "source", DateTimeOffset.UtcNow, 10, true, "db", "i", "s", true), [before]);
        var second = new SlowQuerySnapshot(new(Guid.NewGuid(), cid, DatabaseDialect.MySql, "source", DateTimeOffset.UtcNow.AddMinutes(1), 20, true, "db", "i", "s", true), [after]);
        Assert.Contains("批次", Assert.Single(SnapshotDelta.Compute(first, second)).Delta.Reason!);
    }
}
