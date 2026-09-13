# QueryLens

面向开发者和 DBA 的数据库慢查询、执行计划与性能变化分析桌面应用。当前正在按 [完整需求](docs/goal-objective.md) 分阶段实现，已建立 .NET10/Avalonia MVVM 框架，功能验收状态见 [需求追踪](docs/requirements.md)。

Windows x64 开发环境需要 .NET SDK 10.0.102：

```powershell
dotnet build QueryLens.slnx -c Release
dotnet run --project QueryLens.Desktop -c Release
```

当前窗口为工作区壳，后续里程碑接入真实连接、离线导入、计划分析与脱敏导出。不要把框架产物当作完整应用验收。

设计边界：默认只读、不自动启用数据库配置；密码使用系统保护存储；离线样例明确标识；未经真实实例验证的数据库能力明确记为未验证。

文档：[架构](docs/architecture.md) · [支持矩阵](docs/database-support-matrix.md) · [测试记录](docs/test-report.md) · [阶段进度](docs/progress.md)。
