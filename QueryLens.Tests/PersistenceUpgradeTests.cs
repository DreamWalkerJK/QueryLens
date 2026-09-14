using Microsoft.Data.Sqlite;
using QueryLens.Core;

namespace QueryLens.Tests;

public sealed class PersistenceUpgradeTests
{
    [Fact]
    public async Task Existing_initial_schema_is_upgraded_without_losing_connections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-upgrade-{Guid.NewGuid():N}.db");
        var id = Guid.NewGuid();
        try
        {
            await using (var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await original.OpenAsync();
                using var command = original.CreateCommand();
                command.CommandText = """
                    CREATE TABLE connections(id TEXT PRIMARY KEY,name TEXT NOT NULL,dialect TEXT NOT NULL,host TEXT,port INTEGER,database_name TEXT,auth TEXT,use_tls INTEGER,timeout_seconds INTEGER,user_name TEXT,secret_ref TEXT);
                    CREATE TABLE slow_queries(id TEXT PRIMARY KEY,connection_id TEXT,raw_sql TEXT,redacted_sql TEXT,normalized_sql TEXT,fingerprint TEXT,observed_at TEXT,calls INTEGER,total_ms REAL,min_ms REAL,max_ms REAL,rows_count INTEGER,native_id TEXT,is_offline INTEGER);
                    CREATE TABLE plans(id TEXT PRIMARY KEY,connection_id TEXT,dialect TEXT,version TEXT,method TEXT,collected_at TEXT,raw_text TEXT,is_baseline INTEGER,fingerprint TEXT,is_offline INTEGER);
                    INSERT INTO connections VALUES($id,'existing','PostgreSql','localhost',5432,'shop','Password',1,15,'reader','credential-manager:existing');
                    PRAGMA user_version=1;
                    """;
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }

            await using var upgraded = new LocalStore(path);
            await upgraded.InitializeAsync();
            var existing = Assert.Single(await upgraded.GetConnectionsAsync());
            Assert.Equal(id, existing.Id);
            Assert.Equal("existing", existing.Name);
            Assert.Equal("credential-manager:existing", existing.SecretReference);
            Assert.Empty(await upgraded.GetSlowQueriesAsync());
            Assert.Empty(await upgraded.GetPlansAsync());
            await upgraded.SaveSettingAsync("retention-days", "30");
            Assert.Equal("30", await upgraded.GetSettingAsync("retention-days"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        }
    }

    [Fact]
    public async Task Snapshot_round_trip_and_filtered_paging_keep_source_boundaries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new LocalStore(path);
            await store.InitializeAsync();
            var cid = Guid.NewGuid();
            var fingerprint = SqlFingerprinter.Fingerprint("select * from t where id=1", DatabaseDialect.MySql);
            var metadata = new SnapshotMetadata(Guid.NewGuid(), cid, DatabaseDialect.MySql, "perf", DateTimeOffset.UtcNow, 100, true, "db", "i1", "s1", true);
            var query = new SlowQuery(Guid.NewGuid(), cid, "select * from t where id=1", fingerprint.RedactedSql, fingerprint.NormalizedSql, fingerprint.Fingerprint, metadata.CollectedAt, 2, TimeSpan.FromMilliseconds(10), null, null, null, "native", "db", false, DatabaseDialect.MySql, SnapshotSemantics.Cumulative, CounterEpoch: "e1");
            await store.SaveSnapshotAsync(new SlowQuerySnapshot(metadata, [query]));
            var result = await store.GetSlowQueriesPageAsync(cid, 10, 0, false, "perf");
            var saved = Assert.Single(result);
            Assert.Equal(metadata.Id, saved.SnapshotId);
            Assert.Equal("e1", saved.CounterEpoch);
            Assert.Empty(await store.GetSlowQueriesPageAsync(cid, 10, 0, true, "perf"));
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Retention_does_not_delete_persisted_baseline_plans()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-retention-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new LocalStore(path);
            await store.InitializeAsync();
            var old = DateTimeOffset.UtcNow.AddDays(-20);
            var plan = PlanParser.ParseJson("{\"Node Type\":\"Seq Scan\"}", DatabaseDialect.PostgreSql) with { CollectedAt = old, IsBaseline = true, QueryFingerprint = "fp" };
            await store.SavePlanAsync(plan);
            await store.ApplyRetentionAsync(7);
            Assert.True(Assert.Single(await store.GetPlansAsync()).IsBaseline);
        }
        finally { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    }
}
