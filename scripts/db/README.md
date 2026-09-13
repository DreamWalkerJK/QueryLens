# 数据库权限脚本

脚本只供 DBA 审核和手工执行，QueryLens 不会自动运行。请按最小权限授予专用只读账号，并按目标数据库版本审核对象名称。

- MySQL：`mysql-readonly.sql`（`performance_schema.events_statements_summary_by_digest`）。
- PostgreSQL：`postgresql-readonly.sql`（`pg_stat_statements` 视图读取；扩展启用由 DBA 决定）。
- SQL Server：`sqlserver-readonly.sql`（Query Store/DMV/Showplan 权限）。
- GaussDB：不提供通用授权脚本；产品形态和 `statement_history` 权限必须依据厂商文档确定。
