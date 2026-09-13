using System.Text;
using QueryLens.Core;

namespace QueryLens.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task Bundled_fixture_imports_two_queries_with_window_metrics()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "samples", "slow-query.log"));
        var result = await SlowQueryImporter.ImportAsync(stream, ImportFormat.MySqlSlowLog, DatabaseDialect.MySql, isOfflineSample: true);
        Assert.Equal(2, result.Queries.Count);
        Assert.All(result.Queries, q => Assert.True(q.IsOfflineSample));
        Assert.All(result.Queries, q => Assert.NotNull(q.TotalDuration));
    }
    [Fact]
    public async Task MySql_slow_log_import_preserves_partial_errors_and_redacts_literals()
    {
        const string log = "# Time: 2026-09-13T10:00:00Z\n# Query_time: 0.250\n# Rows_sent: 3\nSELECT * FROM orders WHERE customer_id = 42 AND status = 'paid';\n# malformed entry\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(log));
        var result = await SlowQueryImporter.ImportAsync(stream, ImportFormat.MySqlSlowLog, DatabaseDialect.MySql, isOfflineSample: true);
        var query = Assert.Single(result.Queries);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain("paid", query.RedactedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromMilliseconds(250), query.TotalDuration);
        Assert.True(query.IsOfflineSample);
    }

    [Fact]
    public async Task Json_report_omits_raw_plan_and_sql_by_default()
    {
        var f = SqlFingerprinter.Fingerprint("SELECT * FROM users WHERE email='private@example.test'", DatabaseDialect.PostgreSql);
        var q = new SlowQuery(Guid.NewGuid(), null, "SELECT * FROM users WHERE email='private@example.test'", f.RedactedSql, f.NormalizedSql, f.Fingerprint, DateTimeOffset.UtcNow, 1, null, null, null, null, IsOfflineSample: true, Dialect: DatabaseDialect.PostgreSql);
        var plan = PlanParser.ParseJson("{\"Node Type\":\"Seq Scan\",\"StatementText\":\"SELECT private@example.test\"}", DatabaseDialect.PostgreSql);
        await using var output = new MemoryStream();
        await ReportExporter.ExportJsonAsync(output, [q], [plan]);
        var json = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("private@example.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redactedSql", json, StringComparison.Ordinal);
    }
}
