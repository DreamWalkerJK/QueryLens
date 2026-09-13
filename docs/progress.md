# 实施进度与交付记录

2026-09-13 仓库检查：创建并切换 `codex/querylens-implementation`；远程为 `origin`。

| 里程碑 | 产出与状态 | Git 记录 |
|---|---|---|
| M1 | .NET10/Avalonia MVVM 桌面壳、工程与需求追踪 | `b17adf6` 已提交 |
| M2 | 方言词法、计划解析、SQLite、导入/增量/诊断测试 | `b17adf6` 已提交 |
| M3 | 实际数据库驱动、DPAPI 凭据、能力检测与采集约束 | `a2407b2`、`60eceef` 已提交 |
| M4 | 桌面连接、采集、离线、计划、比较、报告工作流 | `8e3caa5`、`06bcb53` 已提交并已 push |
| M4.1 | 原生文件选择器与桌面冒烟记录 | `60bc327` 已提交；push 因 GitHub 连接重置未成功 |
| M5 | MySQL/PostgreSQL disposable 验证、桌面启动、性能和交付文档 | `faa3384` 已提交并已 push |

## 验证快照

- `dotnet test QueryLens.slnx -c Release`: 48 passed, 0 failed, 0 skipped。
- `dotnet build QueryLens.slnx -c Release`: 0 warnings, 0 errors。
- Windows x64 framework-dependent 发布：`publish/win-x64`。
- MySQL 9.7.1 和 PostgreSQL 16.15 专属容器验证完成；SQL Server 与 Huawei GaussDB 保持待验证。
- 性能基线见 `docs/performance.md`。

## 推送记录

`8e3caa5`、`06bcb53` 已成功推送到 `origin/codex/querylens-implementation`。`60bc327` 与 `faa3384` 均已成功推送到 `origin/codex/querylens-implementation`。
