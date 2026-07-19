# Native 插件开发指南

Native 插件包由 `plugin.json` 和原生 DLL 组成。宿主根据 `libraryName` 加载 DLL，并按名称查找导出函数。

## 一、导出入口

下列签名使用 C 形式表达 ABI；字符串参数和返回值均为以 NUL 结尾的 UTF-8 字符串指针。

```c
void* cortana_plugin_get_info(void);
int   cortana_plugin_init(const char* config_json);
void* cortana_plugin_invoke(const char* tool_name, const char* args_json);
void  cortana_plugin_free(void* ptr);
void  cortana_plugin_destroy(void);
```

| 导出 | 要求 | 行为 |
| --- | --- | --- |
| `cortana_plugin_get_info` | 必需 | 返回插件与工具元数据 JSON 字符串指针 |
| `cortana_plugin_init` | 可选 | 接收运行时配置 JSON；非 0 成功，0 失败 |
| `cortana_plugin_invoke` | 必需 | 接收完整工具名和参数 JSON，返回结果字符串指针 |
| `cortana_plugin_free` | 必需 | 释放 `get_info` / `invoke` 返回的指针 |
| `cortana_plugin_destroy` | 可选 | 停止后台服务并释放插件状态 |

当前宿主使用平台默认的非托管调用约定。插件的目标架构必须与宿主一致，当前发布主线为 Windows x64。

## 二、字符串和内存

- 宿主传入的 `config_json`、`tool_name`、`args_json` 只在当前调用期间有效，插件需要长期保存时必须复制。
- `get_info` 和 `invoke` 返回的指针必须指向有效的 NUL 结尾 UTF-8 字符串。
- 宿主读取返回值后会调用插件自己的 `cortana_plugin_free`；不要要求宿主使用另一套分配器释放。
- 返回空指针会被视为失败。
- `invoke` 的返回值是工具结果字符串，不再套一层 Native ABI 响应对象。结构化结果应返回 JSON 字符串。

## 三、生命周期

```text
加载 DLL
  -> 绑定必需/可选导出
  -> get_info
  -> init（已导出时）
  -> invoke（零次或多次）
  -> destroy（已导出时）
  -> 卸载 DLL
```

`init` 接收的 JSON 与 Process `init.args` 内层对象一致，包含 `dataDirectory`、`workspaceDirectory`、`pluginDirectory`、`wsPort` 和 `extensions`。

`get_info` 至少应返回插件基础字段和 `tools`。`runtime`、`libraryName`、`publishedOps`、`subscribedOps` 属于 `plugin.json` 静态清单；当前 `get_info` 不返回这些字段。

## 四、plugin.json

最小 Native 清单：

```json
{
  "id": "your_native_plugin",
  "name": "Your Native Plugin",
  "version": "1.0.0",
  "description": "Native plugin example",
  "runtime": "native",
  "libraryName": "YourNativePlugin.dll",
  "minHostVersion": "1.0.0"
}
```

使用非 C# 工具链时，由构建流程生成或维护该文件；使用官方 C# SDK 时由 Generator 生成，不要再维护手写副本。

## 五、C# 示例（使用官方 SDK）

```powershell
.\skills\plugin-development\scripts\create-native-plugin.ps1 `
  -Name YourNativePlugin `
  -Id your_native_plugin
```

项目引用 `Netor.Cortana.Plugin.Native`。该包自动带入 Generator，生成 5 个导出入口、工具路由、JSON 上下文适配和 `plugin.json`。

### 插件入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace YourNativePlugin;

[Plugin(
    Id = "your_native_plugin",
    Name = "Your Native Plugin",
    Version = "1.0.0",
    Description = "提供示例 Native 工具。")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<SampleService>();
    }
}
```

### 工具

```csharp
[Tool]
public sealed class SampleTools(SampleService service)
{
    [Tool(Name = "echo", Description = "回显输入文本。")]
    public string Echo(
        [Parameter(Name = "text", Description = "要回显的文本。")] string text)
        => service.Echo(text);
}
```

Generator 会生成完整工具名 `your_native_plugin_echo`。工具参数和返回值限制见 [工具声明与参数规范.md](工具声明与参数规范.md)。

## 六、C# 调试

脚手架会生成独立的 Debug Console 项目。它引用开发态插件程序集和 `Netor.Cortana.Plugin.Native.Debugger`，不直接运行 Debugger 类库本身。

```powershell
dotnet build .\Samples\YourNativePlugin\YourNativePlugin.csproj
dotnet run --project .\Samples\YourNativePlugin\Debug\YourNativePlugin.Debug.csproj
```

REPL 中应列出全部工具，并至少验证每个工具的一条成功路径；有约束的工具还要验证边界或失败路径。Debug Console 验证的是托管开发态逻辑，不等于 Native AOT 发布成功。

## 七、C# 发布

```powershell
.\skills\plugin-development\scripts\publish-native-plugin.ps1 `
  -ProjectDir 'Samples\YourNativePlugin' `
  -SkipDeploy `
  -CreateZip
```

发布前需要安装 Visual Studio“使用 C++ 的桌面开发”工作负载。发布结果应至少包含 `plugin.json` 和目标 Native DLL，并核对 5 个导出入口是否符合预期。

当前共享发布脚本只组装 Native DLL 和 `plugin.json`。需要随包分发模型、模板或其他资源时，必须在生成 zip 前补充资源复制并检查包结构。
