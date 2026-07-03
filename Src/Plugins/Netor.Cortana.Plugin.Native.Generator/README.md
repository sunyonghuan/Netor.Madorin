# Netor.Cortana.Plugin.Native.Generator

Madorin 原生插件开发框架的 **Roslyn Source Generator**，自动为 `[Plugin]` / `[Tool]` 标记的代码生成 AOT 兼容的导出函数和 `plugin.json` 清单文件。

## 功能

基于 `Netor.Cortana.Plugin.Native` 中的 Attribute 标记，自动生成：

| 生成产物 | 说明 |
|----------|------|
| `Startup.g.cs` | 路由字典、桥接方法、5 个 `UnmanagedCallersOnly` 导出函数 |
| `PluginJsonContext.g.cs` | STJ 源码生成上下文（仅在有自定义返回类型时生成） |
| `plugin.json` | 插件清单文件，构建/发布时自动复制到输出目录 |

### 生成的导出函数

| 导出函数 | 说明 |
|----------|------|
| `cortana_plugin_get_info` | 返回插件和工具的完整元数据 JSON |
| `cortana_plugin_init` | 初始化 DI 容器、注册服务、启动 `IHostedService` |
| `cortana_plugin_invoke` | 按工具名路由调用，自动参数解析和错误处理 |
| `cortana_plugin_free` | 释放 `Marshal.StringToCoTaskMemUTF8` 分配的内存 |
| `cortana_plugin_destroy` | 停止 `IHostedService`，Dispose `ServiceProvider` |

## 安装

```shell
dotnet add package Netor.Cortana.Plugin.Native.Generator
```

> 通常不需要单独安装此包，安装 `Netor.Cortana.Plugin.Native` 时会自动引入。

## 工作原理

```
[Plugin] + [Tool] Attributes
        │
        ▼
  NativePluginGenerator (Incremental Source Generator)
        │
        ├── PluginClassAnalyzer   → 分析插件入口类
        ├── ToolClassAnalyzer     → 扫描所有 [Tool] 类和方法
        ├── ToolNameGenerator     → 生成工具名并检测冲突
        │
        ├── StartupEmitter        → 生成 Startup.g.cs
        ├── InfoJsonEmitter       → 生成 get_info 返回的 JSON
        ├── PluginJsonEmitter     → 生成 plugin.json 内容
        └── JsonContextEmitter    → 生成 STJ 上下文（可选）
```

## plugin.json 输出机制

Generator 通过 `context.AddSource("plugin.json", ...)` 输出 JSON 内容（以 `//` 注释包裹，作为合法 C#）。配合 `EmitCompilerGeneratedFiles=true`，Roslyn 将其落盘到 `obj` 目录。

包内附带的 `.targets` 文件会在 `Build` / `Publish` 后自动：
1. 读取落盘的 `plugin.json.cs`
2. 去掉每行 `//` 注释前缀
3. 写出为 `plugin.json` 到输出/发布目录

**消费项目只需在 csproj 中添加：**

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
```

## 编译时诊断

| 诊断 ID | 说明 |
|---------|------|
| CNPG003 | 找不到 `[Plugin]` 标记的入口类 |
| CNPG005 | `[Plugin]` 类必须是 `static partial` |
| CNPG006 | `[Plugin]` 类缺少 `Configure(IServiceCollection)` 方法 |
| CNPG007 | `Configure` 方法签名不正确 |
| CNPG008 | 工具方法使用了不支持的参数/返回类型 |
| CNPG010 | 存在多个 `[Plugin]` 入口类 |
| CNPG011 | 工具名冲突 |
| CNPG012 | `[Plugin].Id` 格式不合法 |
| CNPG019 | 工具类缺少 `[Tool]` 标记 |

## 项目结构

```
Netor.Cortana.Plugin.Native.Generator/
├── NativePluginGenerator.cs          # Generator 入口
├── Analysis/
│   ├── AnalysisModels.cs             # 分析结果模型
│   ├── PluginClassAnalyzer.cs        # [Plugin] 类分析
│   ├── ToolClassAnalyzer.cs          # [Tool] 类/方法扫描
│   ├── ToolNameGenerator.cs          # 工具名生成与冲突检测
│   └── TypeMapper.cs                 # 类型映射（JSON 解析/序列化）
├── Emitters/
│   ├── StartupEmitter.cs             # Startup.g.cs 生成
│   ├── InfoJsonEmitter.cs            # get_info JSON 生成
│   ├── PluginJsonEmitter.cs          # plugin.json 内容生成
│   └── JsonContextEmitter.cs         # PluginJsonContext.g.cs 生成
├── Diagnostics/
│   └── DiagnosticDescriptors.cs      # 编译诊断定义
└── build/
    └── Netor.Cortana.Plugin.Native.Generator.targets  # MSBuild 目标
```

## 要求

- 目标框架：netstandard2.0（Roslyn Generator 要求）
- 消费项目需 .NET 10+
- 依赖 Microsoft.CodeAnalysis.CSharp 4.12.0+
