# GIT-修改-动态子智能体创建审批与保存

## 修改范围

- `Src/Netor.Cortana.AI/`
- `Src/Netor.Cortana.Entitys/`
- `Src/Netor.Cortana.UI/`
- `Docs/未来版本策划/聊天式任务发起与动态智能体/`
- `Docs/GIT提交说明/`

## 修改内容

### 1. 工作流动态子智能体创建链路

- 新增任务级动态子智能体注册表，支持 Manager 在 Magentic 工作流执行期间创建临时子智能体。
- 新增 `create_subagent` 工具，包含名称合法性、同任务去重、数量上限和工具白名单校验。
- 新增动态子智能体工具提供器，将已创建的子智能体包装为 `dynamic_agent_{name}` 工具供 Manager 调用。
- 调整工作流参与者构建和执行链路，让工作流输入框选择的 Provider / Model 可覆盖 Manager 默认配置，并透传给动态子智能体。
- 修复 Magentic 单 Manager 场景下的 self-talk fallback，避免没有预设成员时工作流无法启动。
- 修复工作流最终回复提取优先级，允许 SDK `WorkflowOutputEvent` 中的最终答案覆盖空的中间回复。

### 2. Manager 动态创建提示词模板

- 新增 `Resources/Prompts/Magentic.DynamicCreation.md`，指导 Manager 判断何时创建子智能体、如何命名、如何拆分职责和处理失败。
- 新增提示词注入 Provider，在 Manager 上下文中动态替换 `{{MaxSubAgents}}` 并注入动态创建说明。
- 调整 `Netor.Cortana.AI.csproj`，将提示词模板作为嵌入资源发布。

### 3. 动态子智能体创建审批

- 新增 `OnDynamicAgentCreationRequested` / `OnDynamicAgentCreationResolved` 事件和对应参数模型。
- 新增 `DynamicAgentCreationGate`，以任务级 `TaskCompletionSource` 管理审批等待、单次批准、全部批准和拒绝。
- `create_subagent` 在默认开启审批时会先发布审批请求，等待 UI 决策后再注册动态子智能体。
- 任务结束时清理动态注册表和审批闸，避免 pending 等待和 auto-approve 状态泄漏。

### 4. UI 审批卡片与保存常用 Agent

- 在工作流详情页新增动态子智能体创建审批卡片，展示名称、职责、工具、提示词预览和当前数量上限。
- 支持“批准创建”“本任务全部批准”“拒绝创建”三种操作。
- 新增任务完成后的保存常用 Agent 对话框，可勾选临时子智能体、改名并保存为永久 Agent。
- 保存前校验空名称、对话框内重复名称和已有 Agent 重名，冲突时在行内提示。

### 5. 工作流输入与任务展示体验修复

- 工作流 / 群聊发送前显式同步输入框文本，规避 NativeAOT / CompiledBinding 下发送时读到空输入的问题。
- 发送任务后立即切换到新任务详情，列表项尚未插入时缓存待选任务 ID。
- 将 `titlebar-btn` 系列样式迁移到全局共享样式，修复工作流 / 群聊发送按钮 hover 状态丢失。

## 影响说明

- 本次改动使 Magentic 工作流从“预设成员协作”推进到“Manager 运行时动态创建临时子智能体”的核心形态。
- 动态子智能体默认任务级生命周期，任务结束后销毁；用户可在完成后选择保存为永久 Agent。
- 当前 Debug build 已用于验证编译正确性；AOT publish 仍受本机 NativeAOT 工具链环境影响，需在可用发布环境继续验证。
- `.claude/settings.local.json` 是本地助手权限配置，不纳入本次提交。
