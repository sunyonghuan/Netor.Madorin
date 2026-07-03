# 编排观察面板 TurnId 展示执行计划 : 100%

> 关联文档：[19-编排观察面板过滤摘要执行计划.md](19-编排观察面板过滤摘要执行计划.md)
> 目标：为专家模式开发态编排观察面板中的 turn 卡片补充 `TurnId` 短显示与完整悬浮提示，方便将 UI 诊断结果与日志中的 turn 记录快速对照。

## Step 1 TurnId 展示边界设计 : 100%
- [√] 已确认 TurnId 展示仅作用于最近 turn 卡片，不影响顶部摘要与复制逻辑
- [√] 已确认 ViewModel 负责输出短显示文案与完整提示文案
- [√] 已选定最小形态，采用一行次级文字显示短 TurnId，并通过 tooltip 暴露完整值

## Step 2 ViewModel TurnId 文案输出 : 100%
- [√] 已为 `ChatTurnDiagnosticsItemVm` 增加短 TurnId 与完整提示字段
- [√] 已保持单条复制、整段复制与现有摘要文案兼容
- [√] 已确保短 TurnId 在不同长度下都稳定可读

## Step 3 观察卡片 TurnId 接线 : 100%
- [√] 已在 `ChatView.axaml` 的 turn 卡片中增加 TurnId 展示区域
- [√] 已为展示区域接入完整 TurnId tooltip
- [√] 已保持卡片层级、复制按钮与 warning 文案布局稳定

## Step 4 验证与回填 : 100%
- [√] 运行解决方案 build
- [√] 运行定向测试
- [√] 回填本文档进度、修改文件与验证结果

## 已修改文件
- [√] `Src/Netor.Cortana.UI/ViewModels/ExpertMode/ChatInputVm.cs`
- [√] `Src/Netor.Cortana.UI/Controls/ExpertMode/ChatView.axaml`
- [√] `Docs/已完成功能规划/对话取消重发状态机修复/20-编排观察面板TurnId展示执行计划.md`

## 验证结果
- [√] `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false` 已通过，0 warning / 0 error
- [√] `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter "FullyQualifiedName~ChatConversationEventPublisherTests|FullyQualifiedName~AgentOrchestratorTests|FullyQualifiedName~ChatAgentResolverTests|FullyQualifiedName~AiChatHostedServiceTests|FullyQualifiedName~ChatSessionServiceTests|FullyQualifiedName~ChatTurnCancellationRegistryTests|FullyQualifiedName~ChatGenerationContextBuilderTests|FullyQualifiedName~ChatGenerationTurnCoordinatorTests"` 已通过，32/32 通过
