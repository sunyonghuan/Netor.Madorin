# Voice KWS/STT Pipeline Fix Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** 修复语音唤醒关闭后仍会唤醒，以及唤醒后 STT 不产生 partial/final、桌宠/悬浮字幕/UI 对话链路收不到识别文字的问题。

**Architecture:** 以宿主 `VoicePipelineCoordinator` 作为唯一语音流程编排者，负责 KWS → Greeting/TTS → STT → AI/TTS → KWS 的状态切换。插件只暴露工具和发布自身结果事件，不再在插件内部对 KWS 事件自动启动 STT，避免双编排竞争。设置保存后需要能热应用 KWS/STT/TTS 开关，至少关闭 KWS 时立即停止监听。

**Tech Stack:** .NET / C#, Avalonia UI, EventHub, PluginBus WebSocket, Sherpa-ONNX voice plugins, MSTest.

---

## Current Findings

1. UI 中旧的 `Voice.WakeWordEnabled` 不是当前插件 KWS 的实际运行开关。
   - 旧内置服务读取：`Src/Netor.Cortana.Voice/WakeWordService.cs:41`
   - 插件 KWS 实际读取：`Src/Netor.Cortana.Plugin/Voice/VoiceCapabilityRegistry.cs:66`
   - 设置种子仍显示旧开关：`Src/Netor.Cortana.UI/App.axaml.cs:478`

2. `VoicePipelineCoordinator.StartAsync()` 启动后直接进入 `StartListeningAsync()`，但运行中关闭设置不会热停止已启动的 KWS。
   - 启动订阅和启动 KWS：`Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs:48`
   - `StartListeningAsync()` 调用 KWS：`Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs:111`

3. STT 插件内部和宿主同时编排同一个 KWS 事件。
   - STT 插件订阅 KWS：`Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Startup.cs:17`
   - STT 插件收到 KWS 后启动 STT：`Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/PluginBus/SttVoiceCoordinator.cs:70`
   - 宿主收到桥接后的 `OnWakeWordDetected` 后也启动新一轮：`Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs:52`
   - `SttEngine.Start()` 会先停止旧线程：`Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Engine/SttEngine.cs:40`

4. UI/桌宠/悬浮字幕事件订阅链是存在的，问题更可能在 STT 没有产生 partial/final。
   - 插件 STT 发布事件：`Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Engine/SttEngine.cs:242`
   - 宿主 bridge 到 EventHub：`Src/Netor.Cortana.Networks/WebSockets/Voice/VoiceEventBridge.cs:52`
   - Bubble 订阅：`Src/Netor.Cortana.UI/Views/BubbleWindow.axaml.cs:75`
   - 桌宠订阅 relay：`Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketEventRelayService.cs:39`
   - 桌宠事件映射：`Plugins/Src/DesktopPet/src/DesktopPet.Ai/PetRealtimeEventMapper.cs:19`

---

## Implementation Tasks

### Task 1: Add Pipeline State Safety Tests

**Objective:** 用单元测试锁定“关闭 KWS 时不应启动监听”和“STT final/stopped 事件应保持桥接”的基础行为。

**Files:**
- Modify/Create tests under `Tests/Netor.Cortana.Voice.Tests/` if the project exists.
- If no voice test project exists, create `Tests/Netor.Cortana.Voice.Tests/Netor.Cortana.Voice.Tests.csproj`.
- Reference production project: `Src/Netor.Cortana.Voice/Netor.Cortana.Voice.csproj`.

**Steps:**
1. Inspect existing test projects with `rg --files Tests | grep '\.csproj$'`.
2. If there is no voice test project, create one using existing MSTest project style from `Tests/Netor.Cortana.Networks.Tests/`.
3. Add test doubles for `KwsPluginAdapter`, `SttPluginAdapter`, `TtsPluginAdapter` only if refactoring makes them interface-backed in later tasks.
4. If adapters are not mockable yet, defer coordinator unit test until Task 2 introduces interfaces.
5. Run targeted test project after Task 2.

**Validation:**
- `dotnet test Tests/Netor.Cortana.Networks.Tests/Netor.Cortana.Networks.Tests.csproj --filter VoiceEventBridgeTests`
- Expected: existing bridge tests still pass.

---

### Task 2: Introduce Voice Adapter Interfaces

**Objective:** 让 `VoicePipelineCoordinator` 可测试，同时保持现有 DI 行为不变。

**Files:**
- Modify: `Src/Netor.Cortana.Voice/KwsPluginAdapter.cs`
- Modify: `Src/Netor.Cortana.Voice/SttPluginAdapter.cs`
- Modify: `Src/Netor.Cortana.Voice/TtsPluginAdapter.cs`
- Modify: `Src/Netor.Cortana.Voice/VoiceServiceExtensions.cs`
- Modify: `Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs`

**Steps:**
1. Add `IKwsPluginAdapter` with `ConfigureAsync`, `StartAsync`, `StopAsync`.
2. Add `ISttPluginAdapter` with `ConfigureAsync`, `StartAsync`, `StopAsync`.
3. Add `ITtsPluginAdapter` with `ConfigureAsync`, `RegenerateGreetingAsync`, `GreetingPlayAsync`, `StopAsync` and existing enqueue/finish methods if used elsewhere.
4. Make current adapter classes implement those interfaces.
5. Register interfaces in DI while keeping concrete classes registered for existing call sites.
6. Change `VoicePipelineCoordinator` constructor to depend on interfaces.

**Validation:**
- `dotnet build Netor.Cortana.slnx`
- Expected: no compile errors from DI or call sites.

---

### Task 3: Make Pipeline Respect KWS Enabled at Runtime

**Objective:** 关闭 KWS 后，宿主立刻停止 KWS 并忽略后续唤醒事件。

**Files:**
- Modify: `Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs`
- Possibly modify: `Src/Netor.Cortana.UI/Views/Settings/SystemSettingsPage.axaml.cs`

**Steps:**
1. Inject `SystemSettingsService` into `VoicePipelineCoordinator`.
2. Add helper `IsKwsEnabled()` returning `settings.GetValue("Voice.Kws.Enabled", false)`.
3. In `StartListeningAsync()`, if KWS disabled, stop KWS defensively and keep `_state = State.Idle` or a new disabled state.
4. In `HandleWakeWordAsync()`, return immediately if KWS disabled.
5. Add public method `ApplySettingsAsync(CancellationToken)` that:
   - Stops KWS/STT when `Voice.Kws.Enabled` is false.
   - Starts KWS only when enabled and state is idle/listening-safe.
   - Reconfigures KWS/STT adapters when enabled settings changed.
6. Avoid changing TTS behavior in this task except where needed to cancel active greeting.

**Validation:**
- Add/Run unit tests proving `StartAsync()` does not call `KWS.Start` when disabled.
- Add/Run unit tests proving `ApplySettingsAsync()` calls `KWS.Stop` when disabled.

---

### Task 4: Apply Voice Settings on Save

**Objective:** 系统设置保存后，KWS/STT/TTS 插件开关和插件选择立即生效，尤其关闭 KWS 立即停止唤醒。

**Files:**
- Modify: `Src/Netor.Cortana.UI/Views/Settings/SystemSettingsPage.axaml.cs`
- Possibly modify: `Src/Netor.Cortana.UI/App.axaml.cs`

**Steps:**
1. In `OnSaveClick`, detect whether any of these keys changed:
   - `Voice.Kws.Enabled`
   - `Voice.Kws.PluginId`
   - `Voice.Stt.Enabled`
   - `Voice.Stt.PluginId`
   - `Voice.Tts.Enabled`
   - `Voice.Tts.PluginId`
2. After `SettingsService.SaveBatch(updates)`, resolve `VoicePipelineCoordinator` and call `ApplySettingsAsync(CancellationToken.None)`.
3. Keep existing `ApplyTtsPluginSettingsAsync` logic but avoid duplicate TTS configure if `ApplySettingsAsync` centralizes this.
4. Update success toast to mention “语音设置已实时应用” when relevant.
5. Do not require application restart for enable/disable toggles; only model path/plugin process changes may still require restart if plugin loader cannot unload/reload.

**Validation:**
- Manual: enable KWS, verify wake works; disable KWS, verify no wake without app restart.
- Logs should show KWS stopped when disabled.

---

### Task 5: Remove STT Plugin Internal KWS Auto-Start

**Objective:** 消除 STT 插件和宿主对 KWS 事件的双编排竞争。

**Files:**
- Modify: `Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Startup.cs`
- Modify/Delete: `Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/PluginBus/SttVoiceCoordinator.cs`

**Steps:**
1. Remove `[SubscribesEvent("voice.kws.detected.v1", ...)]` from STT plugin startup.
2. Remove `services.AddHostedService<SttVoiceCoordinator>()` from STT plugin startup, or change coordinator so it only handles TTS suppression if truly needed.
3. Prefer deleting STT coordinator entirely if host owns STT start/stop and TTS coordination.
4. If keeping coordinator for TTS suppression, remove only KWS subscription and `KwsDetectedOp` branch.
5. Confirm STT still starts via host tool call `voice_stt_start` from `VoicePipelineCoordinator`.

**Validation:**
- Build plugin solution: `dotnet build Plugins/Cortana.Plugins.slnx`
- Search check: `rg "voice.kws.detected.v1|KwsDetectedOp|SttVoiceCoordinator" Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa`
- Expected: no STT auto-start subscription remains, or only documented non-start usage remains.

---

### Task 6: Review KWS Plugin TTS Suppression Overlap

**Objective:** 确认 KWS 插件内部 TTS suppression 不会和宿主 `VoicePipelineCoordinator` 冲突。

**Files:**
- Inspect/possibly modify: `Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/PluginBus/KwsVoiceCoordinator.cs`
- Inspect: `Src/Netor.Cortana.Voice/VoicePipelineCoordinator.cs`

**Steps:**
1. Decide whether host or KWS plugin owns TTS suppression.
2. If host owns all KWS stop/start, remove KWS plugin `KwsVoiceCoordinator` hosted service.
3. If plugin suppression stays, document why it is safe with host barge-in behavior.
4. Ensure AI/TTS speaking phase still supports barge-in if desired.

**Validation:**
- Manual: during TTS output, say wake word; expected behavior should match product decision:
  - barge-in enabled: KWS should remain listening during AI/TTS output.
  - barge-in disabled: KWS should be stopped during TTS.

---

### Task 7: Add Diagnostic Logs Around STT Events

**Objective:** 让现场能区分“STT 没启动”、“STT 启动但无音频”、“STT 识别到但事件没桥接”。

**Files:**
- Modify: `Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Engine/SttEngine.cs`
- Modify: `Src/Netor.Cortana.Networks/WebSockets/Voice/VoiceEventBridge.cs`
- Modify: `Src/Netor.Cortana.UI/Views/BubbleWindow.axaml.cs` only if needed for debug-level UI logs.

**Steps:**
1. Log STT start with session id and model directory already exists; keep it.
2. Add debug/info log when first audio buffer is received.
3. Add debug log when first non-empty partial is published.
4. In `VoiceEventBridge`, log debug when bridging STT partial/final with text length, not full text unless debug trace is enabled.
5. Avoid noisy logs in tight audio loop.

**Validation:**
- Manual run should produce sequence:
  - KWS detected
  - host starts STT
  - STT recorder started
  - STT partial published or STT stopped with reason
  - bridge receives partial/final
  - Bubble updates subtitle

---

### Task 8: Verify End-to-End Voice Flow

**Objective:** 用真实运行验证 KWS 开关、STT 字幕、桌宠事件和 AI 对话都正常。

**Files:**
- No code changes expected.

**Steps:**
1. Build host: `dotnet build Netor.Cortana.slnx`.
2. Build plugins: `dotnet build Plugins/Cortana.Plugins.slnx`.
3. Launch UI in dev mode using the repo’s existing run command.
4. Enable `Voice.Kws.Enabled` and `Voice.Stt.Enabled`; select Sherpa plugins if required.
5. Say wake word.
6. Confirm Bubble shows “正在聆听...” then updates to recognized text via partial/final.
7. Confirm DesktopPet receives `wakeword_detected`, then `stt_partial` / `stt_final`.
8. Confirm `VoiceInputChannel` sends final text into AI conversation and UI shows user message bubble.
9. Disable KWS in settings without restart.
10. Say wake word again; confirm no wake event, no Bubble, no DesktopPet wake animation.

**Validation:**
- Build succeeds.
- Manual test confirms no wake when disabled.
- Manual test confirms STT partial/final reaches Bubble and DesktopPet.

---

## Risks and Tradeoffs

1. Removing STT plugin internal KWS subscription changes plugin autonomy. This is intentional if host is the orchestrator, but should be documented in plugin instructions.
2. KWS during TTS is a product decision. Current host comments say barge-in should work, but KWS plugin’s own TTS suppression may disable that. Resolve ownership explicitly.
3. Hot applying plugin selection may be limited by plugin loader lifecycle. If selected plugin changes require reload, show a precise restart-required message only for that case.
4. Tests may require introducing small interfaces because current adapters are concrete classes and hard to mock.
5. Audio behavior still needs real hardware validation; unit tests can only prove state/dispatch logic.

---

## Acceptance Criteria

- Closing the UI voice/KWS switch stops wake detection without restarting the app.
- STT is started by exactly one owner after wake: `VoicePipelineCoordinator`.
- Wake after greeting enters STT and can publish `OnSttPartial` / `OnSttFinal`.
- Bubble subtitle updates with recognized text, not only “正在聆听...”.
- DesktopPet receives `stt_partial` / `stt_final` through WebSocket relay.
- `VoiceInputChannel` receives final text and sends it into AI conversation.
- Existing `VoiceEventBridgeTests` and plugin bus broadcast tests still pass.
