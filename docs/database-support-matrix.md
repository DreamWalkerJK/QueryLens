# 数据库支持矩阵

| 产品 | 实现 | 真实实例验证 | 验证边界 |
|---|---|---|---|
| MySQL | 开发中 | 待验证 | performance_schema 摘要/JSON EXPLAIN/慢日志 |
| PostgreSQL | 开发中 | 待验证 | pg_stat_statements/JSON 计划 |
| SQL Server | 开发中 | 待验证 | DMV 或 Query Store/Showplan XML |
| Huawei GaussDB 集中/分布式 | 待分产品适配 | 未验证 | 需实际产品版本、兼容模式、驱动/认证信息 |
| GaussDB(for MySQL) | 待分产品适配 | 未验证 | 协议兼容不证明统计视图或字段兼容 |
| openGauss | 待独立适配 | 未验证 | 与 Huawei GaussDB 不等价；现有用户容器不作为测试环境 |

未连真实实例时，离线样例与契约测试仅验证解析和代码行为，不能称“完整支持”。每种采集方式必须显示最小权限、影响与探测结果，不自动启用配置。
