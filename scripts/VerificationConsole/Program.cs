using QueryLens.Core;
using QueryLens.Infrastructure;

var host = Environment.GetEnvironmentVariable("QUERYLENS_VERIFY_HOST") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("QUERYLENS_VERIFY_PORT"), out var parsedPort) ? parsedPort : 55432;
var password = Environment.GetEnvironmentVariable("QUERYLENS_VERIFY_PASSWORD") ?? throw new InvalidOperationException("Set QUERYLENS_VERIFY_PASSWORD at runtime.");
var profile = new ConnectionProfile(Guid.NewGuid(), "disposable-postgresql", DatabaseDialect.PostgreSql, host, port, "querylens_verify", AuthenticationMode.Password, false, 10, "querylens_reader", "runtime-secret");
var adapter = new PostgreSqlDatabaseAdapter(new EnvironmentSecretStore(password));
var info = await adapter.DetectAsync(profile);
Console.WriteLine($"product={info.Product}; version={info.Version}");
var capabilities = await adapter.GetCapabilitiesAsync(profile);
foreach (var capability in capabilities) Console.WriteLine($"capability={capability.Name}; status={capability.Status}; reason={capability.Reason}");
var queries = await adapter.ReadSlowQueriesAsync(profile, limit: 20);
Console.WriteLine($"queries={queries.Count}");
foreach (var query in queries) Console.WriteLine($"fingerprint={query.Fingerprint}; calls={query.Calls}; total_ms={query.TotalDuration?.TotalMilliseconds}; offline={query.IsOfflineSample}");

sealed class EnvironmentSecretStore(string value) : ISecretStore
{
    public Task<string> SaveAsync(string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default) => Task.FromResult<string?>(value);
    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
