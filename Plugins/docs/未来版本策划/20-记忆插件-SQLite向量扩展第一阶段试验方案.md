# 20-记忆插件 SQLite 向量扩展第一阶段试验方案

> 创建日期：2026-05-12  
> 状态：方案确认中，待原型实现与测试落盘  
> 范围：Cortana.Plugins.Memory 记忆增强插件  
> 目标：在现有 SQLite 记忆库基础上，引入 SQLite 向量扩展进行第一阶段效果验证，同时控制 Native AOT 发布风险。

## 1. 背景

当前记忆插件使用 SQLite 保存观察记录、记忆片段、抽象记忆、关系、召回日志等数据。

当前召回链路主要位于：

- `Src/Cortana.Plugins.Memory/Services/MemoryRecallService.cs`
- `Src/Cortana.Plugins.Memory/Storage/MemoryStore.cs`

当前 `MemoryStore.SearchRecallCandidates` 使用：

- `agentId` / `workspaceId` 过滤
- `confidence`、`lifecycleState`、`confirmationState` 过滤
- `LIKE` 文本匹配
- `confidence`、`salienceScore`、`retentionScore`、`accessCount` 规则加权

该方式对明确关键词有效，但对同义表达、模糊表达、跨语言表达、上下文语义关联不足。

## 2. 决策

第一阶段选择“SQLite 向量扩展”路线进行验证。

原则：

1. 不替换当前召回链路。
2. 不破坏现有 SQLite 主表结构。
3. 新增向量表与向量召回服务作为可关闭能力。
4. 默认保持关键词召回可用，向量能力失败时自动降级。
5. 所有方案、测试记录、发布风险、回滚记录必须落盘。

## 3. 第一阶段目标

第一阶段不是直接上线完整向量数据库能力，而是验证以下问题：

| 问题 | 验证方式 | 通过标准 |
|---|---|---|
| SQLite 扩展能否随插件加载 | 本地 Debug / Release / publish 包运行 | 无加载异常，可检测扩展能力 |
| Native AOT 发布是否受影响 | 执行 `publish-memory.cmd`，验证 win-x64 AOT 输出 | 发布成功，插件启动成功 |
| 语义召回是否优于关键词召回 | 构造同义、模糊、跨语言查询集 | TopK 命中率高于 baseline |
| 性能是否可接受 | 对比关键词、向量、混合召回耗时 | P95 不超过目标阈值 |
| 失败能否降级 | 删除/禁用扩展后运行召回 | 自动回退关键词召回 |
| 数据是否可回滚 | 删除向量表/配置后运行旧链路 | 旧召回链路正常 |

## 4. 推荐技术路线

优先评估：

1. `sqlite-vec`
2. 备选：`sqlite-vss`

推荐优先 `sqlite-vec`，原因：

- 更轻量；
- 更适合嵌入式 SQLite 场景；
- 不强依赖外部服务；
- 与当前“插件独立本地数据库”的架构一致。

但最终采用哪一个扩展，以 AOT 发布验证结果为准。

## 5. AOT 风险控制

当前 `Cortana.Plugins.Memory.csproj` 对 `win-x64` 启用：

```xml
<PropertyGroup Condition="'$(RuntimeIdentifier)' == 'win-x64'">
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

因此 SQLite 向量扩展必须重点验证 Native AOT 场景。

### 5.1 主要风险

| 风险 | 说明 | 控制策略 |
|---|---|---|
| 原生扩展无法随 AOT 包加载 | SQLite 扩展通常是 native dll | 发布包中显式包含 native dll |
| `Microsoft.Data.Sqlite` 禁用扩展加载 | 需要调用 `EnableExtensions` / `LoadExtension` | 向量能力初始化时单独探测 |
| AOT 裁剪影响动态加载 | Native AOT 对反射和动态依赖更严格 | 不通过反射发现扩展，使用显式路径 |
| 跨平台包体差异 | win-x64、linux-x64 扩展文件不同 | 第一阶段只验证 win-x64，其他 RID 标记未支持 |
| 插件启动失败 | 扩展初始化异常冒泡 | 扩展加载失败只禁用 vector recall，不影响插件启动 |
| 发布体积增加 | native 扩展进入 zip | 记录发布包体积变化 |

### 5.2 AOT 验证要求

必须验证：

1. `dotnet publish` 成功。
2. `publish-memory.cmd` 成功。
3. zip 包包含向量扩展 native 文件。
4. 插件在发布包环境启动成功。
5. 向量扩展加载成功时可执行一次 smoke query。
6. 向量扩展加载失败时，插件仍能使用关键词召回。

### 5.3 降级策略

新增配置建议：

| 配置项 | 默认值 | 说明 |
|---|---:|---|
| `recall.vector.enabled` | `false` | 第一阶段默认关闭 |
| `recall.vector.provider` | `sqlite-vec` | 向量扩展实现 |
| `recall.vector.topK` | `50` | 向量候选数量 |
| `recall.vector.weight` | `0.45` | 混合排序中的向量权重 |
| `recall.keyword.weight` | `0.20` | 混合排序中的文本权重 |
| `recall.vector.failOpen` | `true` | 向量失败时回退关键词召回 |

第一阶段默认不改变生产召回行为，只有显式启用 `recall.vector.enabled=true` 后才进入向量召回。

## 6. 数据库设计

新增普通元数据表：

```sql
CREATE TABLE IF NOT EXISTS memory_embeddings (
  id TEXT PRIMARY KEY,
  memoryId TEXT NOT NULL,
  memoryKind TEXT NOT NULL,
  agentId TEXT NOT NULL,
  workspaceId TEXT NULL,
  embeddingModel TEXT NOT NULL,
  dimensions INTEGER NOT NULL,
  contentHash TEXT NOT NULL,
  embedding BLOB NOT NULL,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_embedding_memory_model
ON memory_embeddings(memoryId, memoryKind, embeddingModel);

CREATE INDEX IF NOT EXISTS IX_embedding_scope
ON memory_embeddings(agentId, workspaceId);
```

如果扩展支持虚拟表，新增向量索引表，例如：

```sql
-- 具体语法以最终扩展为准。
-- sqlite-vec / sqlite-vss 的建表语法不同，原型阶段不直接固化到主迁移。
```

注意：

- 第一阶段不要把向量扩展表写死为不可回滚主表依赖。
- `memory_embeddings` 可作为稳定元数据表。
- 扩展虚拟表可重建，不作为唯一事实源。

## 7. 召回链路设计

### 7.1 Baseline

保留当前关键词召回：

```text
MemoryRecallService
  -> MemoryStore.SearchRecallCandidates
  -> LIKE + rule score
```

### 7.2 Vector Recall

新增可选链路：

```text
MemoryRecallService
  -> MemoryVectorRecallService.SearchVectorCandidates
  -> SQLite vector extension topK
  -> 回表读取 memory_fragments / memory_abstractions
```

### 7.3 Hybrid Recall

合并两路候选：

```text
keyword candidates
+ vector candidates
-> by memoryId 去重
-> 统一计算 hybrid score
-> window grouping
-> recall log
```

推荐分数：

```text
hybridScore =
  vectorSimilarity * 0.45
  + textMatchScore * 0.20
  + confidence * 0.15
  + salienceScore * 0.10
  + retentionScore * 0.05
  + accessOrRecencyScore * 0.05
```

第一阶段可以只在 runner 中对比，不必一次性改生产链路。

## 8. 实施步骤

### S1：扩展加载探测

- 新增 SQLite 向量扩展加载器。
- 使用显式 native dll 路径。
- 加载失败只记录日志，不抛出到插件启动流程。
- 新增 smoke test：检查扩展函数/虚拟表是否可用。

### S2：Embedding 表与写入

- 新增 `memory_embeddings` 表。
- 对 `memory_fragments.summary/detail/title/topic` 生成 content hash。
- content hash 变化才重新生成 embedding。
- 第一阶段 embedding 可先通过测试数据或宿主提供模型生成，避免阻塞扩展验证。

### S3：Runner 验证

在 `Tools/MemoryRecallRunner` 增加试验能力或新增专用 runner：

- baseline keyword recall
- vector recall
- hybrid recall
- 输出 TopK、耗时、命中情况
- 将测试结果写入文档或 `TestResults/memory-vector/`

### S4：AOT 发布验证

执行：

- Debug 本地运行
- Release 非发布运行
- `publish-memory.cmd`
- 发布包插件启动
- 扩展可用/不可用两种情况

### S5：结果复盘

将测试结果写入：

- 本文档“测试记录”章节；或
- 单独测试报告：`docs/memory/构架规划/21-记忆插件-SQLite向量扩展第一阶段测试报告.md`

## 9. 测试矩阵

| 编号 | 场景 | 状态 | 结果 | 备注 |
|---|---|---|---|---|
| T01 | Debug 加载扩展 | 待执行 |  |  |
| T02 | Release 加载扩展 | 待执行 |  |  |
| T03 | win-x64 Native AOT publish | 待执行 |  |  |
| T04 | 发布包启动插件 | 待执行 |  |  |
| T05 | 发布包执行 smoke query | 待执行 |  |  |
| T06 | 禁用/删除扩展后降级关键词召回 | 待执行 |  |  |
| T07 | baseline 关键词召回 TopK 对比 | 待执行 |  |  |
| T08 | vector 召回 TopK 对比 | 待执行 |  |  |
| T09 | hybrid 召回 TopK 对比 | 待执行 |  |  |
| T10 | 1k 记忆性能测试 | 待执行 |  |  |
| T11 | 10k 记忆性能测试 | 待执行 |  |  |
| T12 | 发布包体积变化 | 待执行 |  |  |

## 10. 指标口径

| 指标 | 说明 |
|---|---|
| Top1 命中率 | 第一条是否命中预期记忆 |
| Top5 命中率 | 前 5 条是否包含预期记忆 |
| Top10 命中率 | 前 10 条是否包含预期记忆 |
| P50 延迟 | 单次召回中位耗时 |
| P95 延迟 | 单次召回 95 分位耗时 |
| P99 延迟 | 单次召回 99 分位耗时 |
| 扩展加载耗时 | 插件启动时加载向量扩展耗时 |
| 发布包体积 | 引入扩展前后 zip 大小 |
| 降级成功率 | 扩展不可用时关键词召回是否成功 |

## 11. 测试结果记录

> 每次执行必须追加记录，不覆盖历史结果。

### 2026-05-12 初始记录

| 项目 | 结果 |
|---|---|
| 当前阶段 | 方案落盘 |
| 是否已接入扩展 | 否 |
| 是否已执行 AOT 发布验证 | 否 |
| 已确认风险 | win-x64 当前启用 Native AOT，SQLite native extension 加载必须重点验证 |
| 当前结论 | 可以进入第一阶段原型，但必须默认关闭向量能力，并保证失败降级 |

## 12. 回滚方案

如果扩展验证失败：

1. 设置 `recall.vector.enabled=false`。
2. 不调用向量扩展加载逻辑。
3. 保留或删除 `memory_embeddings` 表均不影响原召回。
4. 删除扩展 native dll。
5. `MemoryRecallService` 回到现有关键词召回。
6. 发布包重新执行 `publish-memory.cmd`。

数据库回滚要求：

```sql
DROP TABLE IF EXISTS memory_embeddings;
-- 扩展虚拟表按实际名称删除。
```

注意：第一阶段不得让原有 `memory_fragments`、`memory_abstractions` 依赖向量表。

## 13. 当前建议

建议先实现最小闭环：

1. 扩展加载探测。
2. AOT 发布 smoke test。
3. `memory_embeddings` 表。
4. 小样本向量查询。
5. Runner 输出 baseline/vector/hybrid 对比。
6. 测试结果落盘。

只有当 AOT 发布、插件启动、失败降级三项全部通过后，才考虑进入生产召回链路。
