# 验证记录

目标 Windows 11 x64 / .NET10。首轮发现系统默认 SDK 是 11 preview，现使用 global.json 固定 10.0.102；最终需用稳定 SDK 重跑。

早期核心测试 12/12 通过，仅覆盖基础字面量、简单节点、诊断和连接元数据回读；未覆盖完整方言、真实数据库、增量、迁移、UI。测试代码和实现正在补强。

早期桌面构建及 win-x64 framework-dependent publish 成功；启动后 3 秒进程仍存活。尚未检查布局或交互，因此桌面冒烟未完成。早期产物不是最终交付版本。

Docker 29.6.1 可用；真实数据库测试将在专属本地容器进行，既有用户数据库不访问。Huawei GaussDB 未验证。

性能记录待执行，必须包含环境、数据库版本、输入字节/查询数/节点数、导入/解析耗时、峰值内存、CPU、并发和准确性。不能用小样例或测试运行时间证明高性能。

补充验证：增强后的 Core + Infrastructure 契约测试为 43/43 通过，覆盖四方言词法边界、PG/MySQL/SQL Server 计划包装和运行指标、坏文件拒绝、敏感信息、SQLite 持久化、适配器工厂与 GaussDB 未验证声明。`dotnet build QueryLens.slnx -c Release` 与 Windows x64 发布均成功。
