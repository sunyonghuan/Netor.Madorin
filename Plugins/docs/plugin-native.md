# Native 原生插件开发指南

> 通道类型：`native` | 隔离级别：进程级（子进程隔离）

> 当前状态：推荐通道。对于新的本地插件开发，默认优先选择 Native；只有明确存在历史兼容需求时才考虑 Dotnet 通道。

## 概述

Native 通道支持使用 C/C++、Rust 或 C# AOT 编写原生 DLL 插件。插件 DLL 在独立的 `Cortana.NativeHost.exe` 子进程中通过 `NativeLibrary.Load` 加载，与宿主之间通过 stdin/stdout JSON 协议通信。

**核心设计理念：原生 DLL 崩溃零容忍** — 即使原生代码发生段错误或非法内存访问，只会导致子进程退出，宿主进程完全不受影响。

### 适用场景

- C/C++/Rust 编写的高性能计算库
- 需要调用系统原生 API 的场景
- 第三方原生 SDK 集成
- 需要强隔离保证的不可信代码

### 架构流程

```
Cortana 宿主进程                          Cortana.NativeHost.exe 子进程
┌─────────────────┐     stdin/stdout     ┌──────────────────────────┐
│  NativePluginHost│◄──── JSON 协议 ────►│  NativeLibrary.Load()    │
│                  │                     │  ├── cortana_plugin_*()  │
│  NativePlugin    │                     │  └── MyNativeLib.dll     │
│   Wrapper        │                     └──────────────────────────┘
        崩溃只影响此进程
```

## 导出函数规范

原生 DLL 必须导出以下 C ABI 函数：

### 必需导出

| 函数 | 签名 | 说明 |
|------|------|------|
| `cortana_plugin_get_info` | `char* ()` | 返回插件信息 JSON（UTF-8），由宿主调用 `cortana_plugin_free` 释放 |
| `cortana_plugin_invoke` | `char* (char* toolName, char* argsJson)` | 调用工具，返回结果字符串，由宿主释放 |
| `cortana_plugin_free` | `void (char* ptr)` | 释放由 `get_info` / `invoke` 返回的字符串内存 |

### 可选导出

| 函数 | 签名 | 说明 |
|------|------|------|
| `cortana_plugin_init` | `int (char* configJson)` | 初始化插件，返回非 0 表示成功 |
| `cortana_plugin_destroy` | `void ()` | 清理资源（进程退出前调用） |

### get_info 返回的 JSON 格式

```json
{
  "id": "com.example.my-native",
  "name": "我的原生插件",
  "version": "1.0.0",
  "description": "插件描述",
  "instructions": "AI 系统指令（可选）",
  "tags": ["工具", "原生"],
  "tools": [
    {
      "name": "my_tool",
      "description": "工具描述",
      "parameters": [
        {
          "name": "input",
          "type": "string",
          "description": "输入参数",
          "required": true
        },
        {
          "name": "count",
          "type": "integer",
          "description": "数量",
          "required": false
        }
      ]
    }
  ]
}
```

#### 参数类型

支持的 `type` 值：`string` / `number` / `integer` / `boolean` / `array` / `object`

## 通信协议

宿主与 NativeHost 子进程通过 stdin/stdout 交换单行 JSON 消息：

### 请求（宿主 → 子进程）

```json
{ "method": "get_info" }
{ "method": "init", "args": "{\"dataDirectory\":\"...\",\"workspaceDirectory\":\"...\"}" }
{ "method": "invoke", "toolName": "my_tool", "args": "{\"input\":\"hello\"}" }
{ "method": "destroy" }
```

### 响应（子进程 → 宿主）

```json
{ "success": true, "data": "结果内容或 JSON 字符串" }
{ "success": false, "error": "错误描述" }
```

### 生命周期

```
1. 宿主启动 NativeHost.exe <library-path>
2. NativeHost 加载 DLL，绑定导出函数
3. 宿主发送 get_info → 获取插件信息和工具列表
4. 宿主发送 init → 传入配置（dataDirectory 等）
5. 宿主发送 invoke → 调用工具（可多次）
6. 宿主发送 destroy → 清理资源
7. 子进程退出
```

## C# AOT 开发示例

使用 C# 的 `PublishAot` 编写原生插件是最便捷的方式：

### 1. 创建项目

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <PublishAot>true</PublishAot>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>
```

### 2. 实现导出函数

```csharp
using System.Runtime.InteropServices;
using System.Text.Json;

public static class PluginExports
{
    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_get_info")]
    public static IntPtr GetInfo()
    {
        var json = """
        {
            "id": "com.example.my-native",
            "name": "我的原生插件",
            "version": "1.0.0",
            "tools": [
                {
                    "name": "hello",
                    "description": "打招呼",
                    "parameters": [
                        { "name": "name", "type": "string", "description": "名字", "required": true }
                    ]
                }
            ]
        }
        """;
        return Marshal.StringToCoTaskMemUTF8(json);
    }

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_invoke")]
    public static IntPtr Invoke(IntPtr toolNamePtr, IntPtr argsJsonPtr)
    {
        var toolName = Marshal.PtrToStringUTF8(toolNamePtr) ?? "";
        var argsJson = Marshal.PtrToStringUTF8(argsJsonPtr) ?? "{}";

        var result = toolName switch
        {
            "hello" => HandleHello(argsJson),
            _ => $"未知工具: {toolName}"
        };

        return Marshal.StringToCoTaskMemUTF8(result);
    }

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_free")]
    public static void Free(IntPtr ptr)
    {
        Marshal.FreeCoTaskMem(ptr);
    }

    private static string HandleHello(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var name = doc.RootElement.GetProperty("name").GetString();
        return $"你好，{name}！";
    }
}
```

### 3. 创建 plugin.json

```json
{
  "id": "com.example.my-native",
  "name": "我的原生插件",
  "version": "1.0.0",
  "runtime": "native",
  "libraryName": "MyNativePlugin.dll"
}
```

#### plugin.json 字段说明

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `id` | string | ✅ | 唯一标识 |
| `name` | string | ✅ | 显示名称 |
| `version` | string | ✅ | 语义版本号 |
| `runtime` | string | ✅ | 固定为 `"native"` |
| `libraryName` | string | ✅ | 原生 DLL 文件名 |
| `minHostVersion` | string | 否 | 最低宿主版本 |

### 4. AOT 发布与部署

```bash
dotnet publish -c Release -r win-x64
```

将 `publish/` 目录中的 DLL 和 `plugin.json` 复制到插件目录：

```
.cortana/plugins/my-native-plugin/
├── plugin.json
└── MyNativePlugin.dll     # AOT 原生 DLL（不含 .NET 运行时依赖）
```

## C/C++ 开发示例

```c
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

// 导出声明（Windows）
#define EXPORT __declspec(dllexport)

EXPORT const char* cortana_plugin_get_info(void) {
    // 返回堆分配的 JSON 字符串
    const char* json = "{\"id\":\"com.example.native-c\","
                       "\"name\":\"C 原生插件\","
                       "\"version\":\"1.0.0\","
                       "\"tools\":[{\"name\":\"ping\","
                       "\"description\":\"测试连通性\","
                       "\"parameters\":[]}]}";
    char* result = (char*)malloc(strlen(json) + 1);
    strcpy(result, json);
    return result;
}

EXPORT const char* cortana_plugin_invoke(const char* toolName, const char* argsJson) {
    char* result = (char*)malloc(256);
    snprintf(result, 256, "工具 %s 调用成功", toolName);
    return result;
}

EXPORT void cortana_plugin_free(char* ptr) {
    free(ptr);
}
```

## 注意事项

1. **内存管理** — `get_info` 和 `invoke` 返回的字符串必须通过 `cortana_plugin_free` 释放，确保分配器匹配
2. **UTF-8 编码** — 所有字符串交换使用 UTF-8 编码
3. **线程安全** — 宿主保证同一插件的调用是串行的（通过 `SemaphoreSlim`）
4. **超时机制** — 工具调用超时时间为 30 秒，超时后宿主记录警告
5. **崩溃恢复** — 子进程崩溃后，宿主标记插件为不可用，`IsProcessAlive` 返回 `false`

## 完整示例

参见 `Samples/NativeTestPlugin/` 目录，包含一个完整的 C# AOT 原生插件，实现了 3 个工具：

| 工具 | 说明 |
|------|------|
| `echo_message` | 回显消息（测试基本通信） |
| `math_add` | 两数相加（测试参数解析） |
| `random_quote` | 随机名言（测试无参调用） |




