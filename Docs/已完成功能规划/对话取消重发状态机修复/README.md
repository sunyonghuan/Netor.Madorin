# 对话取消重发问题 — 架构债拆解方案（文档集）

> 当前版本：**v7.3（终稿，已拍板）**
> 状态：✅ 已完成 / 已落地，已整体迁移到 `Docs/已完成功能规划/`
> 最后核对：2026-06-13

本目录是"对话模式取消重发"问题的完整方案集。原始单文档 1129 行已拆分为 5 篇主文档 + 1 篇结构档，按主题独立可读。

---

## 文档清单

| 顺序 | 文档 | 篇幅 | 读者关注点 |
| --- | --- | --- | --- |
| **0** | **[阶段0-实施前对齐.md](阶段0-实施前对齐.md)** | **~280 行** | **🚦 实施第一行代码前必做**：公共契约签名快照 + 13 条链路当前行为基准 + 4 条实施纪律签字 |
| 1 | [01-问题诊断.md](01-问题诊断.md) | ~170 行 | 取消语义"停+留+截"三段定型 / 当前架构债 / AiChatHostedService 15 种职责 |
| 2 | [02-目标架构.md](02-目标架构.md) | ~180 行 | 修复目标 / 11 个新组件拆解 / ChatTurnContext 一等公民 / 端到端时序 |
| 3 | [03-阶段路线.md](03-阶段路线.md) | ~230 行 | 5 阶段 4 周路线（每阶段独立可上线、独立验收）/ 阶段间依赖 |
| 4 | [04-链路修复矩阵.md](04-链路修复矩阵.md) | ~400 行 | **核心** / Cortana 体系 13 条核心链路 / 6 条被打断链路的架构级修复方案 / 公共契约保留清单 / 验收清单 |
| 5 | [05-兼容性与决策.md](05-兼容性与决策.md) | ~185 行 | 与现有代码的兼容性 / 不改造的代价 / 核心论点 / 待决策事项收敛 |
| — | [对话引擎核心链路架构图.md](对话引擎核心链路架构图.md) | ~590 行 | **结构档** — 全景图 + 5 个关键链路序列图 + turn 状态机 + 跨模式协作矩阵 |

**最短阅读路径**（仅需了解决策结果）：本文档（〇 + 〇之二）→ [05-兼容性与决策.md §十一 核心论点](05-兼容性与决策.md)

**完整阅读路径**（参与实施）：按 1 → 2 → 3 → 4 → 5 顺序读完，配合 [架构图](对话引擎核心链路架构图.md) 对照看。

**实施者上手路径**：先读完 1-5 + 架构图 → 再做 [阶段 0](阶段0-实施前对齐.md) 全部对齐工作 → 才能写第一行代码。

**评审者快速对齐**：本页 §〇之二 关键决策快速参考表，5 分钟掌握全部拍板结果。

---

## 〇、版本演进

| 版本 | 主张 | 问题 |
| --- | --- | --- |
| v1 | "WaitForCurrentRunToStopAsync 死循环 + VM 乐观取消" | 只看到主线症状 |
| v2 | "7 个症状互相喂饭形成雪崩" | 把表象误认为独立 bug |
| v3 | "专家模式要补齐工作模式的插话/排队能力" | over-engineering，专家模式不需要 |
| v4 | "取消 = 立即丢弃当前 turn" | "丢弃"不准（已产出必须保留） |
| v5 | 取消 = "停 + 留 + 截"三段 + 拆解 15 种职责 | 漏了主路径丢失、事件契约不匹配等 4 个事实 |
| v6 | + 新增 `ChatHistoryReplayService` 解决"DB→AgentSession 回灌缺失" | **回灌缺失是误判** — 路径已通过 AF 钩子 `ProvideChatHistoryAsync` 实现 |
| v7 | v5 + v6 的合理校正 - v6 的过度设计；明确不破坏现有压缩链路；阶段重新拆为 5 阶段 4 周；**`IAiOutputChannel` 接口破坏性变更让 turn-aware 成为编译期强制契约** | 评审发现 4 处文档内自相矛盾 |
| v7.1 | v7 + 4 处评审修正：①阶段 1 加 turn 终止事件去重守卫 ②`OnConversationTurnZombieKilled` 改为复用 `Completed(Failed)` ③阶段 5 不删 `IAiChatEngine.Stop()`/`CancelCurrentTask()` ④§2.1 修正 OnDoneAsync 表述 | 用户进一步质疑：对工作/会议模式有没有破坏 |
| v7.2 | v7.1 + **完整破坏面系统回归**：新增工作/会议破坏面矩阵 + 公共契约保留清单（23 处外部调用全部转发）+ 字段删除精确分类 | 用户校正：v7.2 用"零破坏"绕过了真问题 |
| **v7.3（终稿）** | v7.2 重写 §七 为**架构级链路修复矩阵**：13 条核心链路 / 6 条被打断 + 6 不打断 + 1 不实现 / 每条打断链路都有架构级修复方案 / 13 条链路逐条验收清单 | **当前版本（已拍板）** |

---

## 〇之二、关键决策快速参考（实施前对齐）

| 决策点 | 拍板结果 | 一句话理由 |
| --- | --- | --- |
| 取消语义 | "停 + 留 + 截"三段 | 用户最终校正定型，匹配 1 对 1 对话本质（详见 [01](01-问题诊断.md) §1.1） |
| 不引入"插话"/"排队" | 不引入 | 那是工作/会议模式的产物；专家模式是 1 对 1 对话不需要 |
| 不破坏压缩链路 | 完全不动 | AF 钩子 `ProvideChatHistoryAsync` 已实现"压给 AI"那端（详见 [01](01-问题诊断.md) §2.4 / §2.5） |
| 不新增 ChatHistoryReplayService | 作废 | 回灌路径已存在（AF 钩子），v6 误判 |
| `IAiOutputChannel` 接口扩 turnId | **破坏性变更** | 让 turn-aware 在编译期强制；4 个内部实现同步改（详见 [05](05-兼容性与决策.md) §8.2） |
| `IAiChatEngine` 公共方法 | **完全不删** | `WebSocketInputChannel:69`、Voice 多处仍在调 `Stop()`/`CancelCurrentTask()`；只改内部实现 |
| `AiChatHostedService` 公共访问器 | **全部保留+转发** | 12 处外部调用（CurrentSessionResolver、WorkModeInputVm、MeetingInputVm）；阶段 2/3.4 把字段迁到独立组件后转发暴露（详见 [04](04-链路修复矩阵.md) §7.3） |
| 工作模式 / 会议模式破坏面 | **6 条被打断 + 6 条不打断 + 1 条不实现** | v7.3 不再说"零破坏"；承认改造必然有影响，每条被打断的链路都有架构级修复方案（详见 [04](04-链路修复矩阵.md) §7.2） |
| 13 条核心链路完整性 | **逐条验收** | [04](04-链路修复矩阵.md) §7.4 给出每条链路的运行时验收方式，阶段 5 完成后必须全部通过 |
| 阶段 4 事件方案 | 复用 `Status` 枚举 | 不动 EventHub 契约；跨进程订阅方零感知 |
| 5s 超时上报 | 复用 `Completed(Failed, ErrorMessage)` | 不新增事件类型；与 [05](05-兼容性与决策.md) §8.4 "Events.cs 零改动"一致 |
| Turn 终止事件去重 | 阶段 1 必做 | 同一 turnId 只发一次 Completed；取消入口和 catch 块共用守卫（详见 [03](03-阶段路线.md) 阶段 1） |
| `_cancelledTurnIds` 清理策略 | 60s LRU | 够长覆盖最坏 HTTP 慢断；够短不无限增长 |
| 总周期 | **4 周 / 5 阶段** | 阶段 1+2 一周止血并修主路径，后续 3 周清理架构债（详见 [03](03-阶段路线.md)） |
| 阶段 3 切片 | 7 个 sub PR | 每个独立可验证可回滚 |

**最重要的两个判断**：

1. **真正的瓶颈不是"功能缺失"是"职责耦合"** — 2082 行单 service 把 15 种职责绑在共享字段上，任何修补丁都会复活。**只有职责拆分 + turn 一等公民 才是从根上修复**。
2. **接口契约必须强制 turn-aware** — "截"是用户硬要求 #4，让它沦为可选契约 = 把 bug 模板化。**破坏性变更是用编译器把 bug 排除在编译期，不是修补丁**。

---

## 实施前对齐 checklist

实施第一行代码之前，必须完成下面两组对齐：

### 组 1：阅读对齐（团队所有成员）

- [ ] 阅读 [01-问题诊断.md](01-问题诊断.md) — 理解"停+留+截"三段语义和 15 种职责
- [ ] 阅读 [02-目标架构.md](02-目标架构.md) — 理解 11 个新组件拆解 + ChatTurnContext 一等公民
- [ ] 阅读 [03-阶段路线.md](03-阶段路线.md) — 理解 5 阶段 4 周路线和每阶段验收要求
- [ ] 阅读 [04-链路修复矩阵.md](04-链路修复矩阵.md) §7.0 + §7.1 + §7.3 — 13 条链路 + 公共契约保留清单
- [ ] 阅读 [05-兼容性与决策.md §十一 核心论点](05-兼容性与决策.md) — 对齐"架构级修复 vs 修补丁"判断标准
- [ ] 浏览 [对话引擎核心链路架构图.md](对话引擎核心链路架构图.md) §三 全景图 + §五 关键序列图 — 视觉化对照目标态
- [ ] 阅读本文档 §实施纪律 — 理解 4 条硬约束的具体含义和违反后果

### 组 2：基准快照（实施负责人）

🚦 **完整流程见 [阶段0-实施前对齐.md](阶段0-实施前对齐.md)**。摘要：

- [ ] 完成 [阶段0-实施前对齐.md §二](阶段0-实施前对齐.md) 公共契约签名基准快照（6 块：IAiChatEngine / AiChatHostedService 方法 + 访问器 / IAiOutputChannel / IRealtimeProcessOutput / Events.cs / ChatHistoryDataProvider）
- [ ] 完成 [阶段0-实施前对齐.md §三](阶段0-实施前对齐.md) 外部调用方依赖快照（11 处方法 + 12 处访问器 + 4 IAiOutputChannel 实现 / 3 DI 注册）
- [ ] 完成 [阶段0-实施前对齐.md §四](阶段0-实施前对齐.md) 13 条核心链路 + 4 个边界 case 当前行为录像（17 段视频）
- [ ] 完成 [阶段0-实施前对齐.md §五](阶段0-实施前对齐.md) 4 条实施纪律团队签字

**阶段 0 不通过 = 不能进入阶段 1**。这是**合同**，阶段 5 验收靠的是阶段 0 的基准。

---

## 实施纪律（4 条硬约束 — 任何 PR 违反都不接受）

这 4 条不是 nice-to-have 的指导原则，是**实施期间任何 PR 评审的一票否决项**。违反任意一条 = PR 必须打回重做。

### 纪律 1：公共契约只转发不删除

- `IAiChatEngine` 接口的 5 个公共方法（`SendMessageAsync` / `Stop` / `CancelCurrentTask` / `CancelCurrentTaskAsync` / `NewSessionAsync` / `ResumeSessionAsync`）**签名一字不动**
- `AiChatHostedService` 的 5 个公共访问器（`CurrentSessionId` / `CurrentWorkspaceId` / `CurrentAgent` / `CurrentProvider` / `CurrentModel`）+ 3 个 `Change*` 方法**签名一字不动**
- 阶段 5 把字段迁到独立组件后，**通过 `=> _agentResolver.CurrentX` 这种箭头转发暴露**，不能删除访问器签名
- 验证方式：阶段 0 截图签名快照 vs 阶段 5 完成后 diff = 0
- 违反后果：`WebSocketInputChannel:69` / Voice 4 处调用方编译失败 → 破坏现网

### 纪律 2：EventHub 事件结构不动

- `Events.cs` 文件**整个不动**（不新增事件类型、不改 Args record 字段）
- 5s 超时上报复用 `OnConversationTurnCompleted(Status=Failed, ErrorMessage="zombie killed: ...")`，**不新增 OnConversationTurnZombieKilled**
- 阶段 4 VM 改事件源仍用现有 `OnConversationTurnStarted/Completed`，方案 A（复用 Status 枚举）
- 验证方式：`git diff Src/Netor.Cortana.Entitys/Events.cs` 阶段 0 vs 阶段 5 = 空
- 违反后果：跨进程订阅方（PluginBus dispatcher、Memory 插件、外部 WebSocket 客户端）失联

### 纪律 3：AF 历史钩子不重写

- `ChatHistoryDataProvider.ProvideChatHistoryAsync` / `StoreChatHistoryAsync` / `SavePartialResponseAsync` / `AppendWorkflowFinalReport` **完整保留**
- 新增 `ChatMessagePersistence` **只是 turn 取消路径的薄包装**（内部转调 `chatHistoryProvider.SavePartialResponseAsync`），不重新实现持久化逻辑
- 不新增任何形式的"DB → AgentSession 回灌"服务（v6 误判作废）
- 验证方式：`ChatHistoryDataProvider.cs` 公共方法签名快照阶段 0 vs 阶段 5 = 一致
- 违反后果：① 压缩链路（CompactionSegments + 老版 CompactedContext 兼容）失效 ② Workflow→Chat 回灌（[ChatHistoryDataProvider.cs:283-374](../../../Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs#L283-L374) AppendWorkflowFinalReport）链路断裂 ③ Memory 插件去重失败

### 纪律 4：IRealtimeProcessOutput 单独纳入接口改造清单（不与 IAiOutputChannel 同等级处理）

- `IRealtimeProcessOutput` 是**独立接口**，不是 `IAiOutputChannel` 的子集
- 接口签名**一字不动**（`OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct)`），实现端按 `evt.TurnId` 字段做路由
- 调用方 [PowerShellProvider.cs:309](../../../Src/Netor.Cortana.Plugin/BuiltIn/PowerShell/PowerShellProvider.cs#L309) 等插件层零改动 — 它们已经在通过 `new RealtimeProcessEvent { TurnId = ..., ... }` 传 turnId
- 误把它当作"`IAiOutputChannel` 同等级的接口破坏性变更"会强制所有插件改调用方代码（破坏面非零）
- 验证方式：阶段 1 PR 改 `IRealtimeProcessOutput` 接口签名 = PR 拒绝；只有实现端按字段路由是允许的
- 违反后果：插件运行时崩溃 / 编译失败 / 插件作者抱怨破坏性变更

### 这 4 条的判断逻辑

每一条都对应一个"实施时容易踩的坑"，且都有具体的反例：

| 纪律 | 反例（容易踩的坑） | 后果 |
| --- | --- | --- |
| 1 | "字段迁到 ChatAgentResolver 后顺手把公共访问器也删了" | Voice / Networks 编译失败 |
| 2 | "5s 超时事件加个新 EventType 更明确" | 跨进程订阅方接不到 |
| 3 | "ChatMessagePersistence 接管所有 ChatMessages 写入" | Workflow→Chat 回灌断 |
| 4 | "IRealtimeProcessOutput 跟 IAiOutputChannel 一起改" | PowerShell 插件等所有调用方改代码 |

**实施 PR 模板必须显式确认这 4 条**（每个 PR 描述末尾加一段）：

```markdown
## 实施纪律对齐确认（README §实施纪律）

- [ ] 纪律 1：本 PR **不删除** IAiChatEngine / AiChatHostedService 公共契约签名
- [ ] 纪律 2：本 PR **不修改** Events.cs（新增/改字段都不允许）
- [ ] 纪律 3：本 PR **不重写** ChatHistoryDataProvider 已有公共方法
- [ ] 纪律 4：本 PR **不改** IRealtimeProcessOutput 接口签名（仅修改实现端的 evt.TurnId 路由）
```

---

## 文档维护契约

### 实施期间

- 本文档集与代码协同演进；任何文档与代码不一致，**修文档而不是修代码**（架构图是合同）
- 实施过程中如发现新的破坏面或链路，**必须先更新 [04-链路修复矩阵.md](04-链路修复矩阵.md) §7.1 矩阵**，再实施
- 每个阶段完成时，在对应阶段的"验收"栏标注实际验收结果（绿色 ✅ / 黄色 ⚠️ / 红色 ❌ + 说明）

### 实施完成迁移契约

**触发条件**（5 个阶段全部完成、必须全部满足）：

- [ ] 阶段 1 验收：取消后不再"必须重启"；旧 turn HTTP 慢断 3 秒期间新 turn 气泡不被污染；4 个 IAiOutputChannel 实现单元测试通过；同一 turnId 的 OnConversationTurnCompleted 只发一次
- [ ] 阶段 2 验收：取消后无论是否换 Provider/Model/Agent 重发，DB 都有部分回复行；新一轮 AI 引用了刚才被打断那段；切换历史会话仍正常；工作模式 12 处 `_chatService.CurrentXxx` 调用零变化；会议模式启动/切换正常
- [ ] 阶段 3 验收：`AiChatHostedService.cs` ≤ 200 行；v2 全部 7 个症状（8.2.1-8.2.7）消失；旧 turn HTTP 慢断 3 秒期间新 turn 完全不被污染；每个 Executor / Service / Loader 有独立单元测试
- [ ] 阶段 4 验收：多次点击发送不再堆积孤儿 task；UI 状态与服务真实状态严格同步
- [ ] 阶段 5 验收：**公共契约签名快照对比 diff = 0**（阶段 0 截图 vs 阶段 5 完成）；遗留**内部**死代码清零；WebSocketInputChannel / Voice 4 处 `chatEngine.Stop()` / `CancelCurrentTask()` 仍正常工作
- [ ] **[04-链路修复矩阵.md §7.4](04-链路修复矩阵.md) 13 条链路验收清单全部通过**（这是迁移的硬门槛）
- [ ] 全 dotnet build 通过 0 警告 0 错误；全 dotnet test 通过；运行验证视频/截图归档

**迁移操作**（按顺序，单次执行）：

```bash
# 1. 整体移动目录（不重命名）
git mv "Docs/已完成功能规划/对话取消重发状态机修复" \
       "Docs/已完成功能规划/对话取消重发状态机修复"

# 2. 提交移动（让 git rename detection 工作）
git commit -m "docs: 对话取消重发状态机修复方案迁移到已完成功能规划"

# 3. 更新 README.md 顶部状态（按下方模板，下一步骤）
# 4. 更新 5 篇子文档顶部状态行（统一改为"已落地"）
# 5. 更新 对话引擎核心链路架构图.md 顶部状态行（"v1（目标态设计）" → "已落地架构事实"）
```

**迁移后 README.md 顶部状态模板**（参照 [已完成功能规划/工作模式三层架构重构/README.md](../../已完成功能规划/工作模式三层架构重构/README.md) 风格）：

```markdown
# 对话取消重发问题 — 架构债拆解方案（文档集）

> 当前状态：**已完成 / 已落地**
> 实施周期：YYYY-MM-DD ~ YYYY-MM-DD（5 阶段，实际 N 周）
> 最后验证时间：YYYY-MM-DD

本目录收纳"对话模式取消重发问题（架构债拆解）"相关的全套方案与执行结果。
方案于 v7.3 拍板（详见 §〇 版本演进表），分 5 阶段实施完成。

## 验证状态

​```text
dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false
  通过：0 警告，0 错误

dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj
  通过：N/N（含新增 ChatTurnExecutor / ChatAgentResolver / ChatTurnContext 单元测试）

13 条核心链路验收（详见 04-链路修复矩阵.md §7.4）：
  全部通过 ✅
​```

（其它章节保持不变 — 历史档案价值在于 v1 → v7.3 的演进推理仍可见）
```

**迁移后子文档顶部状态行模板**（5 篇统一）：

```markdown
> 子文档：[对话取消重发问题 — 架构债拆解方案（已完成）](README.md) 系列之 0X
> 状态：**已落地**（YYYY-MM-DD 实施完成）
> 范围：...（不变）
> 上一篇 / 下一篇 / 配套结构档 链接（不变）
```

**迁移后归档目录结构**：

```text
Docs/已完成功能规划/对话取消重发状态机修复/
├── README.md                       ← 状态改为"已完成 / 已落地"，附实际实施周期 + 验证状态
├── 01-问题诊断.md                  ← 头部状态行改为"已落地"
├── 02-目标架构.md                  ← 同
├── 03-阶段路线.md                  ← 同；每个阶段验收栏附实际结果
├── 04-链路修复矩阵.md              ← 同；§7.4 13 条链路验收清单全部标注 ✅
├── 05-兼容性与决策.md              ← 同
└── 对话引擎核心链路架构图.md       ← 状态改为"已落地架构事实"；不再标"目标态设计"
```

**迁移后的文档定位转变**：

| 维度 | 实施期间（当前） | 迁移后 |
| --- | --- | --- |
| 文件夹 | `未来版本策划/` | `已完成功能规划/` |
| 整体定位 | 待实施的方案 | 已落地的方案档（历史可追溯） |
| README 状态 | "v7.3 终稿（已拍板）/ 待实施" | "已完成 / 已落地" |
| 架构图状态 | "v1（目标态设计）" | "已落地架构事实" |
| 迭代意义 | 接受评审、调整方案 | **不再修改方案内容**；只在新需求出现时引用作为历史背景 |
| §〇 演进表 | 记录 v1 → v7.3 决策路径 | **保留不动**（演进表是这次架构改造最有价值的资产，下次类似改造可以参考） |

**迁移后的文档使命**：

1. **新人 onboarding**：阅读 §〇 版本演进 + §〇之二 决策表，5 分钟理解"为什么 ChatTurnExecutor / ChatAgentResolver / ChatSessionService 是这个样子"
2. **回归测试覆盖参考**：[04-链路修复矩阵.md §7.4](04-链路修复矩阵.md) 13 条链路验收清单可作为长期回归测试的事实基础
3. **未来类似改造的范本**：本次"职责拆分 + turn 一等公民 + 接口契约破坏性变更"是工作模式 / 会议模式后续可能借鉴的模式
4. **架构债认知教训**：核心论点 "接口面思维 → 链路语义思维" 的转变值得后续架构方案学习

### 不要做什么

- ❌ **不要在 `已完成功能规划/` 里再修改方案内容** — 那是已确定的历史档案。如果实施后发现问题，开新方案（在 `未来版本策划/` 下新建文件夹），而不是改这套
- ❌ **不要删除任何文档** — 即使某些章节看起来重复，它们记录了不同视角下的对照（例如 04 的链路矩阵 vs 架构图的序列图）
- ❌ **不要改版本号** — v7.3 是终稿，迁移后状态改"已完成"但版本号不变

---

## 历史背景

本方案最早针对的用户报告："AI 流式输出时点击停止 → 必须重启软件才能继续对话；偶尔取消后重发会丢已生成的部分回复"。从 v1（"修死循环"）到 v7.3（"架构级链路修复"）经历了 7 次迭代。每次迭代的反思见 [§〇 版本演进](#〇版本演进) 表。

核心教训：**接口面思维 → 链路语义思维**。架构改造的破坏面不仅是接口签名是否兼容，更是事件发布点位置、字段所有权、版本检查内聚度等链路语义层的稳定性。v7.3 引入"13 条核心链路"作为系统视角，让破坏面分析从模糊判断升级为可逐条验证的清单。
