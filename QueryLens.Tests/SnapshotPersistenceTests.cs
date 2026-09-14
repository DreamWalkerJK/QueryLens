using QueryLens.Core;
namespace QueryLens.Tests;
public sealed class SnapshotPersistenceTests
{
    [Fact]
    public async Task Snapshot_and_query_metadata_round_trip_with_paging()
    {
        var path = Path.Combine(Path.GetTempPath(), $"querylens-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new LocalStore(path);
            await store.InitializeAsync();
            var connectionId = Guid.NewGuid();
            var snapshotId = Guid.NewGuid();
            var query = new SlowQuery(Guid.NewGuid(), connectionId, "select 1", "select ?", "select ?", "fp", DateTimeOffset.UtcNow, 3, TimeSpan.FromMilliseconds(90), null, null, null, "native-1", "db", false, DatabaseDialect.MySql, SnapshotSemantics.Cumulative, snapshotId, "epoch-1");
            var metadata = new SnapshotMetadata(snapshotId, connectionId, DatabaseDialect.MySql, "performance_schema", DateTimeOffset.UtcNow, 100, true, "db", "instance-1", "stats-1", true);
            await store.SaveSnapshotAsync(new SlowQuerySnapshot(metadata, [query]));
            var page = await store.GetSlowQueriesPageAsync(connectionId, 10, 0);
            var saved = Assert.Single(page);
            Assert.Equal(snapshotId, saved.SnapshotId);
            Assert.Equal("epoch-1", saved.CounterEpoch);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
