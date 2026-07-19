# Process 插件开发指南

Process 插件由 `plugin.json` 和一个可执行程序组成。宿主启动 `command` 指定的程序，通过标准输入发送请求，通过标准输出接收响应。

## 一、传输规则

- stdin、stdout 使用 UTF-8。
- 一行只包含一个完整 JSON 对象，以换行符结束。
- 插件必须及时刷新 stdout，不能等待缓冲区自动提交。
- stdout 只允许写协议响应；日志写 stderr。
- 空行可以忽略；无法解析的非空行必须返回失败响应。
- 一个请求对应一个响应。当前协议没有请求 ID，宿主按发送顺序关联响应。

## 二、请求

```json
{
  "method": "get_info | init | invoke | destroy",
  "toolName": "仅 invoke 使用",
  "args": "JSON 字符串，仅 init / invoke 使用"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `method` | string | 是 | `get_info`、`init`、`invoke` 或 `destroy` |
| `toolName` | string | `invoke` 时 | `get_info.tools[].name` 中的完整工具名 |
| `args` | string | `init` 时必填 | 内层 JSON 的字符串表示；`invoke` 缺省参数按 `{}` 处理 |

`args` 不是嵌套 JSON 对象，而是 JSON 字符串。实际传输时需要转义内层引号。

### get_info

```json
{"method":"get_info"}
```

### init

```json
{"method":"init","args":"{\"dataDirectory\":\"D:/data\",\"workspaceDirectory\":\"D:/work\",\"pluginDirectory\":\"D:/plugins/demo\",\"wsPort\":9001,\"extensions\":{\"pluginBusEndpoint\":\"ws://localhost:9001/internal\",\"pluginBusProtocol\":\"cortana.plugin-bus\",\"pluginBusVersion\":\"1.4.0\",\"pluginBusPort\":\"9001\"}}"}
```

内层 `init` 对象字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `dataDirectory` | string | 插件专属数据目录 |
| `workspaceDirectory` | string | 初始化时的工作区目录 |
| `pluginDirectory` | string | 插件包根目录 |
| `wsPort` | integer | 宿主 WebSocket 端口 |
| `extensions` | object<string, string> | 宿主扩展键值；当前宿主注入值均为字符串 |

### invoke

```json
{"method":"invoke","toolName":"demo_echo","args":"{\"text\":\"hello\"}"}
```

调用 `invoke` 前必须成功完成 `init`。未知工具、缺少 `toolName`、参数 JSON 无效或工具执行异常均返回失败响应。

### destroy

```json
{"method":"destroy"}
```

插件应先返回成功响应，再停止后台服务、释放资源并退出进程。官方 Process SDK 返回 `data: null` 后结束消息循环。

## 三、响应

```json
{"success":true,"data":"结果字符串或 JSON 字符串","error":null}
```

```json
{"success":false,"data":null,"error":"可读错误信息"}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `success` | boolean | 请求是否成功 |
| `data` | string 或 null | 成功结果；结构化结果仍以 JSON 字符串承载 |
| `error` | string 或 null | 失败原因 |

`get_info` 的 `data` 也是 JSON 字符串，宿主需要再次解析。例如：

```json
{"success":true,"data":"{\"id\":\"demo\",\"name\":\"Demo\",\"version\":\"1.0.0\",\"description\":\"Demo plugin\",\"tags\":[],\"capabilities\":[],\"providedCapabilities\":[],\"requiredHostCapabilities\":[],\"settingsSchema\":[],\"tools\":[{\"name\":\"demo_echo\",\"shortName\":\"echo\",\"description\":\"Echo text\",\"parameters\":[{\"name\":\"text\",\"type\":\"string\",\"description\":\"Input text\",\"required\":true}]}]}"}
```

## 四、生命周期

```text
启动进程
  -> get_info
  -> init
  -> invoke（零次或多次）
  -> destroy
  -> 进程退出
```

- `get_info` 不依赖 `init`。
- `init` 负责建立运行时状态，官方 SDK 会在此阶段构建 DI 容器并启动托管服务。
- `invoke` 可以重复调用；每次调用应隔离临时状态。
- stdin 被关闭或进程被终止时，也必须尽量释放资源。

`plugin.json` 的字段和 `get_info` 的字段并不完全相同，见 [插件清单与能力声明.md](插件清单与能力声明.md)。

## 五、C# 示例（使用官方 SDK）

仓库脚手架会生成入口、工具、JSON 上下文和项目内自测：

```powershell
.\skills\plugin-development\scripts\create-process-plugin.ps1 `
  -Name YourProcessPlugin `
  -Id your_process_plugin
```

项目只需引用 `Netor.Cortana.Plugin.Process`；Generator 和 buildTransitive 目标会随包带入。

### 插件入口

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin;

namespace YourProcessPlugin;

[Plugin(
    Id = "your_process_plugin",
    Name = "Your Process Plugin",
    Version = "1.0.0",
    Description = "提供示例 Process 工具。")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<SampleService>();
    }
}
```

### 程序入口

```csharp
using YourProcessPlugin;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
#if DEBUG
    return await SelfTest.RunAsync();
#else
    Console.Error.WriteLine("Self-test is only available in Debug builds.");
    return 2;
#endif
}

await Startup.RunPluginAsync();
return 0;
```

不要在 C# SDK 项目中自行实现 stdin/stdout 循环、工具路由或 `plugin.json`。

## 六、C# 自测与发布

```powershell
dotnet build .\Samples\YourProcessPlugin\YourProcessPlugin.csproj
dotnet run --project .\Samples\YourProcessPlugin\YourProcessPlugin.csproj -- --self-test
```

自测必须先调用生成调试器的 `InitAsync()`，再逐个调用全部工具；任一失败应返回非 0 退出码。

发布脚本支持 JIT self-contained、framework-dependent 和 AOT exe：

```powershell
.\skills\plugin-development\scripts\publish-process-plugin.ps1 `
  -ProjectDir 'Samples\YourProcessPlugin' `
  -SkipDeploy `
  -CreateZip
```

`-FrameworkDependent` 与 `-Aot` 不能同时使用。不传二者时默认为 win-x64 JIT self-contained。

当前共享发布脚本只把发布目录根级文件放入 staging。插件包含 `models/`、`templates/` 等子目录时，必须在打包前补充资源复制并检查 zip 内容。
