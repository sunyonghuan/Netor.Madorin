# 桌面 3D 宠物独立 EXE 技术方案

> 阶段：策划阶段  
> 方向：不以 Cortana 插件形态实现，改为独立桌面 EXE，应用内置 MCP 工具与 WebSocket 对话流接入。  
> 核心目标：轻量、常驻、高性能、兼容 Live2D / Live3D / VRM / glTF 开源模型生态。
>
> 当前架构基线详见：`桌面3D宠物项目构架.md`。本文保留技术选型讨论与验证结论，正式工程分层以项目构架文档为准。

## 1. 背景与目标

原先考虑将桌宠作为 Cortana Native 插件实现，但桌宠具备常驻窗口、透明置顶、GPU 渲染、模型资源加载、右键菜单、动画状态机、字幕显示等能力，这些职责并不适合放进插件 DLL 或 NativeHost 子进程中。

新的方案将桌宠定义为独立桌面应用：

- 独立 EXE 负责窗口、渲染、模型、动画、字幕、右键菜单。
- 应用内置 MCP 工具负责让 AI 或宿主控制桌宠。
- 实时对话流通过 WebSocket 接入。
- 嘴唇同步第一阶段不做精确音素对齐，只做“说话状态下随机/循环嘴部动作”。
- 优先保证桌宠常驻时低 CPU、低内存、低进程数量、易退出、易恢复。

## 2. 明确排除的路线

### 2.1 WebView2

不采用 WebView2 作为 3D 渲染层。

原因：

- 会引入 Edge/WebView2 多进程模型。
- 常驻桌宠场景下进程数量偏多。
- 退出与资源释放体验不可控。
- 对一个桌面宠物来说，浏览器运行时过重。

### 2.2 WPF

不采用 WPF 作为主窗口或主渲染层。

原因：

- 框架较重。
- 性能与常驻体验不符合目标。
- 透明窗口、GPU 渲染、点击穿透等能力最终仍要依赖底层 Win32 / DirectX。

### 2.3 Avalonia 主渲染

Avalonia 可作为设置面板候选，但不建议承担主桌宠渲染。

原因：

- Avalonia 内置 Skia，体积约十几 MB 级别。
- 对设置页、模型管理页、配置面板很合适。
- 对“右下角常驻透明 3D / Live2D 角色”来说，UI 框架不是核心价值，反而增加运行时成本。

## 3. 推荐总体架构

推荐采用“单应用进程 + 内置 MCP + WebSocket 对话流 + 原生 GPU 渲染”的架构。

```text
AI / 宿主
   │
   │ MCP stdio / streamable-http
   ▼
DesktopPet.exe
   │
   ├─ MCP 工具服务
   ├─ WebSocket 对话流客户端
   ├─ 透明置顶窗口
   ├─ DirectX 11 / OpenGL / Vulkan 渲染
   ├─ Live2D Cubism Native SDK 互操作
   ├─ glTF / GLB / VRM 模型加载
   ├─ 动画状态机
   ├─ 字幕显示
   └─ 右键菜单 / 托盘 / 配置
```

### 3.1 DesktopPet.exe

桌宠主程序，负责用户可见体验、AI 工具暴露和实时对话流接入。

职责：

- 创建透明、无边框、置顶窗口。
- 支持右下角默认定位、多显示器定位、拖拽、贴边、缩放。
- 支持修改窗口大小。
- 自动记住窗口大小和位置，再次启动恢复。
- 支持点击穿透开关。
- 加载 Live2D / 3D 模型。
- 播放待机、思考、说话、隐藏、显示等动画。
- 显示字幕。
- 提供右键菜单。
- 内置 MCP tools。
- 通过 WebSocket 接收实时对话流。
- 根据 AI 思考、输出、完成等事件切换动画状态。

### 3.2 单进程收益

- 启动简单。
- 部署简单。
- 用户只看到一个应用程序。
- 不需要维护本地 IPC。
- 右键退出即可完整关闭。

### 3.3 单进程约束

- MCP、WebSocket、渲染循环必须做好异步边界，不能互相阻塞。
- UI / 渲染线程必须独立于 MCP 请求处理。
- 所有可选依赖都要提前做 AOT 兼容验证。
- 如果 Live2D / 3D 模型依赖无法 Native AOT，则保留 IL 发布回退路径。

## 4. 技术选型建议

> 2026-05-24 验证后更新：当前主线已经收敛为“纯 C# 单 EXE + Win32 + Direct3D11 + Live2D Native Core 薄绑定 + AOT-first”。下面的 C++ / Rust / Stride / Unity 仍作为备选路线记录，不再作为第一版默认路线。

## 4.1 首选路线：C++ 原生渲染

推荐：

```text
C++ 20/23
Win32
DirectX 11
Live2D Cubism Native SDK
glTF / GLB / VRM 加载器
Dear ImGui 或原生 Win32 菜单用于调试/设置
```

适用目标：

- Windows 优先。
- 强调性能、体积、稳定退出、低资源占用。
- 希望深度控制透明窗口、输入穿透、GPU 渲染循环。

优点：

- 常驻性能最好。
- 可控性最高。
- 与 Live2D Cubism Native SDK 匹配度高。
- 不受 .NET Native AOT 约束。
- 适合做长期产品级桌宠。

缺点：

- 开发成本高于 C# / Unity。
- VRM、PMX、动画混合等能力需要逐步建设。
- 需要更强的渲染和资源管理工程能力。

## 4.2 跨平台候选：Rust 原生渲染

可选：

```text
Rust
winit
wgpu
Live2D Native SDK FFI
gltf-rs
```

优点：

- 现代工程体验好。
- wgpu 可覆盖 DirectX / Vulkan / Metal。
- 跨平台潜力好。
- 内存安全较强。

缺点：

- Live2D Native SDK 接入需要 FFI 包装。
- Windows 桌面特性如透明窗口、点击穿透、托盘菜单仍要写平台代码。
- VRM / Live2D 生态资料不如 Unity/C++ 直观。

## 4.3 折中路线：Stride

可选：

```text
C#
Stride 3D
Direct3D / OpenGL / Vulkan
单独接入 Live2D Native SDK
```

优点：

- C# 开发效率较高。
- 已有完整 3D 引擎能力。
- 相机、材质、动画、资源管理不用全部自研。

缺点：

- 引擎体积不会特别小。
- Live2D 仍需单独集成。
- VRM / PMX / Live2D 生态兼容不如 Unity。
- Native AOT 风险较高，不建议第一版强求 AOT。

## 4.4 不推荐长期主线：Unity

Unity 的模型兼容生态很强，VRM、Live2D、MMD、动画、材质、特效都有成熟方案。

但不推荐作为长期主线：

- 运行时重。
- 体积大。
- 启动慢。
- 透明桌面、点击穿透、托盘等桌宠能力需要额外原生插件。
- 产品控制感不如自研原生渲染壳。

Unity 可以作为效果验证原型，但不建议作为最终产品基础。

## 5. 模型兼容规划

### 5.1 第一阶段必须支持

#### Live2D Cubism

支持文件：

- `.model3.json`
- `.moc3`
- texture
- physics
- pose
- motion
- expression

能力：

- 待机动作。
- 表情切换。
- 简单说话嘴部动作。
- 鼠标交互区域。
- 基础物理。

#### glTF / GLB

支持文件：

- `.gltf`
- `.glb`

能力：

- mesh 加载。
- texture 加载。
- material 加载。
- skeleton / skinning。
- animation clips。

#### VRM

支持：

- VRM 0.x。
- VRM 1.0 作为后续增强。

能力：

- humanoid 骨骼。
- blendshape / expression。
- look-at。
- spring bone。
- 待机与说话状态动作。

### 5.2 第二阶段支持

#### PMX / PMD

MMD 模型生态很大，但格式、材质、物理、toon 渲染、动作兼容复杂。

建议第二阶段支持：

- PMX / PMD 模型加载。
- VMD 动作加载。
- toon 材质。
- 基础物理。

不建议第一阶段做，以免拖慢主线。

## 6. 动画与状态机

第一阶段状态机保持简单：

```text
Idle       待机
Thinking   思考
Speaking   说话
Happy      开心
Sleep      休眠
Hidden     隐藏
Dragging   拖拽中
```

事件映射：

| 输入事件 | 状态 | 表现 |
|---|---|---|
| 没有交互 | Idle | 待机动画循环 |
| AI 开始思考 | Thinking | 思考动作、眼神变化、轻微摆动 |
| AI 有文字输出 | Speaking | 字幕显示、嘴部随机动作、身体轻微动作 |
| AI 输出完成 | Idle | 回到待机 |
| 用户右键隐藏 | Hidden | 淡出或收起 |
| 鼠标拖动 | Dragging | 暂停自动定位 |

## 7. 嘴部动画策略

第一阶段不做精确嘴唇同步，不做音素对齐。

实现方式：

- 有文本输出时进入 Speaking 状态。
- 每 80-180ms 随机选择一个嘴部 morph / parameter。
- 根据文字输出节奏调节开合频率。
- 输出完成后嘴部参数平滑回到 0。

Live2D 参数示例：

- `ParamMouthOpenY`
- `ParamMouthForm`

VRM / glTF 表情示例：

- `aa`
- `ih`
- `ou`
- `ee`
- `oh`
- 或模型自定义 blendshape。

后续若接入 TTS，可用音量 RMS 驱动嘴部开合；再后续才考虑音素/viseme。

## 8. 字幕系统

字幕由 Render 主程序显示，不依赖 WebView。

能力：

- 半透明字幕气泡。
- 自动换行。
- 流式追加。
- 输出结束后延迟淡出。
- 支持最多保留最近一句。
- 可通过右键菜单关闭字幕。

字幕输入来源：

- MCP `pet_say` 工具。
- MCP `pet_think` 工具。
- 宿主主动推送的文本事件。
- WebSocket 实时对话事件。

## 9. MCP 工具规划

第一阶段 MCP tools：

| 工具名 | 说明 |
|---|---|
| `pet_show` | 显示桌宠 |
| `pet_hide` | 隐藏桌宠 |
| `pet_say` | 显示一句话并进入说话状态 |
| `pet_think` | 显示思考状态 |
| `pet_idle` | 回到待机状态 |
| `pet_play_animation` | 播放指定动画 |
| `pet_set_expression` | 设置表情 |
| `pet_change_model` | 切换模型 |
| `pet_status` | 查询桌宠状态 |

工具调用不直接操作 GPU 或窗口，只向应用内事件队列投递命令，由渲染/状态机线程按帧消费。

## 10. 应用内消息设计

单 EXE 方案不使用本地 IPC。MCP 工具、WebSocket 对话流、右键菜单、托盘事件统一投递到应用内消息队列。

优点：

- 避免 MCP 请求线程直接操作渲染对象。
- 避免 WebSocket 流式回调阻塞 UI / 渲染线程。
- 保持动画状态切换有序。
- 便于后续记录事件日志和复现问题。

消息格式：

```json
{
  "type": "command",
  "requestId": "uuid",
  "command": "say",
  "payload": {
    "text": "你好，我在这里。",
    "mode": "speaking"
  }
}
```

响应格式：

```json
{
  "type": "response",
  "requestId": "uuid",
  "success": true,
  "error": null
}
```

线程边界：

| 来源 | 线程/任务 | 处理方式 |
|---|---|---|
| MCP tools | 后台异步任务 | 投递命令，等待轻量确认 |
| WebSocket 对话流 | 后台异步任务 | 投递 thinking/speaking/idle 事件 |
| 右键菜单 | UI 线程 | 投递用户命令 |
| 渲染循环 | 渲染线程 | 每帧消费状态快照，不执行阻塞 I/O |

## 11. AOT 风险分析

### 11.1 第一版必须按 AOT 约束设计

第一版就要打好 AOT 框架，否则后续再改会产生较大返工。

基本原则：

- `csproj` 开启 AOT/Trim 分析。
- 所有 JSON 序列化使用 `System.Text.Json` source generator。
- 禁止运行时反射扫描工具、模型、插件。
- 禁止动态加载 C# 程序集。
- 禁止依赖 `Reflection.Emit`、动态代理、运行时代码生成。
- 所有 native 调用优先使用 `LibraryImport` source generator。
- 所有依赖库进入项目前先做 AOT smoke test。

### 11.2 Live2D 风险

Live2D Cubism Native SDK 本身是 native SDK，从 .NET 角度看风险主要在 C# 绑定层：

- 如果绑定层只是 P/Invoke / LibraryImport，AOT 风险可控。
- 如果绑定层依赖反射、动态加载、复杂 marshalling，则需要替换或自写薄绑定。
- native DLL 必须随应用发布。
- 需要验证 Windows x64 Native AOT 发布后 Live2D 初始化、模型加载、纹理上传、动作播放全部可用。

结论：

- Live2D 不是必然阻止 AOT。
- 真正关键是 C# 绑定层是否 AOT 友好。
- 第一阶段应做最小 Live2D AOT 验证项目。

### 11.3 3D / VRM 风险

3D 依赖风险主要来自：

- glTF / VRM 加载库是否使用反射式 JSON。
- 数学库、图片库、压缩库是否 AOT 友好。
- 材质扩展、动画扩展是否依赖动态类型。
- VRM 生态库是否深度依赖 Unity 类型或反射。

建议：

- glTF 基础加载优先选择可控、源码可审计、System.Text.Json 友好的库。
- VRM 第一版只做最小子集，避免一开始接入复杂 Unity 生态库。
- 必须建立 `AotSmokeTests`：加载一个 GLB、一个 VRM、一个 Live2D 模型，发布 Native AOT 后真实运行。

### 11.4 回退策略

如果 Live2D 或 3D 核心依赖完全不支持 Native AOT，则保留 IL 发布模式。

回退原则：

- 源码仍保持 AOT 友好写法。
- 发布模式从 `PublishAot=true` 回退到普通 self-contained / ReadyToRun。
- 不因为回退 IL 就引入反射扫描、动态插件等设计。
- AOT smoke test 持续保留，等待依赖升级后再切回。

## 12. 发布形态

建议目录：

```text
DesktopPet/
  DesktopPet.exe
  models/
    live2d/
    vrm/
    gltf/
  config/
    settings.json
    model-profiles.json
  logs/
  runtimes/
```

MCP 配置示例：

```json
{
  "name": "Desktop Pet",
  "transportType": "stdio",
  "command": "E:\\Path\\DesktopPet\\DesktopPet.exe",
  "arguments": ["--mcp"]
}
```

运行模式：

| 模式 | 启动方式 | 说明 |
|---|---|---|
| 桌宠模式 | `DesktopPet.exe` | 启动窗口、托盘、渲染、WS 对话流 |
| MCP 模式 | `DesktopPet.exe --mcp` | 同一 EXE 进入 MCP stdio 模式 |
| 单实例控制 | `DesktopPet.exe --show` | 唤起已运行实例 |

虽然是同一个 EXE，但可以通过命令行模式区分桌宠窗口与 MCP stdio 行为。若宿主要求 stdio MCP 长驻，可由 `--mcp` 模式连接或唤起当前单实例。

## 13. 第一阶段里程碑

### M1 原生窗口

- 无边框透明窗口。
- 右下角定位。
- 置顶。
- 拖拽改变位置。
- 修改窗口大小。
- 自动记住大小和位置。
- 再次启动恢复上次大小和位置。
- 右键菜单：显示/隐藏、退出、切换模型、置顶、点击穿透、重置位置、保存当前位置和大小。
- 退出干净。

### M2 Live2D 最小加载

- 加载一个 Live2D 模型。
- 播放 idle motion。
- 切换 expression。
- 支持基础嘴部动作。

### M3 3D 模型最小加载

- 加载 GLB。
- 加载 VRM。
- 播放模型自带动画。
- 支持基础 expression / blendshape。

### M4 MCP 与 WS 控制

- MCP 暴露 show/hide/say/think/status。
- 应用内置 MCP stdio 模式。
- WebSocket 接入实时对话流。
- MCP / WS 事件统一进入应用内队列。

### M5 桌宠体验闭环

- AI 输出文字时显示字幕。
- 说话状态随机嘴部动作。
- 思考状态动作。
- 输出结束回到待机。
- 模型与窗口配置持久化。

## 14. 最终建议

长期产品路线建议：

```text
纯 C# 单 EXE
C# LibraryImport 调 Win32
Vortice.Direct3D11 GPU 渲染
Live2D Cubism Native SDK 薄绑定
glTF / GLB / VRM 加载
内置 MCP stdio 模式
WebSocket 实时对话流
AOT-first 工程约束
```

主桌宠窗口不建议依赖 WebView2、WPF、Avalonia 或 Silk.NET.Windowing 高层窗口 API。

当前验证后的优先路线：

```text
Win32 透明置顶窗口
  └─ C# LibraryImport / UnmanagedCallersOnly
Direct3D11 渲染
  └─ Vortice.Direct3D11
模型层
  ├─ Live2D Cubism Native SDK 薄绑定
  └─ glTF / GLB / VRM 加载器
AI 接入
  ├─ 内置 MCP
  └─ WebSocket 实时流
```

Avalonia 可以作为设置面板或模型管理器候选，但不进入第一版主桌宠窗口最小闭环。Silk.NET.Windowing 在 Native AOT 下需要显式 `GlfwWindowing.Use()`，且仍有反射/单文件相关警告，因此不作为主窗口首选。

Live2D 当前验证结论：

- 官方 `Cubism SDK for Native R5` 已下载并取到 `Live2DCubismCore.dll`。
- `Live2DCubismCore.dll` 可以随 Native AOT EXE 放在发布目录旁边加载。
- 真实样例 `Haru.moc3` 已通过 consistency check、revive moc、initialize model。
- 可以读取参数与 drawable 数量。
- 可以写入 `ParamMouthOpenY` 并调用 `csmUpdateModel`。
- 建议第一版用自研 `LibraryImport` 薄绑定直连 Core，第三方 `Live2DCSharpSDK` 可作为参考或过渡。

Vortice.Direct3D11 当前验证结论：

- D3D11 device 创建在 Native AOT 下可成功运行。
- HWND + swap chain + render target + present 路径可成功运行。
- 完整 swap chain 路径会触发 SharpGen.Runtime 的 `IL2067` / `IL2072` 反射式 vtable 警告。

因此第一阶段可用 Vortice 快速推进渲染闭环；如果后续要求严格 0 IL 警告发布，应考虑把 D3D11/DXGI 调用收敛为自研极薄 `LibraryImport` + COM vtable 绑定，只覆盖桌宠需要的 API。

如果 Live2D / 3D 核心依赖在 Native AOT 下不可用，则回退：

```text
self-contained IL 发布
ReadyToRun 可选
保留 AOT 友好代码结构
等待依赖成熟后重新开启 Native AOT
```

第一版不要追求精确嘴唇同步，不要追求 PMX/MMD 全兼容。AOT 从第一版开始打框架并持续验证；如果关键模型依赖确实不支持 AOT，再退回 IL 发布模式。
