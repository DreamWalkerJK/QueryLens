# QueryLens

面向开发者和 DBA 的数据库慢查询、执行计划与性能变化分析桌面应用，已交付可运行的 Windows x64 Avalonia 客户端。应用支持创建、保存、复制、测试和删除连接，导入或采集慢查询，查看 SQL 指纹，导入 JSON/XML 执行计划，保存基线并比较计划，显示规则诊断建议，以及导出默认脱敏的 JSON 报告。

## 运行

Windows x64 开发环境需要 .NET SDK 10.0.102：

```powershell
dotnet run --project QueryLens.Desktop -c Release
```

构建和测试：

```powershell
dotnet build QueryLens.slnx -c Release
dotnet test QueryLens.slnx -c Release
```

已验证发布目录为 [publish/win-x64](publish/win-x64)，入口程序为 [QueryLens.Desktop.exe](publish/win-x64/QueryLens.Desktop.exe)。

## 使用边界

应用默认只读，不自动启用扩展、Query Store、慢查询日志或全局参数。密码使用 Windows 当前用户 DPAPI 保存，配置、日志和报告不写入明文密码。离线样例模式在界面中明确标记，并按 `IsOfflineSample` 与真实采集结果隔离。

连接测试会读取真实产品、版本和兼容模式，并显示每项能力的可用、不可用、权限不足或未验证状态。MySQL、PostgreSQL、SQL Server 和 GaussDB 使用独立适配器；GaussDB 按产品形态、协议和兼容模式保守处理，未连接 Huawei GaussDB 实例的能力不会标记为已验证。

导入路径支持 MySQL 慢查询日志、JSON 统计快照和 JSON/XML 执行计划。报告默认只导出脱敏 SQL，只有显式勾选“包含原始 SQL”才会写入原文。长任务支持取消，采集具备 5 秒最小间隔和结果上限。

## 文档

- [需求追踪](docs/requirements.md)
- [架构说明](docs/architecture.md)
- [数据库支持矩阵](docs/database-support-matrix.md)
- [测试报告](docs/test-report.md)
- [性能记录](docs/performance.md)
- [阶段进度](docs/progress.md)
- [DBA 权限脚本](scripts/db/README.md)

真实数据库验证只使用专属 disposable Docker 容器；不会访问用户现有数据库服务或配置。

## 可复现实例验证

仓库包含一个不写入凭据的 PostgreSQL 验证控制台，源码位于 `scripts/VerificationConsole`。先准备专属 disposable PostgreSQL 实例和只读账号（控制台默认连接数据库 `querylens`、用户 `querylens_reader`），再通过环境变量提供连接信息；密码只在进程环境中读取，不会写入源码或输出：

```powershell
$env:QUERYLENS_VERIFY_HOST = "127.0.0.1"
$env:QUERYLENS_VERIFY_PORT = "55432"
$env:QUERYLENS_VERIFY_PASSWORD = "<runtime-only-password>"
dotnet run --project scripts/VerificationConsole/VerificationConsole.csproj -c Release
```

`QUERYLENS_VERIFY_HOST` 默认 `127.0.0.1`，`QUERYLENS_VERIFY_PORT` 默认 `55432`；`QUERYLENS_VERIFY_PASSWORD` 必须设置。控制台会探测产品/版本和能力，并读取最多 20 条慢查询摘要。验证完成后清除当前 PowerShell 会话中的环境变量；不要把真实密码提交到仓库或命令历史。
