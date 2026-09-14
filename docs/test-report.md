# 验证记录

目标平台为 Windows 11 x64，SDK 由 `global.json` 固定为 .NET 10.0.102。

## 自动化验证

```text
dotnet test QueryLens.slnx -c Release
Passed: 56  Failed: 0  Skipped: 0

dotnet build QueryLens.slnx -c Release
0 warnings  0 errors
```

测试覆盖四方言 SQL 指纹边界、字符串/转义/注释/引用/参数/多语句、PostgreSQL/MySQL/SQL Server 计划解析、未知节点保留、损坏输入拒绝、统计增量与重置、DPAPI/敏感信息脱敏、SQLite 持久化和迁移、快照分页/epoch 校验/缺失条目与基线保护、适配器错误分类、取消和配置校验。

## 真实数据库验证

- MySQL：专属 `mysql:latest` 容器，MySQL 9.7.1，`performance_schema` 能力可用，读取 5 条摘要；重复采集正确返回 `SamplingInterval`；最小权限账号正确返回 `PermissionDenied`。证据：[artifacts/adapter-verification/mysql-smoke.md](../artifacts/adapter-verification/mysql-smoke.md)。
- PostgreSQL：专属 `postgres:16` 容器，PostgreSQL 16.15，`DetectAsync` 返回产品/版本/兼容模式；未安装 `pg_stat_statements` 时返回 `Unavailable`，计划导入保持 `Available`。证据：[artifacts/adapter-verification/postgresql-smoke.md](../artifacts/adapter-verification/postgresql-smoke.md)。
- SQL Server：Showplan XML 离线解析和 Query Store/DMV 读取契约已验证；当前没有可用的专属实例，因此在线能力保持待验证。
- Huawei GaussDB：没有真实实例；按产品形态和兼容模式保守标记为未验证，openGauss 不替代该验证。

## 桌面冒烟

Windows x64 发布程序 [QueryLens.Desktop.exe](../publish/win-x64/QueryLens.Desktop.exe) 已实际启动，窗口标题为 `QueryLens · 数据库慢查询诊断`，进程保持响应；启动时自动创建 SQLite 表 `connections`、`plans`、`settings`、`slow_queries`，离线样例可写入并在查询/计划列表显示。主流程命令已连接到界面：保存/复制/删除连接、连接测试、导入慢日志/JSON、指纹聚合/筛选/趋势、导入 JSON/XML 计划、TreeView 节点详情、基线/比较、保留策略、取消长任务、脱敏报告导出。

原生文件选择器已接入，启动和交互边界见 [docs/ui-smoke.md](ui-smoke.md)。真正层级计划控件、历史趋势、筛选和保留策略已接入；原生文件选择器仍保留 Windows Shell 人工点击验收步骤。

## 性能

可复现的 10,000 条指纹和 1,000 份计划解析基线见 [docs/performance.md](performance.md)。该基线报告输入规模、计划节点数、耗时、CPU、托管内存、并发度和结果准确性，不外推生产吞吐量。
