# Token 进度条跳变修复 : 100%

## Step 1 隔离压缩调用的 Usage 上报（问题 1）: 100%
- [√] 在 `TokenTrackingChatClient` 增加 `SuppressUsage()` 作用域（IDisposable），期间 `RecordUsage` 不写 token 字段也不回调 observer
- [√] 在 `ChatHistoryDataProvider.CompactAndReplaceAsync` 中用 `using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();` 包裹摘要调用
- [√] `GenerateAndUpdateTitleAsync` 同样抑制（fire-and-forget 标题生成亦可能污染主进度条）

## Step 2 模型/Provider/Agent 切换时重置（问题 2）: 100%
- [√] `AiChatHostedService.RebuildAgent()` 末尾调用 `factory.ResetTokenStats()`

## Step 3 新会话 / 恢复会话时重置（问题 3）: 100%
- [√] `NewSessionAsync` 末尾调用 `factory.ResetTokenStats()`
- [√] `ResumeSessionAsync` 末尾调用 `factory.ResetTokenStats()`

## Step 4 流式 Usage 抖动（问题 4）: 100%
- [√] `TokenTrackingChatClient.GetStreamingResponseAsync` 流内累积 usage（Input 取最大、Output 累加），流结束统一合并上报一次
- [√] `GetResponseAsync` 保持一次到位

## Step 5 TotalTokenCount 累加修正（问题 5）: 100%
- [√] `AddOrUpdateSessionAsync` 增加 `accumulateInputTokens` 参数，仅在 `StoreChatHistoryAsync` 路径下传 `true`
- [√] 新会话创建 / 取消对话等调用路径不再重复累加

## Step 6 编译验证 : 100%
- [√] `dotnet build Netor.Cortana.slnx` 成功（70 条历史警告，0 error）

## 修改文件清单
1. `Src/Netor.Cortana.AI/Providers/TokenTrackingChatClient.cs`
2. `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs`
3. `Src/Netor.Cortana.AI/AiChatHostedService.cs`
