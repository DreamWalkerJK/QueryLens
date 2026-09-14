using System.Collections.Immutable;

namespace QueryLens.Core;

public enum DatabaseDialect { MySql, PostgreSql, SqlServer, GaussDb, Unknown }
public enum AuthenticationMode { Password, Integrated, Token, None }
public enum CapabilityStatus { Available, Unavailable, PermissionDenied, Unverified }
public enum Severity { Info, Low, Medium, High, Critical }
public enum SnapshotSemantics { Window, Cumulative }

/// Metadata describing one statistics read.  Epoch and completeness fields are
/// deliberately explicit: cumulative counters are never treated as a window
/// unless the source proves that both snapshots are comparable.
public sealed record SnapshotMetadata(Guid Id, Guid? ConnectionId, DatabaseDialect Dialect,
    string Source, DateTimeOffset CollectedAt, int RequestedLimit, bool IsComplete,
    string? Database = null, string? InstanceEpoch = null, string? StatisticsEpoch = null,
    bool EpochsVerified = false, bool IsOfflineSample = false, Guid? PreviousSnapshotId = null);
public sealed record SlowQuerySnapshot(SnapshotMetadata Metadata, IReadOnlyList<SlowQuery> Queries);

public sealed record ConnectionProfile(Guid Id, string Name, DatabaseDialect Dialect, string Host, int Port, string Database,
    AuthenticationMode Authentication = AuthenticationMode.Password, bool UseTls = true, int TimeoutSeconds = 30,
    string? UserName = null, string? SecretReference = null, string? ProductVariant = null);
public sealed record DatabaseInfo(string Product, string Version, string? CompatibilityMode = null);
public sealed record Capability(string Name, CapabilityStatus Status, string Description, string MinimumPrivilege,
    string? Reason = null);
public sealed record SlowQuery(Guid Id, Guid? ConnectionId, string RawSql, string RedactedSql, string NormalizedSql,
    string Fingerprint, DateTimeOffset ObservedAt, long Calls, TimeSpan? TotalDuration, TimeSpan? MinDuration,
    TimeSpan? MaxDuration, long? Rows, string? NativeQueryId = null, string? Database = null, bool IsOfflineSample = false,
    DatabaseDialect Dialect = DatabaseDialect.Unknown, SnapshotSemantics Semantics = SnapshotSemantics.Window,
    Guid? SnapshotId = null, string? CounterEpoch = null);
public sealed record PlanNode(string NodeType, string? Relation = null, string? Index = null, string? JoinType = null,
    double? EstimatedRows = null, double? ActualRows = null, double? EstimatedCost = null, TimeSpan? ActualDuration = null,
    ImmutableArray<PlanNode> Children = default, ImmutableDictionary<string,string>? Attributes = null)
{
    public ImmutableArray<PlanNode> Children { get; init; } = Children.IsDefault ? ImmutableArray<PlanNode>.Empty : Children;
}
public sealed record ExecutionPlan(Guid Id, Guid? ConnectionId, DatabaseDialect Dialect, string SourceVersion,
    string CollectionMethod, DateTimeOffset CollectedAt, string RawText, PlanNode Root, bool IsBaseline = false,
    string? QueryFingerprint = null, bool IsOfflineSample = false, string? SourceOrigin = null);
public sealed record DiagnosticFinding(string RuleId, string Title, string Evidence, DatabaseDialect Dialect,
    string? Version, Severity Severity, double Confidence, string Verification, string Tradeoffs, Guid? PlanId = null);

public sealed record FingerprintResult(string RedactedSql, string NormalizedSql, string Fingerprint, DatabaseDialect Dialect,
    int NormalizationVersion = 1, ImmutableArray<string> Statements = default);
