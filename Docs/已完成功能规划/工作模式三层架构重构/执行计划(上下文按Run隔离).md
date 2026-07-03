# 执行计划：上下文按 Run 隔离 : 100%

> 状态：已完成
> 日期：2026-06-08
> 来源：[工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md) §六、§九 阶段 2、§十 D5

---

## Step 1 文档与现状对齐 : 100%

- [√] 拆出阶段 2 独立执行计划文档
- [√] 确认当前代码没有 `WorkTaskContextService` / `WorkTaskContextMessages` / `WorkTaskContextSegments`
- [√] 确认当前 A 层历史仍由 `WorkflowExecutor` 内存字典按 `taskId` 保存
- [√] 确认 C 层已经以单次消息注入 `environment + step.input`

## Step 2 Run 级上下文持久化地基 : 100%

- [√] 新增 `WorkTaskContextMessages` 表，字段包含 `TaskId` / `RunId` / `Sequence` / `Role` / `Content`
- [√] 新增 `WorkTaskContextSegments` 表，为后续 Run 级压缩预留
- [√] 新增实体与 `WorkTaskContextService`
- [√] 注册服务与索引

## Step 3 A 层历史按 RunId 隔离 : 100%

- [√] `WorkflowExecutor` 首轮创建 `RunId` 后以 `RunId` 作为上下文键
- [√] 用户/助手消息追加到 `WorkTaskContextMessages`
- [√] 继续任务时按 `RunId` 恢复历史
- [√] 取消/失败时清理内存缓存，不删除持久化历史

## Step 4 C 层单步短上下文验证 : 100%

- [√] 确认 `ProjectStepDispatcher` 每次只发送当前 step input
- [√] 补测试覆盖 environment 注入，不携带其他 step 历史

## Step 5 验证与文档对齐 : 100%

- [√] 补充上下文服务单元测试
- [√] 补充 WorkflowExecutor/ProjectStepDispatcher 相关测试
- [√] 构建通过
- [√] AI 测试通过
- [√] 更新父方案完成状态

---

## 执行记录

### 2026-06-08

- 已确认父方案中阶段 2 的目标是“按 Run 压缩/隔离”，B 层不进入上下文表。
- 当前仓库尚无 `WorkTaskContextService`，阶段 2 需要先补持久化地基。
- 已新增 `WorkTaskContextMessages` / `WorkTaskContextSegments` 两张表与索引。
- 已新增 `WorkTaskContextMessageEntity` / `WorkTaskContextSegmentEntity` / `WorkTaskContextService`。
- 已将 `WorkflowExecutor` 的 A 层历史从 `taskId` 内存缓存改为 `RunId` 级缓存，并同步持久化 system/user/assistant 消息。
- 已确认 C 层 `ProjectStepDispatcher` 每次只构造当前 step 的短上下文，并自动注入 environment。
- 验证：
  - `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false` 通过，0 警告 0 错误
  - `dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false` 通过，25/25（存在已知 MSTEST0001 提示）

## 修改文件

- `Docs/已完成功能规划/工作模式三层架构重构/执行计划(上下文按Run隔离).md`
- `Src/Netor.Cortana.Entitys/CortanaDbContext.cs`
- `Src/Netor.Cortana.Entitys/Entities/WorkTaskContextMessageEntity.cs`
- `Src/Netor.Cortana.Entitys/Entities/WorkTaskContextSegmentEntity.cs`
- `Src/Netor.Cortana.Entitys/Services/WorkTaskContextService.cs`
- `Src/Netor.Cortana.AI/WorkMode/WorkflowExecutor.cs`
- `Src/Netor.Cortana.UI/App.axaml.cs`
- `Tests/Netor.Cortana.AI.Tests/WorkModeFiles/WorkTaskContextServiceTests.cs`
- `Tests/Netor.Cortana.AI.Tests/WorkModeFiles/ProjectStepDispatcherPromptTests.cs`
