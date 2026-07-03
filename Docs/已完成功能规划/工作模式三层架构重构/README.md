# 工作模式三层架构重构（文档集）

本目录收纳"工作模式三层组织架构重构"相关的所有方案与执行计划。文档随讨论持续拆分，以保持单文档聚焦。

## 当前文档清单

| 文档 | 用途 | 状态 |
|---|---|---|
| [工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md) | 总方案：A/B/C 三层定义、plan/environment 协议、阶段路线 | 已完成，阶段 0–4 均已落地 |
| [执行计划(Agent数据文件化迁移).md](执行计划(Agent数据文件化迁移).md) | 阶段 0：Agent 体系从数据库迁移到文件（含数据库切换 madorin.db） | 已完成，0.0–0.5 均已落地 |
| [执行计划(阶段0.0-清理废弃代码).md](执行计划(阶段0.0-清理废弃代码).md) | 阶段 0.0 独立子文档：P2 旧动态智能体残留清理 | 已完成，保留调研地图、清理记录、验证结果与人工验收备注 |
| [执行计划(三层架构骨架).md](执行计划(三层架构骨架).md) | 阶段 1：三层架构骨架 | 已完成 |
| [执行计划(上下文按Run隔离).md](执行计划(上下文按Run隔离).md) | 阶段 2：上下文按 Run 隔离 | 已完成 |
| [执行计划(渐进式计划展开).md](执行计划(渐进式计划展开).md) | 阶段 3：渐进式计划展开 | 已完成 |
| [执行计划(可靠性兜底与看门狗).md](执行计划(可靠性兜底与看门狗).md) | 阶段 4：可靠性兜底与看门狗 | 已完成 |
| [修复计划(A必须交付B).md](修复计划(A必须交付B).md) | 后续修复：D1 决策"A 不持有执行能力"代码层落地（5 commit），解决 25 步任务跑 13 步停止问题 | 已完成（端到端验证通过 / 25 步任务回归通过 / 2026-06-08 归档） |

## 当前验证状态

最后验证时间：2026-06-08。

```text
dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false
  通过：0 警告，0 错误

dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false
  通过：39/39

dotnet build Plugins/runner_data/memory-supply-probe/memory-supply-probe.csproj -p:UseSharedCompilation=false
  通过：0 警告，0 错误

rg "agent\.default\.xiaoyue|@xiaoyue" Plugins Src Tests
  零结果
```

## 计划拆分状态

| 拟拆分文档 | 来源 | 状态 |
|---|---|---|
| 执行计划(阶段0.5-数据库切换).md | Agent 文件化 §六.0.5 | 已由 [执行计划(Agent数据文件化迁移).md](执行计划(Agent数据文件化迁移).md) §六.0.5 / §八 / §十.D6 覆盖并落地，不再单独拆分 |

> 拆分原则：每份文档对应一个独立可上线的阶段或子阶段；超过 600 行就考虑拆。

## 阶段 0 子阶段一览（已敲定，D6 修订版 v3）

| 子阶段 | 内容 | 依赖 | 子文档 |
|---|---|---|---|
| 0.0 | 清理废弃代码（P2 旧动态智能体）| 无 | [执行计划(阶段0.0-清理废弃代码).md](执行计划(阶段0.0-清理废弃代码).md) |
| 0.1 | 基础设施（AgentFileService / Index / Watcher / Manifest）| 0.0 | 父文档 §六.0.1 |
| 0.2 | UI 适配（AgentSettingsPage 字段简化、默认 Agent 下拉、必填校验、bound_plugins 悬空插件灰显）| 0.1 | 父文档 §六.0.2 |
| 0.3 | AIAgentFactory 重构（工具源、Provider/Model 解析解耦）| 0.1（与 0.2 并行）| 父文档 §六.0.3 |
| 0.4 | AgentService 切换为文件代理 + 实体重命名（ChatSessionEntity / WorkTaskEntity 的 AgentId → AgentName）+ 6 个 LLM 工具改写 | 0.2 + 0.3 | 父文档 §六.0.4 |
| 0.5 | 新数据库 madorin.db + 从旧 cortana.db 选择性导入（用 `Db.SchemaVersion` 版本号判定）| 0.4 | 父文档 §六.0.5；已落地 |

> 阶段 0.6 / 0.7 已合并到 0.5（D6 修订版 v3）：旧库永不动，仅用版本号控制位判定首次升级；新库直接按目标 schema 建表，不做 ALTER/DROP。

## 阅读顺序建议

1. 先读 [工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md) §一–§五 理解动机和角色
2. 再读 §六–§八 理解上下文/约束/解决了什么
3. 然后读 §九 阶段路线
4. 读 [执行计划(Agent数据文件化迁移).md](执行计划(Agent数据文件化迁移).md) §十 是已敲定的 D1–D6 决策
5. 需要追溯实施细节时读各阶段执行计划；当前阶段 0–4 已全部完成

## 关联外部文档

- [智能体创建时机与工具上下文刷新问题讨论.md](../智能体创建时机与工具上下文刷新问题讨论.md)（已解决）
- [输入框工具开关](../../未来版本策划/输入框工具开关)（接口预留，本次不实现）
- [02-核心架构.md](../工作模式方案策划/02-核心架构.md) 等工作模式既有方案

## 已预留扩展点（未实现）

为未来"任务树（主任务 fork 子 WorkTask）"场景留的轻量预留位。当前**只在 schema 登记，代码不解析、B 不消费**——等真正的 ERP 级多模块并行需求出现时再补实现。

| 预留位 | 当前状态 | 未来用途 |
|---|---|---|
| `plan.yaml` 的 `step.kind` 字段 | 仅 schema 文档登记；YAML parser 忽略；默认语义 = `leaf` | `kind: sub_task` 时 B 看到 → fork 一个子 WorkTask + 子 plan.yaml；主 task 阻塞等所有子 task done |
| `WorkTasks.ParentTaskId` | 字段已存在（D5 加），handoff 路径已会写 | 子 WorkTask 指向主 task；UI 展示任务树；watchdog 多 B 实例独立扫描 |
| `WorkTaskEvents.RunId` | 字段已存在（D5 加） | 子 task 的 Milestone/Completion 事件能跨 task 冒泡到主 task 让主 B 解除阻塞 |

**为什么不预先实现**：YAGNI。当前主线任务（小说/调研/视频剪辑）都是"长但线性"，单 plan.yaml + 渐进式展开够用。等遇到 ERP 这种"真·多模块并行"再做，需求形态才清楚，避免现在设计的抽象未来作废。

**为什么至少留 schema 字段**：plan.yaml 是 yaml + 文档化 schema，写入 `kind: leaf` 完全无副作用；将来加 `Kind` 属性时不需要数据迁移。代价 ≈ 0。
