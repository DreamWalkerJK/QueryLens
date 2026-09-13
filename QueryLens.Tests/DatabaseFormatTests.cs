using System.Globalization;
using QueryLens.Core;

namespace QueryLens.Tests;

public sealed class DialectLexerTests
{
    [Theory]
    [InlineData(DatabaseDialect.MySql)]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.GaussDb)]
    public void String_literal_does_not_swallow_following_predicates(DatabaseDialect dialect)
    {
        var first = SqlFingerprinter.Fingerprint("SELECT customer_id FROM orders WHERE status = 'paid' AND region = 'north' AND customer_id = 42", dialect);
        var second = SqlFingerprinter.Fingerprint("select customer_id from orders where status='pending' and region='south' and customer_id=7", dialect);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Contains("region", first.NormalizedSql);
        Assert.Contains("and customer_id", first.NormalizedSql);
        Assert.DoesNotContain("north", first.RedactedSql);
        Assert.DoesNotContain("paid", first.RedactedSql);
        Assert.Equal(3, first.NormalizedSql.Count(c => c == '?'));
    }

    [Fact]
    public void Postgres_dollar_quoted_literals_are_redacted_and_casts_preserved()
    {
        var first = SqlFingerprinter.Fingerprint("SELECT $tag$private;value$tag$::text, $$second secret$$, $1::int FROM t WHERE id = 12", DatabaseDialect.PostgreSql);
        var second = SqlFingerprinter.Fingerprint("SELECT $other$different;value$other$::text, $$other secret$$, $2::int FROM t WHERE id = 99", DatabaseDialect.PostgreSql);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain("private", first.RedactedSql);
        Assert.DoesNotContain("secret", first.RedactedSql);
        Assert.Contains("::text", first.NormalizedSql.Replace(" ", ""));
        Assert.Contains("::int", first.NormalizedSql.Replace(" ", ""));
        Assert.Contains("where id", first.NormalizedSql);
        Assert.Single(first.Statements);
    }

    [Fact]
    public void Postgres_nested_block_comments_are_removed_entirely()
    {
        var commented = SqlFingerprinter.Fingerprint("SELECT id /* outside /* nested */ confidential */ FROM orders WHERE id = 1", DatabaseDialect.PostgreSql);
        var plain = SqlFingerprinter.Fingerprint("SELECT id FROM orders WHERE id = 2", DatabaseDialect.PostgreSql);

        Assert.Equal(plain.Fingerprint, commented.Fingerprint);
        Assert.DoesNotContain("confidential", commented.RedactedSql);
    }

    [Fact]
    public void Sql_server_bracket_identifiers_preserve_embedded_digits_and_escapes()
    {
        var result = SqlFingerprinter.Fingerprint("SELECT [Order 42], [weird]]name] FROM [dbo].[orders2] WHERE [id2]=@p1", DatabaseDialect.SqlServer);

        Assert.Contains("[Order 42]", result.RedactedSql);
        Assert.Contains("[weird]]name]", result.RedactedSql);
        Assert.Contains("[orders2]", result.NormalizedSql);
        Assert.Contains("[id2]", result.NormalizedSql);
        Assert.DoesNotContain("@p1", result.NormalizedSql);
    }

    [Fact]
    public void MySql_hash_comment_is_removed_but_hash_in_quoted_identifier_is_preserved()
    {
        var first = SqlFingerprinter.Fingerprint("SELECT `tag#name` FROM orders # internal customer secret\n WHERE id = 1", DatabaseDialect.MySql);
        var second = SqlFingerprinter.Fingerprint("SELECT `tag#name` FROM orders WHERE id = 2", DatabaseDialect.MySql);

        Assert.Equal(second.Fingerprint, first.Fingerprint);
        Assert.Contains("`tag#name`", first.RedactedSql);
        Assert.DoesNotContain("customer secret", first.RedactedSql);
    }

    [Theory]
    [InlineData(DatabaseDialect.MySql, "'can\\'t;publish'", "'hello'")]
    [InlineData(DatabaseDialect.PostgreSql, "'can''t;publish'", "'hello'")]
    [InlineData(DatabaseDialect.SqlServer, "N'can''t;publish'", "N'hello'")]
    public void Escaped_strings_do_not_split_statements(DatabaseDialect dialect, string firstLiteral, string secondLiteral)
    {
        var first = SqlFingerprinter.Fingerprint($"SELECT {firstLiteral} AS value; SELECT id FROM t WHERE status = 'open'", dialect);
        var second = SqlFingerprinter.Fingerprint($"SELECT {secondLiteral} AS value; SELECT id FROM t WHERE status = 'closed'", dialect);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(2, first.Statements.Length);
        Assert.Contains("from t", first.Statements[1]);
        Assert.DoesNotContain("publish", first.RedactedSql);
    }

    [Fact]
    public void Identifiers_with_digits_and_arithmetic_are_not_consumed_as_numeric_literals()
    {
        var result = SqlFingerprinter.Fingerprint("SELECT column2, 1+2, 3-4 FROM table3 WHERE id4 = 10", DatabaseDialect.PostgreSql);

        Assert.Contains("column2", result.NormalizedSql);
        Assert.Contains("table3", result.NormalizedSql);
        Assert.Contains("id4", result.NormalizedSql);
        Assert.Contains("?+?", result.NormalizedSql.Replace(" ", ""));
        Assert.Contains("?-?", result.NormalizedSql.Replace(" ", ""));
    }

    [Fact]
    public void Fingerprints_include_dialect_even_for_empty_input()
    {
        Assert.NotEqual(SqlFingerprinter.Fingerprint("", DatabaseDialect.MySql).Fingerprint,
            SqlFingerprinter.Fingerprint("", DatabaseDialect.PostgreSql).Fingerprint);
    }
}

public sealed class VendorPlanFormatTests
{
    [Fact]
    public void Postgres_explain_array_wrapper_preserves_operator_tree_and_actual_timing()
    {
        const string json = """
            [{"Plan":{"Node Type":"Hash Join","Join Type":"Inner","Startup Cost":11.2,
              "Total Cost":125.75,"Plan Rows":20,"Actual Rows":400,"Actual Total Time":12.5,
              "Plans":[
                {"Node Type":"Seq Scan","Relation Name":"orders","Plan Rows":20000,"Actual Rows":40000},
                {"Node Type":"Hash","Plan Rows":40,"Plans":[
                  {"Node Type":"Index Scan","Relation Name":"customers","Index Name":"customers_pkey","Plan Rows":40}]}]},
              "Planning Time":0.9,"Execution Time":13.1}]
            """;

        var plan = PlanParser.ParseJson(json, DatabaseDialect.PostgreSql, "16.4");
        var join = Assert.Single(PlanTestNodes.Flatten(plan.Root), n => n.NodeType == "Hash Join");

        Assert.Equal("Inner", join.JoinType);
        Assert.Equal(20, join.EstimatedRows);
        Assert.Equal(400, join.ActualRows);
        Assert.Equal(125.75, join.EstimatedCost);
        Assert.Equal(TimeSpan.FromMilliseconds(12.5), join.ActualDuration);
        Assert.Equal(2, join.Children.Length);
        var indexScan = Assert.Single(PlanTestNodes.Flatten(join), n => n.Relation == "customers");
        Assert.Equal("customers_pkey", indexScan.Index);
        Assert.Null(indexScan.ActualRows);
        Assert.Null(indexScan.ActualDuration);
    }

    [Fact]
    public void MySql_query_block_nested_loop_exposes_tables_indexes_and_access_methods()
    {
        const string json = """
            {"query_block":{"select_id":1,"cost_info":{"query_cost":"230.75"},"nested_loop":[
              {"table":{"table_name":"orders","access_type":"ALL","rows_examined_per_scan":20000,
                "rows_produced_per_join":3000,"filtered":"15.00","cost_info":{"read_cost":"100.00","eval_cost":"30.00","prefix_cost":"130.00"}}},
              {"table":{"table_name":"customers","access_type":"eq_ref","key":"PRIMARY","used_key_parts":["id"],
                "rows_examined_per_scan":1,"rows_produced_per_join":3000,"cost_info":{"prefix_cost":"230.75"}}}
            ]}}
            """;

        var plan = PlanParser.ParseJson(json, DatabaseDialect.MySql, "8.4.0");
        var nodes = PlanTestNodes.Flatten(plan.Root).ToArray();
        var orders = Assert.Single(nodes, n => n.Relation == "orders");
        var customers = Assert.Single(nodes, n => n.Relation == "customers");

        Assert.Equal(20000, orders.EstimatedRows);
        Assert.Equal("PRIMARY", customers.Index);
        Assert.True(orders.NodeType.Contains("scan", StringComparison.OrdinalIgnoreCase) || orders.NodeType.Contains("ALL", StringComparison.Ordinal));
        Assert.True(customers.NodeType.Contains("eq_ref", StringComparison.OrdinalIgnoreCase) || customers.NodeType.Contains("index", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nodes, n => n.EstimatedCost == 230.75);
        Assert.All(nodes, n => Assert.Null(n.ActualRows));
        Assert.All(nodes, n => Assert.Null(n.ActualDuration));
    }

    [Fact]
    public void SqlServer_showplan_resolves_object_and_parallel_runtime_counters_using_invariant_numbers()
    {
        const string xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.539" Build="16.0.1000.6">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT id FROM dbo.orders">
                <QueryPlan><RelOp NodeId="0" PhysicalOp="Index Scan" LogicalOp="Index Scan" EstimateRows="12.5" EstimatedTotalSubtreeCost="1.75">
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="0" ActualRows="7" ActualElapsedms="3" />
                    <RunTimeCountersPerThread Thread="1" ActualRows="11" ActualElapsedms="5" />
                  </RunTimeInformation>
                  <IndexScan Ordered="true"><Object Database="[shop]" Schema="[dbo]" Table="[orders]" Index="[IX_orders_date]" /></IndexScan>
                </RelOp></QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var plan = PlanParser.ParseXml(xml, DatabaseDialect.SqlServer, "2022");
            var scan = Assert.Single(PlanTestNodes.Flatten(plan.Root), n => n.NodeType == "Index Scan");

            Assert.Equal("[orders]", scan.Relation);
            Assert.Equal("[IX_orders_date]", scan.Index);
            Assert.Equal(12.5, scan.EstimatedRows);
            Assert.Equal(1.75, scan.EstimatedCost);
            Assert.Equal(18, scan.ActualRows);
            Assert.Equal(TimeSpan.FromMilliseconds(5), scan.ActualDuration);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("\"not a plan\"")]
    public void Invalid_plan_shape_is_rejected_with_a_data_error(string json)
    {
        Assert.ThrowsAny<Exception>(() => PlanParser.ParseJson(json, DatabaseDialect.PostgreSql));
    }
}

internal static class PlanTestNodes
{
    public static IEnumerable<PlanNode> Flatten(PlanNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
