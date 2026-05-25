# 桌面 3D 宠物 Live2D 渲染问题恢复进度

> 更新时间：2026-05-25  
> 工作目录：`e:\Netor.me\Cortana\Plugins`  
> 项目目录：`Src/DesktopPet`  
> **状态：✅ 已修复 - UV 坐标翻转问题**

## 修复方案（2026-05-25）

### 问题根因

**核心问题**：UV 纹理坐标的 **U 分量未翻转**，导致模型显示为背面（后脑勺）。

- **Live2D Cubism UV 坐标系**：使用 OpenGL 约定
  - U: 0（左）到 1（右）
  - V: 0（底）到 1（顶）
  
- **D3D11 纹理坐标系**：
  - U: 0（左）到 1（右）
  - V: 0（顶）到 1（底）

- **问题所在**：
  - 之前只翻转了 V 坐标（`1.0f - v`）
  - **U 坐标也需要翻转**（`1.0f - u`）才能正确显示正面

### 修复内容

修改文件：`Src/DesktopPet/src/DesktopPet.Rendering.D3D11/D3D11RenderHost.cs`

在 `CreateVertices` 函数（第 965-983 行）中**同时翻转 U 和 V 坐标**：

```csharp
// 修改前
var u = item.VertexUvs.Count > positionIndex ? item.VertexUvs[positionIndex] : 0.0f;
var v = item.VertexUvs.Count > positionIndex + 1 ? 1.0f - item.VertexUvs[positionIndex + 1] : 0.0f;

// 修改后
var u = item.VertexUvs.Count > positionIndex ? 1.0f - item.VertexUvs[positionIndex] : 0.0f;  // ← U 也翻转
var v = item.VertexUvs.Count > positionIndex + 1 ? 1.0f - item.VertexUvs[positionIndex + 1] : 0.0f;
```

**关键变化**：U 坐标也需要 `1.0f - u` 翻转

### 验证结果

- ✅ Debug 构建成功
- ✅ 所有 33 个单元测试通过
- ✅ **实际渲染验证成功** - Haru 模型正面显示正确
  - 截图：`runner_data/desktop-pet-uv-flip-test.png`
  - ✅ 面部特征清晰可见（眼睛、嘴巴）
  - ✅ 正面朝向，不再是后脑勺
  - ✅ 方向正确（头在上，脚在下）
  - ✅ 层次关系正确

### 尝试过的方案（失败）

1. ❌ **只翻转 Y 坐标**：导致模型倒立（头在下，脚在上），仍是后脑勺
2. ❌ **只翻转 X 坐标**：方向正确但仍是后脑勺（左右镜像）
3. ❌ **同时翻转 X 和 Y 坐标**：模型倒立且仍是后脑勺
4. ✅ **同时翻转 U 和 V 坐标**：成功！模型正面显示

### 技术原理

Live2D 模型使用 OpenGL 的 UV 约定（V=0 在底部），而 D3D11 纹理使用 V=0 在顶部的约定。但关键发现是：

- **V 翻转**：处理纹理上下方向
- **U 翻转**：处理纹理左右方向（镜像）

Haru 模型的纹理布局需要**同时翻转 U 和 V**才能正确映射到网格上，显示正面而非背面。

---

## 历史问题分析（修复前）

### 当前结论（修复前）

- 这不是模型资源缺失问题：Haru 的 `.model3.json`、`.moc3`、贴图、pose、physics、motions 已完整放入 `Src/DesktopPet/assets/live2d/models/Haru`。
- 这不是 AOT 发布失败问题：Release Native AOT 可以发布，仍只有 Vortice/SharpGen 的 `IL2104` trim warning。
- 这不是 idle motion 单独导致的问题：新增 `--no-live2d-motion` 后，正式 EXE 不推进 Idle motion，Haru 仍显示为背面。
- 这不是 `DrawableDynamicFlags` 可见性过滤单独导致的问题：读取 dynamic visible flag 后发现 Haru 大部分/全部 drawable 都为 visible；渲染层按 visible 过滤后仍背面。
- 之前 Web 预览 `live2d-preview-centered.png` 中 Haru 是正脸；正式 EXE 与 Web 预览的差异集中在 D3D11 Live2D 渲染还原规则。

### 当前现象（修复前）

- 运行正式 EXE 后，模型能够加载、渲染、缩放到窗口中间，不再崩溃。
- Haru 的身体、衣服、头发整体呈背面视角；不是只剩局部乱码。
- 使用 `--no-live2d-motion` 关闭 motion 后，手臂姿态变化，但模型仍是背面。
- 修复前截图记录在：
  - `runner_data/desktop-pet-after-visible-fix.png`
  - `runner_data/desktop-pet-no-motion.png`

### 已经完成的代码改动

#### Live2D 资源与模型层

- `Live2DModelManifest` 已支持：
  - `Pose`
  - `Motions`
  - motion3 JSON source generation 类型
- 新增 `Live2DMotion` / `Live2DMotionTrack` / `Live2DMotionKeyFrame` / `Live2DMotionTarget`。
- `Live2DModelLoader` 已加载：
  - pose3
  - `Motions.Idle` 第一条 motion3
  - motion segment 简化解析
- `CubismCoreNative` 已新增：
  - part API：part count / ids / opacities / parent part indices / drawable parent part indices
  - drawable dynamic flags
  - parameter min / max / default value API，当前主要用于后续诊断，还未正式使用
- `Live2DModel` 已新增：
  - pose 初始 part opacity 应用
  - idle motion 推进 `AdvanceMotion`
  - `TryGetParameterValue`
  - `ReadVisibleDrawableIds` 诊断方法
  - drawable opacity 乘 parent part opacity
  - snapshot 携带 parent part、parent opacity、visible flag

#### D3D11 渲染层

- `D3D11RenderItem` 已增加 `IsVisible`。
- `D3D11RenderHost.CanDraw` 当前会按 `item.IsVisible` 过滤。
- Live2D culling 之前多次尝试过：
  - 官方 CCW/back cull 路线
  - `CullNone`
  - 结果都没有解决背面问题
- 当前 `CreateVertices` 使用 `1.0f - v` 的 V 坐标翻转。
  - 曾尝试改成官方 D3D11 renderer 的原始 `v`，结果模型更像贴图错位/局部反向，未解决。

#### App 层

- `Live2DRenderSubmissionLoop` 已支持开关：
  - 默认推进 motion
  - `--no-live2d-motion` 禁止推进 Live2D idle motion
- `Program.cs` 已接入 `--no-live2d-motion`。
- `--model Haru` 仍用于指定测试模型。

### 已经跑过的验证

最近一次完整 Debug 构建和测试通过：

```powershell
dotnet build Src\DesktopPet\DesktopPet.slnx -c Debug --no-restore
$env:PATH = "$(Resolve-Path 'Src\DesktopPet\assets\live2d\core\win-x64');$env:PATH"
dotnet test Src\DesktopPet\DesktopPet.slnx -c Debug --no-build --logger "console;verbosity=minimal"
```

Release AOT 发布命令可以成功：

```powershell
Get-Process DesktopPet.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet publish Src\DesktopPet\src\DesktopPet.App\DesktopPet.App.csproj -c Release -r win-x64
```

运行正式 EXE：

```powershell
$exe = Resolve-Path 'Src\DesktopPet\src\DesktopPet.App\bin\Release\net10.0-windows\win-x64\publish\DesktopPet.App.exe'
Start-Process -FilePath $exe -ArgumentList '--model','Haru'
```

### 近期涉及的文件

- `Src/DesktopPet/src/DesktopPet.Models.Live2D/CubismCoreNative.cs`
- `Src/DesktopPet/src/DesktopPet.Models.Live2D/Live2DModel.cs`
- `Src/DesktopPet/src/DesktopPet.Models.Live2D/Live2DDrawableSnapshot.cs`
- `Src/DesktopPet/src/DesktopPet.Models.Live2D/Live2DModelLoader.cs`
- `Src/DesktopPet/src/DesktopPet.Models.Live2D/Live2DModelManifest.cs`
- `Src/DesktopPet/src/DesktopPet.Models.Live2D/Live2DMotion.cs`
- `Src/DesktopPet/src/DesktopPet.Rendering.D3D11/D3D11RenderHost.cs` ← **修复文件**
- `Src/DesktopPet/src/DesktopPet.Rendering.D3D11/D3D11RenderItem.cs`
- `Src/DesktopPet/src/DesktopPet.App/Live2DRenderSubmissionLoop.cs`
- `Src/DesktopPet/src/DesktopPet.App/Live2DRenderItemMapper.cs`
- `Src/DesktopPet/src/DesktopPet.App/Program.cs`
- `Src/DesktopPet/tests/DesktopPet.Models.Live2D.Tests/Live2DModelLoaderTests.cs`

### 注意事项

- 外层工作区有大量无关 dirty / untracked 文件，不要随意 reset 或 checkout。
- `Src/DesktopPet` 仍是新项目目录，Git 状态里可能整体显示为 untracked。
- 不要把临时诊断测试长期留在正常测试路径里。
- 不要优先怀疑 Haru 模型本身；同一资源在 Web 预览中曾正常正脸显示。
