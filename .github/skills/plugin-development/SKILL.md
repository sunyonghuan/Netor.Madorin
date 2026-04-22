---
name: plugin-development
version: 5
description: ' 插件开发全流程技能。按通道类型引导：Native DLL / Process EXE / MCP。主推 C#（提供脚手架），其他语言提供协议规范。触发关键词：插件开发、创建插件、发布插件、Native 插件、Process 插件、MCP 接入。'
user-invocable: true
---

# Plugin Development

## 选通道

                         ┌─ 接现成工具服务 (GitHub/Filesystem/自建 MCP Server)
                         │    → MCP 通道（UI 配置，不写代码）→ references/mcp-setup.md
    做扩展 ───────────── │
                         │                          ┌─ 原生 DLL → Native 通道
                         └─ 自己写 ─ 产物是什么？ ──┤
                                                    └─ EXE      → Process 通道

- **Native 通道**：原生 DLL（C ABI）。C# 必须 **AOT 发布**（IL DLL 加载不了）。C/C++/Rust/Go 可出原生 DLL 走这条路。
- **Process 通道**：EXE 可执行文件。**任何语言**都能实现，跑 stdio NDJSON 协议。
- **MCP 通道**：连外部 MCP Server，数据库配置，不写 plugin.json。

## 零、环境准备

运行一次即可（自动检测并安装缺失组件）：

```powershell
.\scripts\setup-dev-environment.ps1
```

检查 .NET 10+ SDK 和 AOT 需要的 C++ 构建工具。不做 Native DLL 可加 `-SkipAot`。

## 一、先用 ScriptRunner 验原型（可选）

写插件前，用 C# 脚本插件试一把：

- `sys_csx_run_str` 直接跑业务逻辑片段
- `#r "nuget:Humanizer, 2.14.1"` 试 NuGet 包能否满足需求
- `sys_csx_session_*` 保留跨次状态，模拟插件持久层

原型通过再决定做 Native 还是 Process，避免白造。

## 二、Native 通道（原生 DLL）

### C#（有完整脚手架）

```powershell
.\scripts\create-native-plugin.ps1 -Name MyPlugin -Id my_plugin
# ... 写业务代码 ...
.\scripts\publish-native-plugin.ps1 -ProjectDir Samples\MyPlugin
```

细节：[references/csharp-native.md](./references/csharp-native.md)
错误速查：[references/csharp-aot-errors.md](./references/csharp-aot-errors.md)

### 其他语言（C / C++ / Rust / Go / Zig）

任何能产出导出符号的原生 DLL 的语言都能接入。没有脚手架，按 C ABI 契约自行实现：[references/native-abi.md](./references/native-abi.md)

内容包含：5 个导出函数签名、内存所有权规则、plugin.json 格式、C/Rust/Go 最小示例。

> Python / Node.js 等解释型语言不适合 Native 通道（无法产出原生 DLL），请走 Process 通道。

## 三、Process 通道（EXE 子进程）

### C#（有完整脚手架）

```powershell
.\scripts\create-process-plugin.ps1 -Name MyPlugin -Id my_plugin
# ... 写业务代码 ...
.\scripts\publish-process-plugin.ps1 -ProjectDir Samples\MyPlugin
```

脚手架默认引用 `Netor.Cortana.Plugin.Process` 框架；编译时会自动生成消息循环、`plugin.json` 和 `{PluginClass}Debugger`。

### 其他语言（Python / Node.js / Go / Rust ...）

**没有脚手架，按协议规范自行实现**：[references/process-protocol.md](./references/process-protocol.md)

该规范是语言中立的完整契约：NDJSON 帧格式、4 个方法 (`get_info`/`init`/`invoke`/`destroy`)、`plugin.json` 结构、实现要点清单。

打包要求：产出单个 EXE；C# 场景优先使用 `publish-process-plugin.ps1` 统一输出运行目录和 zip 包。

## 四、MCP 通道

不写插件，在 Cortana UI「MCP 服务」页添加配置条目。

参考：[references/mcp-setup.md](./references/mcp-setup.md)

## 五、部署约定（通用）

- 插件目录：`.cortana/plugins/<kebab-name>/`
- 开发期：发布脚本自动部署到 `Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins\`
- 运行时：`{WorkspaceDirectory}\.cortana\plugins\`，宿主自动扫描加载
- 目录内**只保留**运行文件（`.dll` / `.exe` / `.json` / 运行时依赖）；**不要**带 `.pdb` / `.xml` / `.deps.json` 以外的开发文件

## 六、参考映射（想做 X → 看 Y）

| 想做 | 看 |
|---|---|
| C# 原生 DLL 插件 | [references/csharp-native.md](./references/csharp-native.md) |
| C# EXE 子进程插件 | 使用 `scripts/create-process-plugin.ps1` 生成框架工程 |
| Python / Node / Go / Rust 子进程插件 | [references/process-protocol.md](./references/process-protocol.md) |
| MCP 接入 | [references/mcp-setup.md](./references/mcp-setup.md) |
| AOT 编译错误 | [references/csharp-aot-errors.md](./references/csharp-aot-errors.md) |
| C/C++/Rust/Go 原生 DLL | [references/native-abi.md](./references/native-abi.md) |

## 更新规则

每次修改此技能或其 references / scripts，**将 `version` 字段 +1**。
