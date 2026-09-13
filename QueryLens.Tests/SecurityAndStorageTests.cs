using Microsoft.Data.Sqlite;
using QueryLens.Core;

namespace QueryLens.Tests;

public sealed class SensitiveDataTests
{
    [Theory]
    [InlineData("Host=localhost;Password=my-private-value;Database=test", "my-private-value")]
    [InlineData("connection failed: Pwd=another-private-value;User Id=reader", "another-private-value")]
    [InlineData("Password=\"private value with spaces\";Host=localhost", "private value with spaces")]
    [InlineData("Password='private;value;with;semicolons';Host=localhost", "private;value;with;semicolons")]
    public void Exception_redaction_removes_the_entire_secret(string message, string secret)
    {
        var redacted = SensitiveData.Redact(message);

        foreach (var piece in secret.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            Assert.DoesNotContain(piece, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted);
    }
}

public sealed class PersistedAnalysisTests
{
    [Fact]
    public async Task Query_and_plan_save_after_initialization_and_are_written_to_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-analysis-{Guid.NewGuid():N}.db");
        var profile = new ConnectionProfile(Guid.NewGuid(), "fixture", DatabaseDialect.PostgreSql, "localhost", 5432, "orders");
        var fingerprint = SqlFingerprinter.Fingerprint("SELECT id FROM orders WHERE customer_id = 123", profile.Dialect);
        var query = new SlowQuery(Guid.NewGuid(), profile.Id, "SELECT id FROM orders WHERE customer_id = 123",
            fingerprint.RedactedSql, fingerprint.NormalizedSql, fingerprint.Fingerprint, DateTimeOffset.UtcNow,
            4, TimeSpan.FromMilliseconds(200), null, null, 12, "native-123", "orders", true);
        var plan = PlanParser.ParseJson("{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"orders\"}", profile.Dialect) with
        { ConnectionId = profile.Id, IsBaseline = true, QueryFingerprint = fingerprint.Fingerprint };
        try
        {
            await using (var store = new LocalStore(path))
            {
                await store.InitializeAsync();
                await store.SaveConnectionAsync(profile);
                await store.SaveSlowQueryAsync(query);
                await store.SavePlanAsync(plan);
            }

            await using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await database.OpenAsync();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT calls, total_ms, min_ms, max_ms, native_id, is_offline FROM slow_queries WHERE id=$id";
            command.Parameters.AddWithValue("$id", query.Id);
            using (var row = await command.ExecuteReaderAsync())
            {
                Assert.True(await row.ReadAsync());
                Assert.Equal(4, row.GetInt64(0));
                Assert.Equal(200, row.GetDouble(1));
                Assert.True(row.IsDBNull(2));
                Assert.True(row.IsDBNull(3));
                Assert.Equal("native-123", row.GetString(4));
                Assert.Equal(1, row.GetInt32(5));
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT is_baseline, fingerprint, raw_text FROM plans WHERE id=$id";
            command.Parameters.AddWithValue("$id", plan.Id);
            using var savedPlan = await command.ExecuteReaderAsync();
            Assert.True(await savedPlan.ReadAsync());
            Assert.Equal(1, savedPlan.GetInt32(0));
            Assert.Equal(fingerprint.Fingerprint, savedPlan.GetString(1));
            Assert.Equal(plan.RawText, savedPlan.GetString(2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        }
    }
}
