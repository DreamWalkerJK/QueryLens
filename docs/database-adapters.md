# 数据库适配器

`QueryLens.Infrastructure` 提供四个只读适配器：MySQL (`MySqlConnector` 2.6.2)、PostgreSQL (`Npgsql` 10.0.3)、SQL Server (`Microsoft.Data.SqlClient` 6.1.4) 和 GaussDB/openGauss（Npgsql 协议连接，仅做产品识别）。入口是 `DatabaseAdapterFactory.Create(DatabaseDialect, ISecretStore?)`，所有连接、读取方法均为异步并接受 `CancellationToken`。

适配器从真实服务器读取产品信息和兼容模式。`ReadSlowQueriesAsync` 仅查询已有累计统计并限制 1–1000 行：MySQL 使用 `performance_schema.events_statements_summary_by_digest`（皮秒转换为毫秒），PostgreSQL 使用 `pg_stat_statements` 的 `total_exec_time/min_exec_time/max_exec_time`，SQL Server 使用 Query Store runtime stats。累计计数器重置、实例重启和淘汰由调用方通过快照时间识别；平均耗时不会被当成窗口平均值。

GaussDB 产品形态差异较大，适配器只执行 `version()` 产品识别并返回 `Unverified` 的 `gaussdb.statement_history` 能力，不假设 PostgreSQL 系统视图存在；统计快照应由用户导入。当前环境有 openGauss 容器，但未在本阶段连接或修改任何用户数据库。

最小权限建议：MySQL 对摘要表 `SELECT`；PostgreSQL 安装并配置 `pg_stat_statements` 且允许视图读取；SQL Server `VIEW DATABASE STATE`（Query Store）或 `VIEW SERVER STATE`（DMV），Showplan 采集需要 `SHOWPLAN`。应用不会自动启用扩展、Query Store、日志或修改全局参数。

官方参考： [MySQL statement summary tables](https://dev.mysql.com/doc/refman/8.0/en/performance-schema-statement-summary-tables.html)、[PostgreSQL pg_stat_statements](https://www.postgresql.org/docs/16/pgstatstatements.html)、[SQL Server Query Store runtime stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-runtime-stats-transact-sql?view=sql-server-ver16)、[openGauss statement history](https://docs.opengauss.org/en/docs/6.0.0/docs/DatabaseReference/statement_history.html)。

Windows 密码使用当前用户 DPAPI 保存在应用数据目录；连接字符串、日志和异常消息不会包含明文密码。生产环境应启用 TLS 证书校验（默认开启）。
