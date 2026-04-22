# Netor.Cortana.Plugin.Process.Generator

Cortana Process 通道插件框架的 Roslyn Source Generator，负责根据标记代码生成入口、清单和调试辅助代码。

## 功能

- 扫描 `[Plugin]` 入口类和 `[Tool]` 工具类
- 生成 `Program.g.cs`，补齐消息循环入口、工具路由和 DI 注册代码
- 生成 `{PluginClass}Debugger.g.cs`，提供强类型调试辅助代码
- 生成 `plugin.json` 清单内容，供宿主发现和加载插件
- 在编译期输出诊断，尽早发现插件声明、工具签名和命名冲突问题

## 安装

```shell
dotnet add package Netor.Cortana.Plugin.Process.Generator
```

## 工作原理

```text
[Plugin] + [Tool] 标记代码
	|
	v
ProcessPluginGenerator
	|
	+-- PluginClassAnalyzer   -> 分析插件入口类
	+-- ToolClassAnalyzer     -> 扫描工具类与工具方法
	+-- ToolNameGenerator     -> 生成工具名并检测冲突
	|
	+-- ProgramEmitter        -> 生成 Program.g.cs
	+-- DebuggerEmitter       -> 生成 Debugger.g.cs
	+-- PluginJsonEmitter     -> 生成 plugin.json 内容
```

## 生成产物

| 生成文件 | 说明 |
|------|------|
| `Program.g.cs` | 生成插件入口、消息循环启动代码、工具路由字典和 DI 注册代码 |
| `{PluginClass}Debugger.g.cs` | 生成强类型调试辅助类，便于本地调试和测试 |
| `plugin.json` | 生成宿主加载所需的插件元数据清单 |

## 集成方式

- Generator 在编译时运行，不需要手写入口主循环
- `build/Netor.Cortana.Plugin.Process.Generator.targets` 负责把生成内容参与构建输出
- 消费项目只需要专注于声明插件元数据和工具方法

## 编译期诊断

| 诊断 ID | 说明 |
|------|------|
| `CNPG003` | 找不到 `[Plugin]` 标记的入口类 |
| `CNPG005` | `[Plugin]` 类不是要求的声明形式 |
| `CNPG006` | 缺少 `Configure(IServiceCollection)` 方法 |
| `CNPG007` | `Configure` 方法签名不正确 |
| `CNPG008` | 工具方法使用了不支持的参数或返回类型 |
| `CNPG010` | 存在多个 `[Plugin]` 入口类 |
| `CNPG011` | 工具名冲突 |
| `CNPG012` | `[Plugin].Id` 格式不合法 |
| `CNPG019` | 工具类缺少 `[Tool]` 标记 |

## 项目结构

- `Analysis/` - 语义分析、工具扫描、命名和类型映射
- `Emitters/` - 生成 Program、Debugger 和 plugin.json 的代码输出器
- `Diagnostics/` - 编译期诊断定义
- `build/` - 参与 NuGet 包分发的 MSBuild 目标文件

## 要求

- Generator 项目本身目标框架为 netstandard2.0
- 依赖 Roslyn 编译器 API
- 主要面向 `Netor.Cortana.Plugin.Process` 消费项目使用