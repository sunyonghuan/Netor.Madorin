# UI 重构技术选型决策

> **状态：已确认 · 可执行**
> 日期：2026-07-21
> 项目：Netor.Madorin — Madorin Workbench

---

## 决策摘要

**界面重构框架：Avalonia 12 + .NET 10，维持现有技术栈不变。**
**Markdown 渲染：引入 `LiveMarkdown.Avalonia` 作为核心渲染控件，替换现有自研控件。**

---

## 背景

现有 `Netor.Cortana.UI` 基于 Avalonia 构建，已在生产环境运行，AOT 编译无问题。
当前自研 Markdown 渲染控件存在部分格式渲染不完整的问题，是唯一需要解决的技术短板。

备选方案二（Browser + HTML + WebSocket）经过评估后排除，原因见下文。

---

## 方案对比

### 方案一：Avalonia 原生（选定）

| 维度 | 结论 |
|------|------|
| AOT 支持 | ✅ 已在生产验证，完整支持 Native AOT win-x64 |
| 启动速度 | ✅ 极快，原生渲染 |
| 内存占用 | ✅ 低，不引入额外运行时 |
| 跨平台 | ✅ Avalonia 一套代码 Windows / macOS / Linux |
| Markdown 渲染 | ⚠ 原自研控件有格式缺陷 → **引入成熟第三方控件解决** |
| 流式文字输出 | ⚠ 实现复杂度中等 → LiveMarkdown 原生支持 |
| 代码量 | ✅ 沿用现有架构，无需整体重写 |

### 方案二：Browser + HTML + WebSocket（排除）

排除原因：
- 本地插件重度执行 + 可能的本地模型部署，内存本已是大头，再叠加 WebView2 的 150–300 MB 不可接受
- 两套技术栈（.NET + JS）增加维护成本
- Avalonia AOT 已经稳定，Browser 方案没有 AOT 优势
- 现有 Avalonia 代码资产丢失

---

## Markdown 控件验证结论

### 验证环境
- .NET 10.0.301 SDK
- Avalonia 12.0.3
- 验证项目：`E:\Temp\MarkdownValidation\MdValidation`

### 候选库

#### ① LiveMarkdown.Avalonia 2.2.1（**首选**）

```xml
<PackageReference Include="LiveMarkdown.Avalonia" Version="2.2.1" />
```

| 项目 | 结果 |
|------|------|
| Avalonia 12 兼容 | ✅ 直接兼容 |
| AOT 编译 | ✅ **零警告**，发布通过 |
| 设计目标 | 专为 AI / LLM 流式响应场景设计 |
| 流式追加 | ✅ 原生支持，低闪烁 |
| NuGet | ✅ `LiveMarkdown.Avalonia` 2.2.1 |
| 核心控件 | `LiveMarkdown.Avalonia.MarkdownTextBlock` |
| 内容属性 | `Text`（字符串，直接赋值） |

**流式使用模式：**
```csharp
// 每次收到 token 直接追加
_mdTextBlock.Text += token;
// 或整体更新
_mdTextBlock.Text = accumulatedMarkdown;
```

#### ② ClassIsland.Markdown.Avalonia.Tight 12.0.0（**备选**）

```xml
<PackageReference Include="ClassIsland.Markdown.Avalonia.Tight" Version="12.0.0" />
```

| 项目 | 结果 |
|------|------|
| Avalonia 12 兼容 | ✅ 专为 Avalonia 12 适配的 fork |
| AOT 编译 | ⚠ 2 条程序集汇总警告（IL2104 / IL3053），编译通过 |
| 格式支持 | 更全面，支持扩展插件体系 |
| 流式支持 | ❌ 非专门设计，需自行节流刷新 |
| NuGet | ✅ `ClassIsland.Markdown.Avalonia.Tight` |
| 核心控件 | `Markdown.Avalonia.MarkdownScrollViewer` |
| 内容属性 | `Markdown`（字符串） |

### AOT 编译输出
```
✅ 发布成功
输出：MdValidation.exe — 24 MB (Native AOT 单文件)
LiveMarkdown.Avalonia：0 警告
ClassIsland.Tight：2 条汇总警告（可压制）
```

---

## 落地方案

### 使用策略

**主渲染控件：`LiveMarkdown.Avalonia.MarkdownTextBlock`**

适用场景：
- 专家模式对话气泡（AI 回复）
- 工作模式对话视图
- 会议模式发言气泡
- 对话面板（conv-panel）中的 AI 消息

**补充方案：`ClassIsland.Markdown.Avalonia.Tight`（可选）**

适用场景：
- 如果某些格式（如复杂表格、数学公式扩展）LiveMarkdown 渲染不完整时作为备用
- 产物预览、静态文档展示等非流式场景

### 替换现有控件的步骤

1. 在 `Netor.Madorin.Desktop` 或对应 UI 项目中添加 NuGet 引用：
   ```xml
   <PackageReference Include="LiveMarkdown.Avalonia" Version="2.2.1" />
   ```

2. 在 AXAML 中引入命名空间：
   ```xml
   xmlns:lmd="using:LiveMarkdown.Avalonia"
   ```

3. 替换现有自研 Markdown 控件：
   ```xml
   <!-- 旧：自研控件 -->
   <!-- <local:MarkdownView Markdown="{Binding Content}" /> -->

   <!-- 新：LiveMarkdown -->
   <ScrollViewer>
     <lmd:MarkdownTextBlock Text="{Binding Content}" />
   </ScrollViewer>
   ```

4. 流式输出绑定（ViewModel 侧）：
   ```csharp
   // 在 Token 回调中直接更新属性
   public string MarkdownContent
   {
       get => _markdownContent;
       set => this.RaiseAndSetIfChanged(ref _markdownContent, value);
   }
   
   // Stream token handler
   private void OnToken(string token)
   {
       MarkdownContent += token; // UI 自动刷新
   }
   ```

5. AOT 发布验证：
   ```bash
   dotnet publish -c Release -r win-x64 -p:PublishAot=true --self-contained
   ```
   期望：0 error，LiveMarkdown 0 warning

---

## 关联文档

- [多智能体并行UI重构方案.md](多智能体并行UI重构方案.md) — 整体 UI 架构方案
- [界面样板/index.html](界面样板/index.html) — 交互原型

---

## 备注

- 验证项目保留在 `E:\Temp\MarkdownValidation\MdValidation`，可随时复用测试新格式
- 如后续升级 Avalonia 版本，需重新验证两个库的兼容性
- LiveMarkdown.Avalonia 的 `Text` 属性是字符串类型，支持 CompiledBinding，完全 AOT 安全
