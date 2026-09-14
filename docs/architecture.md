# 架构决策

2026-09-13：仓库初始为空壳，采用 .NET SDK 10.0.102 / C#14 / Avalonia 12.1.2 / CommunityToolkit.Mvvm 8.4.2，Windows x64 为交付平台。global.json 防止使用系统默认的 .NET 11 preview。版本固定；其他平台尚未验证。

Core 负责模型、方言词法、导入、计划解析/比较、规则和本地数据；Infrastructure 负责成熟数据库驱动与 Windows DPAPI；Desktop 使用 MVVM 协调异步服务；Tests 独立于窗口。界面与核心不互相依赖，适配器按 capability 返回可用/不可用/权限不足/未验证。

本地 SQLite 保存版本化数据、快照元数据与来源信息，schema v4 迁移保留既有数据；快照记录批次完整性、实例/统计 epoch 和离线状态，只有可证明可比的累计快照才计算增量。密码仅保存在 Windows 当前用户保护的秘密存储。数据库访问默认只读，有限结果、超时和取消，不自动启用扩展或执行实际分析。实际采集与离线构造样例明确分离。原始 SQL 与脱敏/归一化分别持久化，默认报告仅含脱敏内容。

SkillTree 已阅读：DataBase/MySql/{数据库慢查询.sql,优化.sql,MySQL.md}，PostgreSQL/pssql.md，SQL Server/{SQLServer.md,SQL Server执行计划.md}，DotNet/CSharp专题/{异步编程.md,异步编程模型.md,IAsyncEnumerable.md,LINQ分组聚合与IQueryable.md,内存管理.md,性能优化SpanMemoryUnsafe.md,并行编程.md}，DesignPrinciples/SOLID.md 与 DesignPattern/设计模式.md。应用其异步取消、资源确定释放、流式输入、有界并发、先过滤投影分页、证据驱动优化原则。教程不足以作为数据库版本兼容证明，GaussDB 另需官方资料。

不引入 DDD/微服务/消息队列。先正确、可验证，再依据实测优化。
