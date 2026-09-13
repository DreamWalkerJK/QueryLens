# 数据库支持矩阵

| 产品 | 代码状态 | 真实实例验证 | 已验证边界 |
|---|---|---|---|
| MySQL 9.7.1 | 已实现 | 已验证（专属 `mysql:latest` 容器） | `performance_schema` 摘要读取、能力探测、慢日志导入、JSON EXPLAIN 导入；采样间隔和权限错误分类 |
| PostgreSQL 16.15 | 已实现 | 已验证（专属 `postgres:16` 容器） | 产品/版本/兼容模式探测、`pg_stat_statements` 能力探测（未安装时正确报告不可用）、JSON 计划导入 |
| SQL Server | 已实现 | 待真实实例验证 | Query Store/DMV 读取契约、Showplan XML 离线导入；在线 Showplan 采集保持未验证并要求隔离连接 |
| Huawei GaussDB 集中/分布式 | 有限支持 | 未验证 | 产品形态、版本、兼容模式和驱动信息保留；统计能力按未验证处理，需真实产品和官方配置 |
| GaussDB(for MySQL) | 有限支持 | 未验证 | MySQL 协议形态单独说明；协议兼容不推断统计视图或字段完全兼容 |
| openGauss | 有限支持 | 未验证 | 与 Huawei GaussDB 不等价；不使用用户现有 openGauss 容器替代验证 |

离线样例、契约测试和解析测试只能证明代码行为，不能替代真实数据库兼容性验证。每项采集能力会显示最小权限、影响和探测结果；应用不会自动安装扩展或修改实例配置。
