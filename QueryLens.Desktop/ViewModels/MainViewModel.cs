using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueryLens.Core;
using QueryLens.Infrastructure;

namespace QueryLens.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QueryLens");
    private LocalStore? _store;
    private ISecretStore? _secrets;
    private bool _initialized;
    private bool _isReloading;
    private ExecutionPlan? _beforePlan;
    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ObservableCollection<SlowQuery> Queries { get; } = [];
    public ObservableCollection<SlowQueryGroup> QueryGroups { get; } = [];
    public ObservableCollection<ExecutionPlan> Plans { get; } = [];
    public ObservableCollection<Capability> Capabilities { get; } = [];
    public ObservableCollection<DiagnosticFinding> Findings { get; } = [];
    public ObservableCollection<PlanChange> PlanChanges { get; } = [];
    public ObservableCollection<PlanNodeItem> PlanTreeRoots { get; } = [];
    public ObservableCollection<string> ErrorMessages { get; } = [];
    [ObservableProperty] private string statusMessage = "正在初始化本地工作区…";
    [ObservableProperty] private string connectionName = "本地样例连接";
    [ObservableProperty] private string host = "localhost";
    [ObservableProperty] private string port = "5432";
    [ObservableProperty] private string database = "querylens";
    [ObservableProperty] private string userName = "";
    [ObservableProperty] private string passwordInput = "";
    [ObservableProperty] private string dialectText = "PostgreSQL";
    [ObservableProperty] private string productVariant = "";
    [ObservableProperty] private string authenticationText = "密码";
    [ObservableProperty] private bool useTls = true;
    [ObservableProperty] private int timeoutSeconds = 30;
    [ObservableProperty] private string queryImportPath = "samples/slow-query.log";
    [ObservableProperty] private string planImportPath = "samples/postgres-plan.json";
    [ObservableProperty] private string reportExportPath = "QueryLens-report.json";
    [ObservableProperty] private string planVersion = "unknown";
    [ObservableProperty] private string planQueryFingerprint = "";
    [ObservableProperty] private string queryTimeFilter = "";
    [ObservableProperty] private string queryConnectionFilter = "";
    [ObservableProperty] private string queryDatabaseFilter = "";
    [ObservableProperty] private string queryFingerprintFilter = "";
    [ObservableProperty] private string queryTagFilter = "";
    [ObservableProperty] private int retentionDays = 30;
    [ObservableProperty] private string selectedPlanText = "请选择计划查看节点和诊断建议。";
    [ObservableProperty] private string rawPlanText = "";
    [ObservableProperty] private bool offlineMode = true;
    [ObservableProperty] private bool includeRawSql;
    [ObservableProperty] private bool confirmDelete;
    [ObservableProperty] private string queryFilterText = "";
    [ObservableProperty] private ConnectionProfile? selectedConnection;
    [ObservableProperty] private SlowQuery? selectedQuery;
    [ObservableProperty] private SlowQueryGroup? selectedQueryGroup;
    [ObservableProperty] private ExecutionPlan? selectedPlan;
    [ObservableProperty] private PlanNodeItem? selectedPlanNode;
    [ObservableProperty] private string selectedPlanNodeDetails = "请选择计划节点查看详情。";
    public IReadOnlyList<string> Dialects { get; } = ["MySQL", "PostgreSQL", "SQL Server", "GaussDB"];
    public IReadOnlyList<string> AuthenticationModes { get; } = ["密码", "集成认证", "令牌"];
    public IReadOnlyList<string> ImportFormats { get; } = ["MySQL 慢查询日志", "JSON 统计快照"];
    [ObservableProperty] private string importFormatText = "MySQL 慢查询日志";
    public IEnumerable<SlowQuery> FilteredQueries => Queries.Where(MatchesQuery);
    private bool MatchesQuery(SlowQuery q)
        => (string.IsNullOrWhiteSpace(QueryFilterText)
            || q.Fingerprint.Contains(QueryFilterText, StringComparison.OrdinalIgnoreCase)
            || q.RedactedSql.Contains(QueryFilterText, StringComparison.OrdinalIgnoreCase)
            || (q.Database?.Contains(QueryFilterText, StringComparison.OrdinalIgnoreCase) ?? false))
        && (string.IsNullOrWhiteSpace(QueryConnectionFilter) || (q.ConnectionId?.ToString().Contains(QueryConnectionFilter, StringComparison.OrdinalIgnoreCase) ?? false))
        && (string.IsNullOrWhiteSpace(QueryDatabaseFilter) || (q.Database?.Contains(QueryDatabaseFilter, StringComparison.OrdinalIgnoreCase) ?? false))
        && (string.IsNullOrWhiteSpace(QueryFingerprintFilter) || q.Fingerprint.Contains(QueryFingerprintFilter, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(QueryTagFilter) || q.RedactedSql.Contains(QueryTagFilter, StringComparison.OrdinalIgnoreCase))
        && (!DateTimeOffset.TryParse(QueryTimeFilter, out var since) || q.ObservedAt >= since);
    public string QueryTrendSummary => SelectedQuery is null
        ? "选择查询后显示同指纹历史趋势。"
        : string.Join("  ·  ", Queries.Where(q => q.Fingerprint == SelectedQuery.Fingerprint)
            .OrderBy(q => q.ObservedAt).TakeLast(12)
            .Select(q => $"{q.ObservedAt:MM-dd HH:mm}={FormatDuration(q.TotalDuration, q.Calls)}"));
    public string SelectedQuerySummary => SelectedQuery is null ? "尚未选择慢查询。" : $"{SelectedQuery.Dialect} · {SelectedQuery.Fingerprint} · 调用 {SelectedQuery.Calls} · 平均耗时 {FormatDuration(SelectedQuery.TotalDuration, SelectedQuery.Calls)}";
    public MainViewModel() => PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedPlan)) SelectPlan(SelectedPlan); if (e.PropertyName == nameof(SelectedPlanNode)) SelectPlanNode(SelectedPlanNode); if (e.PropertyName == nameof(SelectedQuery)) OnPropertyChanged(nameof(SelectedQuerySummary)); if (e.PropertyName is nameof(QueryTimeFilter) or nameof(QueryConnectionFilter) or nameof(QueryDatabaseFilter) or nameof(QueryFingerprintFilter) or nameof(QueryTagFilter) or nameof(QueryFilterText)) { ApplyQueryFilters(); OnPropertyChanged(nameof(FilteredQueries)); } if (e.PropertyName == nameof(OfflineMode) && _initialized) _ = RefreshAsync(default); };
    partial void OnSelectedQueryChanged(SlowQuery? value)
    {
        OnPropertyChanged(nameof(SelectedQuerySummary));
        if (value is not null && string.IsNullOrWhiteSpace(PlanQueryFingerprint)) PlanQueryFingerprint = value.Fingerprint;
        ApplyQueryFilters();
    }
    partial void OnSelectedQueryGroupChanged(SlowQueryGroup? value)
    {
        if (value is null) return;
        SelectedQuery = Queries.FirstOrDefault(q => q.Fingerprint.Equals(value.Fingerprint, StringComparison.OrdinalIgnoreCase));
    }
    partial void OnQueryFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredQueries));
    partial void OnSelectedConnectionChanged(ConnectionProfile? value)
    {
        if (value is null) return;
        ConnectionName = value.Name; Host = value.Host; Port = value.Port.ToString(); Database = value.Database;
        UserName = value.UserName ?? ""; DialectText = value.Dialect switch { DatabaseDialect.MySql => "MySQL", DatabaseDialect.SqlServer => "SQL Server", DatabaseDialect.GaussDb => "GaussDB", _ => "PostgreSQL" };
        ProductVariant = value.ProductVariant ?? ""; UseTls = value.UseTls; TimeoutSeconds = value.TimeoutSeconds;
        AuthenticationText = value.Authentication switch { AuthenticationMode.Integrated => "集成认证", AuthenticationMode.Token => "令牌", _ => "密码" };
        if (_initialized && !_isReloading && !OfflineMode) _ = RefreshAsync(default);
    }
    public async Task InitializeAsync()
    {
        if (_initialized) return; _initialized = true; Directory.CreateDirectory(_dataDirectory); _store = new LocalStore(Path.Combine(_dataDirectory, "querylens.db")); await _store.InitializeAsync(); if (OperatingSystem.IsWindows()) _secrets = new DpapiSecretStore(Path.Combine(_dataDirectory, "secrets")); RetentionDays = int.TryParse(await _store.GetSettingAsync("retention_days", "30"), out var days) ? Math.Clamp(days, 0, 3650) : 30; await ReloadAsync(); if (OfflineMode && Queries.Count == 0) await LoadOfflineSamplesAsync(default); StatusMessage = OfflineMode ? "离线样例模式：未连接真实数据库" : "本地工作区已加载";
    }
    [RelayCommand] private async Task SaveConnectionAsync(CancellationToken token)
    { try { EnsureStore(); var previous = SelectedConnection; var p = await BuildProfileAsync(token); await _store!.SaveConnectionAsync(p, token); if (previous is not null && previous.Id == p.Id && previous.SecretReference is not null && previous.SecretReference != p.SecretReference && _secrets is not null) await _secrets.DeleteAsync(previous.SecretReference, token); SelectedConnection = p; await ReloadAsync(token); StatusMessage = $"连接“{p.Name}”已保存；密码只保存为受保护引用。"; } catch (Exception ex) { ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 保存连接：{ex.Message}"); StatusMessage = UserMessage(ex); } }
    [RelayCommand] private async Task TestConnectionAsync(CancellationToken token)
    { try { if (OfflineMode) { StatusMessage = "当前为离线样例模式，未发起网络连接。关闭离线模式后再测试。"; return; } var p = await BuildProfileAsync(token); var a = DatabaseAdapterFactory.Create(p.Dialect, _secrets); var info = await a.DetectAsync(p, token); var caps = await a.GetCapabilitiesAsync(p, token); Capabilities.Clear(); foreach (var c in caps) Capabilities.Add(c); StatusMessage = $"已连接 {info.Product} {info.Version}；能力 {caps.Count} 项。"; } catch (Exception ex) { StatusMessage = UserMessage(ex); } }
    [RelayCommand(IncludeCancelCommand = true)] private async Task CollectAsync(CancellationToken token)
    { try { EnsureStore(); if (OfflineMode) { await LoadOfflineSamplesAsync(token); return; } var p = await BuildProfileAsync(token); var rows = await DatabaseAdapterFactory.Create(p.Dialect, _secrets).ReadSlowQueriesAsync(p, limit: 500, cancellationToken: token); var snapshotId = Guid.NewGuid(); var snapshot = new SlowQuerySnapshot(new SnapshotMetadata(snapshotId, p.Id, p.Dialect, "live-collection", DateTimeOffset.UtcNow, 500, rows.Count < 500, p.Database, null, null, false, false), rows.Select(r => r with { SnapshotId = snapshotId }).ToArray()); await _store!.SaveSnapshotAsync(snapshot, token); await ReloadAsync(token); StatusMessage = $"已采集 {rows.Count} 条统计并保存快照；累计计数器不会被当成窗口平均值。"; } catch (Exception ex) { StatusMessage = UserMessage(ex); } }
    [RelayCommand(IncludeCancelCommand = true)] private async Task ImportQueriesAsync(CancellationToken token)
    { try { EnsureStore(); await using var stream = File.OpenRead(ResolvePath(QueryImportPath)); var f = ImportFormatText.StartsWith("JSON", StringComparison.OrdinalIgnoreCase) ? ImportFormat.JsonSnapshot : ImportFormat.MySqlSlowLog; var result = await SlowQueryImporter.ImportAsync(stream, f, ParseDialect(), SelectedConnection?.Id, Database, OfflineMode, token); foreach (var q in result.Queries) await _store!.SaveSlowQueryAsync(q, token); await ReloadAsync(token); StatusMessage = result.Errors.Count == 0 ? $"已导入 {result.Queries.Count} 条慢查询。" : $"导入 {result.Queries.Count} 条，另有 {result.Errors.Count} 条数据质量错误。"; if (result.Errors.Count > 0) foreach (var error in result.Errors.Take(10)) ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 日志导入：{error}"); } catch (Exception ex) { ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 日志导入：{ex.Message}"); StatusMessage = $"导入失败：{ex.Message}"; } }
    [RelayCommand(IncludeCancelCommand = true)] private async Task ImportPlanAsync(CancellationToken token)
    { try { EnsureStore(); var fingerprint = PlanQueryFingerprint.Trim(); if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("请先选择慢查询或填写 SQL 指纹，才能关联执行计划。", nameof(PlanQueryFingerprint)); var path = ResolvePath(PlanImportPath); var raw = await File.ReadAllTextAsync(path, token); var d = ParseDialect(); var p = Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? PlanParser.ParseXml(raw, d, PlanVersion, SelectedConnection?.Id) : PlanParser.ParseJson(NormalizePlanJson(raw), d, PlanVersion, SelectedConnection?.Id); p = p with { IsOfflineSample = OfflineMode, QueryFingerprint = fingerprint, SourceOrigin = OfflineMode ? "offline-sample" : (SelectedConnection?.Database ?? Database) }; await _store!.SavePlanAsync(p, token); await ReloadAsync(token); SelectedPlan = p; StatusMessage = $"已导入计划：{p.Root.NodeType}；已关联指纹 {fingerprint}。"; } catch (Exception ex) { ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 计划导入：{ex.Message}"); StatusMessage = $"计划导入失败：{ex.Message}"; } }
    [RelayCommand(IncludeCancelCommand = true)] private async Task ExportReportAsync(CancellationToken token)
    { try { EnsureStore(); await using var stream = File.Create(ResolvePath(ReportExportPath)); await ReportExporter.ExportJsonAsync(stream, Queries, Plans, IncludeRawSql, token); StatusMessage = $"报告已导出：{ResolvePath(ReportExportPath)}（原始 SQL：{(IncludeRawSql ? "用户显式选择" : "已脱敏")}）"; } catch (Exception ex) { StatusMessage = $"报告导出失败：{ex.Message}"; } }
    [RelayCommand] private async Task LoadOfflineSamplesAsync(CancellationToken token)
    { try { EnsureStore(); OfflineMode = true; var id = Guid.Parse("00000000-0000-0000-0000-000000000001"); var sql = "SELECT * FROM orders WHERE customer_id = 42 ORDER BY created_at DESC"; var f = SqlFingerprinter.Fingerprint(sql, DatabaseDialect.PostgreSql, "querylens-sample"); var q = new SlowQuery(id, null, sql, f.RedactedSql, f.NormalizedSql, f.Fingerprint, DateTimeOffset.UtcNow, 1840, TimeSpan.FromMilliseconds(78752), TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(1840), 120, "offline-qry-001", "querylens-sample", true, DatabaseDialect.PostgreSql, SnapshotSemantics.Window); var raw = await File.ReadAllTextAsync(ResolvePath("samples/postgres-plan.json"), token); var p = PlanParser.ParseJson(NormalizePlanJson(raw), DatabaseDialect.PostgreSql, "sample", null) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), IsOfflineSample = true, QueryFingerprint = f.Fingerprint, SourceOrigin = "offline-sample" }; var changedRaw = raw.Replace("Seq Scan", "Index Scan", StringComparison.Ordinal); var p2 = PlanParser.ParseJson(NormalizePlanJson(changedRaw), DatabaseDialect.PostgreSql, "sample", null) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), IsOfflineSample = true, QueryFingerprint = f.Fingerprint, SourceOrigin = "offline-sample" }; await _store!.SaveSlowQueryAsync(q, token); await _store.SavePlanAsync(p with { IsBaseline = true }, token); await _store.SavePlanAsync(p2, token); await ReloadAsync(token); SelectedPlan = p; StatusMessage = "离线样例已加载；包含同指纹基线与变化计划，样例与真实采集结果严格分离。"; } catch (Exception ex) { StatusMessage = $"样例加载失败：{ex.Message}"; } }
    [RelayCommand] private async Task SelectBaselineAsync(CancellationToken token) { if (SelectedPlan is null) { StatusMessage = "请先选择计划，再标记基线。"; return; } try { EnsureStore(); var baseline = SelectedPlan with { IsBaseline = true }; await _store!.SetPlanBaselineAsync(baseline.Id, token); _beforePlan = baseline; await ReloadAsync(token); SelectedPlan = Plans.FirstOrDefault(p => p.Id == baseline.Id); StatusMessage = "已标记并持久化当前计划为基线。"; } catch (Exception ex) { ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 标记基线：{ex.Message}"); StatusMessage = $"标记基线失败：{ex.Message}"; } }
    [RelayCommand] private void CompareSelectedPlans() { PlanChanges.Clear(); if (_beforePlan is null || SelectedPlan is null) { StatusMessage = "请先选择基线计划，再选择后续计划。"; return; } try { foreach (var c in PlanComparer.Compare(_beforePlan, SelectedPlan)) PlanChanges.Add(c); StatusMessage = $"计划比较完成：{PlanChanges.Count} 项变化。"; } catch (Exception ex) { StatusMessage = $"计划比较已拒绝：{ex.Message}"; } }
    [RelayCommand] private void DuplicateConnection() { ConnectionName += " 副本"; SelectedConnection = null; StatusMessage = "已复制连接字段；保存后才会创建新的本地记录。"; }
    [RelayCommand] private async Task DeleteConnectionAsync(CancellationToken token) { if (SelectedConnection is null) { StatusMessage = "请先选择要删除的连接。"; return; } if (!ConfirmDelete) { StatusMessage = $"请先勾选确认，再删除连接“{SelectedConnection.Name}”。"; return; } try { var target = SelectedConnection; var name = target.Name; await _store!.DeleteConnectionAsync(target.Id, token); if (_secrets is not null) await _secrets.DeleteAsync(target.SecretReference, token); ConfirmDelete = false; SelectedConnection = null; await ReloadAsync(token); StatusMessage = $"连接“{name}”已删除。"; } catch (Exception ex) { StatusMessage = UserMessage(ex); } }
    [RelayCommand] private async Task RefreshAsync(CancellationToken token) { try { await ReloadAsync(token); StatusMessage = $"已刷新：{Queries.Count} 条查询，{Plans.Count} 份计划。"; } catch (Exception ex) { StatusMessage = UserMessage(ex); } }
    [RelayCommand] private async Task SaveRetentionAsync(CancellationToken token) { try { EnsureStore(); if (RetentionDays is < 0 or > 3650) throw new ArgumentOutOfRangeException(nameof(RetentionDays), "保留天数必须为 0–3650。"); await _store!.SaveSettingAsync("retention_days", RetentionDays.ToString(), token); var removed = await _store.ApplyRetentionAsync(RetentionDays, token); await ReloadAsync(token); StatusMessage = $"已保存保留策略：{RetentionDays} 天；清理 {removed} 条记录。"; } catch (Exception ex) { ErrorMessages.Insert(0, $"{DateTimeOffset.Now:T} 保留策略：{ex.Message}"); StatusMessage = $"保留策略失败：{ex.Message}"; } }
    private async Task BuildConnectionListAsync(CancellationToken token) { var selectedId = SelectedConnection?.Id; Connections.Clear(); foreach (var c in await _store!.GetConnectionsAsync(token)) Connections.Add(c); if (selectedId.HasValue) SelectedConnection = Connections.FirstOrDefault(c => c.Id == selectedId.Value); }
    private async Task ReloadAsync(CancellationToken token = default) { EnsureStore(); _isReloading = true; try { await BuildConnectionListAsync(token); Queries.Clear(); foreach (var q in await _store!.GetSlowQueriesAsync(OfflineMode ? null : SelectedConnection?.Id, 1000, token)) if (q.IsOfflineSample == OfflineMode) Queries.Add(q); ApplyQueryFilters(); Plans.Clear(); foreach (var p in await _store.GetPlansAsync(OfflineMode ? null : SelectedConnection?.Id, 500, token)) if (p.IsOfflineSample == OfflineMode) Plans.Add(p); _beforePlan = Plans.FirstOrDefault(p => p.IsBaseline); } finally { _isReloading = false; } }
    private async Task<ConnectionProfile> BuildProfileAsync(CancellationToken token) { if (!int.TryParse(Port, out var p) || p is < 1 or > 65535) throw new ArgumentException("端口必须为 1–65535。", nameof(Port)); if (TimeoutSeconds is < 1 or > 300) throw new ArgumentException("超时必须为 1–300 秒。", nameof(TimeoutSeconds)); var r = SelectedConnection?.SecretReference; if (!string.IsNullOrEmpty(PasswordInput)) { if (_secrets is null) throw new PlatformNotSupportedException("当前系统没有可用的受保护凭据存储。"); r = await _secrets.SaveAsync(PasswordInput, token); PasswordInput = ""; } var auth = AuthenticationText switch { "集成认证" => AuthenticationMode.Integrated, "令牌" => AuthenticationMode.Token, _ => AuthenticationMode.Password }; return new(SelectedConnection?.Id ?? Guid.NewGuid(), ConnectionName.Trim(), ParseDialect(), Host.Trim(), p, Database.Trim(), auth, UseTls, TimeoutSeconds, string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(), r, ProductVariant.Trim()); }
    private DatabaseDialect ParseDialect() => DialectText switch { "MySQL" => DatabaseDialect.MySql, "SQL Server" => DatabaseDialect.SqlServer, "GaussDB" => DatabaseDialect.GaussDb, _ => DatabaseDialect.PostgreSql };
    private string ResolvePath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
    private void EnsureStore() { if (_store is null) throw new InvalidOperationException("本地工作区尚未初始化，请稍候重试。"); }
    private void SelectPlan(ExecutionPlan? p) { Findings.Clear(); PlanTreeRoots.Clear(); SelectedPlanNode = null; SelectedPlanNodeDetails = "请选择计划节点查看详情。"; if (p is null) { SelectedPlanText = "请选择计划查看节点和诊断建议。"; RawPlanText = ""; return; } RawPlanText = p.RawText; SelectedPlanText = RenderTree(p.Root, 0); PlanTreeRoots.Add(new PlanNodeItem(p.Root)); foreach (var f in DiagnosticEngine.Analyze(p)) Findings.Add(f); }
    private void SelectPlanNode(PlanNodeItem? item) { if (item is null) { SelectedPlanNodeDetails = "请选择计划节点查看详情。"; return; } var n = item.Node; var p = SelectedPlan; SelectedPlanNodeDetails = $"操作符：{n.NodeType}\n关系：{n.Relation ?? "不可用"}\n索引：{n.Index ?? "不可用"}\n连接：{n.JoinType ?? "不可用"}\n估算行数：{n.EstimatedRows?.ToString("g") ?? "不可用"}\n实际行数：{n.ActualRows?.ToString("g") ?? "不可用"}\n估算成本：{n.EstimatedCost?.ToString("g") ?? "不可用"}（保留引擎原生成本单位）\n实际耗时：{(n.ActualDuration is null ? "不可用" : $"{n.ActualDuration.Value.TotalMilliseconds:g} ms")}\n来源：{p?.SourceOrigin ?? "不可用"}\n版本：{p?.SourceVersion ?? "不可用"}\n方法：{p?.CollectionMethod ?? "不可用"}\n采集时间：{p?.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"; }
    private void ApplyQueryFilters() { if (Queries.Count == 0) { QueryGroups.Clear(); OnPropertyChanged(nameof(FilteredQueries)); OnPropertyChanged(nameof(QueryTrendSummary)); return; } var source = Queries.Where(MatchesQuery); QueryGroups.Clear(); foreach (var g in SlowQueryAggregator.Group(source)) QueryGroups.Add(g); OnPropertyChanged(nameof(FilteredQueries)); OnPropertyChanged(nameof(QueryTrendSummary)); }
    private static string RenderTree(PlanNode n, int d) => new StringBuilder().Append(' ', d * 2).Append(n.NodeType).Append(n.Relation is null ? "" : $" · 表 {n.Relation}").Append(n.Index is null ? "" : $" · 索引 {n.Index}").Append(n.JoinType is null ? "" : $" · 连接 {n.JoinType}").Append(n.EstimatedRows is null ? "" : $" · 估算行数 {n.EstimatedRows:g}").Append(n.ActualRows is null ? "" : $" · 实际行数 {n.ActualRows:g}").Append(n.EstimatedCost is null ? "" : $" · 估算成本 {n.EstimatedCost:g}").Append(n.ActualDuration is null ? "" : $" · 实际耗时 {n.ActualDuration.Value.TotalMilliseconds:g} ms").AppendLine().Append(string.Concat(n.Children.Select(c => RenderTree(c, d + 1)))).ToString();
    private static string NormalizePlanJson(string raw) => raw.Replace("\"plan\":", "\"Plan\":", StringComparison.OrdinalIgnoreCase);
    private static string FormatDuration(TimeSpan? total, long calls) => total is null || calls <= 0 ? "不可用" : $"{total.Value.TotalMilliseconds / calls:0.##} ms";
    private static string UserMessage(Exception ex) => ex is AdapterException ? ex.Message : $"操作失败：{ex.Message}";
}
