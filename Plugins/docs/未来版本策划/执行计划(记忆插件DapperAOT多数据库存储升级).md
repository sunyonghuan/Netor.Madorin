# Dapper.AOT + SQL 方言层 + Provider Store 升级计划 : 0%

## 背景

当前记忆插件使用 `Microsoft.Data.Sqlite` + 手写 SQL 直接访问 SQLite。该方案对 Native AOT 友好、性能可控，但随着 `setting.json` 已引入 `storage.provider`，后续需要支持 SQL Server、PostgreSQL 或其他数据库时，必须先把存储 Provider、连接创建、SQL 方言和业务 Store 解耦。

本计划采用：

```text
Dapper.AOT + SQL 方言层 + Provider Store
```

目标不是一次性替换现有 SQLite 实现，而是在保持现有功能稳定的前提下，逐步增加可扩展存储架构。

---

## 总体目标

1. 保持当前 SQLite 存储默认可用。
2. 引入 Provider Store 架构，支持按 `setting.json` 选择存储实现。
3. 引入 SQL 方言层，隔离 SQLite / SQL Server / PostgreSQL 的 SQL 差异。
4. 引入 Dapper.AOT，降低手写 reader 映射复杂度，同时保持 Native AOT 友好。
5. 后续可平滑增加 SQL Server / PostgreSQL Provider。
6. 保留现有 `IMemoryStore` 业务接口，避免影响上层记忆服务。

---

## 目标架构

```text
IMemoryStore
  ├─ SqliteMemoryStore              当前默认实现，可逐步迁移到 Dapper.AOT
  ├─ SqlServerMemoryStore           后续实现
  └─ PostgresMemoryStore            后续实现

IMemoryDbConnectionFactory
  ├─ SqliteMemoryDbConnectionFactory
  ├─ SqlServerMemoryDbConnectionFactory
  └─ PostgresMemoryDbConnectionFactory

IMemorySqlDialect
  ├─ SqliteMemorySqlDialect
  ├─ SqlServerMemorySqlDialect
  └─ PostgresMemorySqlDialect

MemoryStorageProviderFactory
  └─ 根据 setting.json 的 storage.provider 注册对应实现
```

---

## Step 1 设计存储 Provider 抽象 : 0%

- [×] 新增 `IMemoryDbConnectionFactory`，统一创建 `DbConnection`。
- [×] 新增 `IMemoryStorageProvider` 或等价注册入口，用于表达 provider 能力。
- [×] 明确 provider 名称规范：`sqlite`、`sqlserver`、`postgresql`。
- [×] 扩展 `setting.json` 文档说明，定义 provider 切换规则。
- [×] 保持 `IMemoryStore` 作为业务层唯一依赖，不让业务服务直接依赖 Dapper。

### 验收标准

- `storage.provider=sqlite` 时行为与当前一致。
- 非 sqlite provider 未实现时，应给出明确错误，而不是静默 fallback。

---

## Step 2 引入 SQL 方言层 : 0%

- [×] 新增 `IMemorySqlDialect`。
- [×] 将以下差异从 Store 查询中抽出：
  - [×] 当前 UTC 时间表达式。
  - [×] `LIMIT` / `TOP` / `OFFSET FETCH`。
  - [×] Upsert 语法。
  - [×] 建表 SQL 类型映射。
  - [×] 布尔值表达。
  - [×] JSON 查询表达式。
- [×] 新增 `SqliteMemorySqlDialect`，先覆盖当前 SQLite 语法。
- [×] 预留 `SqlServerMemorySqlDialect` 文件或设计说明。

### 验收标准

- SQLite 查询仍可通过现有测试和手工验证。
- 新增 SQL 不直接散落在业务服务中。

---

## Step 3 引入 Dapper.AOT : 0%

- [×] 增加 Dapper.AOT 依赖。
- [×] 检查 Native AOT 发布是否产生 trimming 警告。
- [×] 为高频 DTO / Model 建立明确映射规则。
- [×] 避免使用 `dynamic`、运行时未知列、复杂多映射。
- [×] 优先迁移只读查询：
  - [×] `ListRecentMemories`
  - [×] `GetStatusSnapshot`
  - [×] `SearchRecallCandidates`
  - [×] `GetProfileRecallCandidates`
  - [×] `GetRecentRecallCandidates`

### 验收标准

- Dapper.AOT 查询在 Native AOT 发布下无关键裁剪警告。
- 高频读取路径结果与旧实现一致。

---

## Step 4 拆分 SQLite Provider Store : 0%

- [×] 将当前 `MemoryStore` 中的 SQLite 专有 SQL 逐步迁移到 `SqliteMemoryStore` 或 SQLite Provider 内部。
- [×] 保持 `IMemoryStore` 接口不变。
- [×] 表服务如 `MemoryFragmentsTable`、`MemoryAbstractionsTable` 可继续保留，但应逐步依赖连接工厂和方言层。
- [×] 将初始化 SQL 和迁移 SQL 归入 SQLite Provider。
- [×] 增加 provider 级初始化日志，输出 provider、database path、schema version。

### 验收标准

- 插件启动时能明确记录当前 provider。
- 现有 SQLite 数据库无需迁移即可继续使用。

---

## Step 5 增加 SQL Server Provider 预研实现 : 0%

- [×] 新增 SQL Server 连接工厂。
- [×] 新增 SQL Server 方言实现。
- [×] 转换建表 SQL：
  - [×] `TEXT` -> `nvarchar(max)` / `nvarchar(450)`。
  - [×] `REAL` -> `float` 或 `decimal`。
  - [×] `INTEGER` -> `int` / `bigint`。
  - [×] `0/1 bool` -> `bit`。
- [×] 转换 Upsert 逻辑为 `MERGE` 或事务内 `UPDATE + INSERT`。
- [×] 转换分页和排序 SQL。
- [×] 设计全文检索替代方案：SQL Server Full-Text Search。

### 验收标准

- 能在空 SQL Server 数据库初始化 schema。
- 能完成基础写入、召回、供应、配置读取。
- 与 SQLite 行为保持一致。

---

## Step 6 数据迁移工具规划 : 0%

- [×] 设计 SQLite -> SQL Server 导出导入流程。
- [×] 增加迁移命令或独立工具：
  - [×] 读取 SQLite。
  - [×] 批量写入目标数据库。
  - [×] 校验行数。
  - [×] 校验关键表 checksum 或抽样数据。
- [×] 保留回滚策略：迁移不删除原 SQLite 文件。
- [×] 迁移后更新 `setting.json` 的 provider 和连接字符串。

### 验收标准

- 可以从现有 SQLite 数据库迁移到新 provider。
- 迁移完成后主动注入、召回、配置工具可正常工作。

---

## Step 7 性能与健康检查 : 0%

- [×] 增加数据库健康检查工具或内部诊断服务。
- [×] 输出数据库大小、表行数、最大表、索引信息。
- [×] 输出召回查询耗时 P50 / P95。
- [×] SQLite 下输出 WAL / vacuum / optimize 建议。
- [×] SQL Server 下输出索引缺失和全文索引状态。

### 验收标准

- 能判断当前数据库是否需要迁移或优化。
- 能辅助定位召回慢、写入慢、锁等待等问题。

---

## Step 8 发布与兼容性策略 : 0%

- [×] 默认 provider 仍为 `sqlite`。
- [×] 旧版无 `setting.json` 时自动使用 SQLite 默认配置。
- [×] `setting.json` 中未知 provider 必须明确报错。
- [×] 新 provider 标记为实验性，避免默认启用。
- [×] 文档说明不同 provider 的适用场景和限制。

### 验收标准

- 老用户无感升级。
- 新用户可以通过配置切换实验性存储。
- 发布包中包含 `setting.json` 模板。

---

## 推荐实施顺序

```text
1. 保留当前 SQLite 实现
2. 抽象连接工厂和 SQL 方言
3. 引入 Dapper.AOT 迁移只读高频查询
4. 拆分 SQLite Provider Store
5. 预研 SQL Server Provider
6. 增加迁移工具和健康检查
```

---

## 风险点

| 风险 | 说明 | 应对 |
|---|---|---|
| Native AOT 裁剪警告 | Dapper 普通反射映射可能有风险 | 优先使用 Dapper.AOT，避免 dynamic |
| SQL 方言复杂 | 多数据库 SQL 无法完全通用 | 方言层隔离，不写通用大 SQL |
| 数据迁移风险 | SQLite 到 SQL Server 类型差异 | 增加迁移校验和回滚 |
| 行为不一致 | 排序、分页、时间函数可能不同 | 增加验收测试和抽样对比 |
| 插件体积增加 | 新 provider 引入额外依赖 | provider 依赖按需引用或独立包 |

---

## 当前不做事项

- [×] 不立即替换全部 SQLite 手写 SQL。
- [×] 不默认启用 SQL Server。
- [×] 不把 EF Core 作为插件默认 ORM。
- [×] 不让业务服务直接依赖 Dapper。
- [×] 不在未完成迁移工具前删除旧 SQLite 数据。

---

## 涉及文件规划

预计新增或调整：

```text
Plugins/Src/Madorin.Plugins.Memory/Storage/Providers/
Plugins/Src/Madorin.Plugins.Memory/Storage/Dialects/
Plugins/Src/Madorin.Plugins.Memory/Storage/Connections/
Plugins/Src/Madorin.Plugins.Memory/Storage/Sqlite/
Plugins/Src/Madorin.Plugins.Memory/Storage/SqlServer/
Plugins/Src/Madorin.Plugins.Memory/setting.json
```

---

## 进度记录

- 2026-05-14：创建升级计划文档。
