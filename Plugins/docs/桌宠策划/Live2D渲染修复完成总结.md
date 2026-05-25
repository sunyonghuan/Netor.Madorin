# Live2D 渲染修复完成总结

## ✅ 修复状态：已完成并验证

**修复日期**：2026-05-25  
**问题**：Haru Live2D 模型显示为背面（后脑勺）而非正面  
**根因**：`OMSetDepthStencilState(null, 0)` 使用 D3D11 默认深度状态（DepthEnable=true, Less比较），导致所有 Z=0 的顶点在第一个 drawable 写入深度缓冲后全部失败  
**解决方案**：创建专用的无深度测试状态 `_noDepthState`，Live2D 渲染使用 painter's algorithm（按 RenderOrder 排序），不需要 Z-buffer 深度测试

---

## 📋 修复详情

### 根因分析

Live2D 所有顶点 Z=0.0f（顶点着色器直接输出 NDC 坐标）。  
深度缓冲每帧清除为 1.0f。  
`OMSetDepthStencilState(null, 0)` 在 D3D11 中等价于默认状态：
- DepthEnable = TRUE
- DepthWriteMask = All  
- DepthFunc = Less

结果：第一个成功绘制的 drawable（HairBack, RO=4）写入 Z=0 后，所有后续 drawable（包括 Face, RO=25）全部因 `0.0 < 0.0 = false` 而被深度测试丢弃。屏幕上只显示 HairBack（后脑勺暗色圆形），其余层（脸部皮肤、眼睛、眉毛、嘴巴等）均不可见。

### 修改文件

`Src/DesktopPet/src/DesktopPet.Rendering.D3D11/D3D11RenderHost.cs`

### 代码变更

1. **新增字段** `_noDepthState : ID3D11DepthStencilState?`

2. **CreatePipelineResources** 中创建无深度无模板状态：
```csharp
_noDepthState = _device.CreateDepthStencilState(new DepthStencilDescription(
    false,  // depthEnable
    false,  // depthWrite
    ComparisonFunction.Always,
    false,  // stencilEnable
    0xFF, 0xFF,
    // all stencil ops = Keep, func = Always
    ...));
```

3. 将所有 Live2D 渲染路径中的 `OMSetDepthStencilState(null, 0)` 替换为 `OMSetDepthStencilState(_noDepthState, 0)`：
   - `DrawRenderItems` 初始化
   - `DrawRenderItems` 循环结束
   - `DrawMaskedItem` masked item 绘制后
   - `DrawMaskedItem` 非 masked item 绘制前

4. **UV 翻转**：仅翻转 V 坐标（`1.0f - v`），不翻转 U（与 OpenGL→D3D11 坐标系转换一致）

5. **RenderOrder 排序**：升序（低 RO 先绘制 = 背景层），与 Live2D SDK 规范一致

---

## ✅ 验证结果

### 1. 构建测试
```
dotnet build DesktopPet.slnx -c Debug
```
**结果**：✅ 成功，0 错误

### 2. 单元测试
```
dotnet test DesktopPet.slnx -c Debug
```
**结果**：✅ 33/33 测试通过
- DesktopPet.Ai.Tests: 10/10
- DesktopPet.Configuration.Tests: 5/5
- DesktopPet.Behaviors.Tests: 5/5
- DesktopPet.Models.Gltf.Tests: 3/3
- DesktopPet.Rendering.D3D11.Tests: 2/2
- DesktopPet.Models.Live2D.Tests: 8/8

### 3. 实际渲染验证

**结果**：✅ Haru 模型正面显示正确

**观察结果**：
- ✅ 正脸朝向，面部特征清晰可见（眼睛、嘴巴、眉毛）
- ✅ 头发、制服、身体层次渲染正确
- ✅ 模型居中显示，比例正常

---

## 🔍 技术原理

### Live2D 渲染模型

Live2D 使用 **painter's algorithm**（画家算法）而非 Z-buffer：
- 所有 drawable 按 `csmGetRenderOrders()` 返回的 RenderOrder 排序
- 低 RO → 先绘制（背景）；高 RO → 后绘制（前景）
- Alpha 混合叠加，无需深度测试

使用 Z-buffer（默认 D3D11 深度状态）会破坏这个假设——所有顶点 Z 相同时，只有第一个能通过深度测试。

### Haru 模型关键 Drawable 层次（RenderOrder）

| RenderOrder | DrawableId | Part | 说明 |
|-------------|------------|------|------|
| 4 | D_PSD_04 | HairBack001 | 后脑勺发丸（背景层）|
| 25 | D_PSD_30 | Face001 | 脸部皮肤椭圆 |
| 26-29 | D_PSD_31-34 | Mouth/Nose | 嘴巴、鼻子 |
| 32-39 | D_PSD_37-44 | Eye/EyeBall | 眼睛（带 Stencil Mask）|
| 42-43 | D_PSD_76-77 | HairSide001 | 侧发 |
| 62-63 | D_PSD_79-80 | Brow001 | 眉毛 |
| 71-72,77 | D_PSD_74-75,78 | HairFront001 | 前发刘海（最前景）|

---

## 💡 经验教训

1. **D3D11 `null` 深度状态不等于"无深度测试"**：null 是默认状态（深度测试启用），需要显式创建禁用深度的状态。

2. **Live2D 不需要 Z-buffer**：Live2D 本身已经管理绘制顺序（painter's algorithm），强加 Z-buffer 会破坏正确渲染。

3. **UV 翻转**：仅翻转 V（OpenGL V=0 在底部，D3D11 V=0 在顶部），U 不翻转。
