# Cortana Native AOT 插件开发指南

> **框架版本**：1.0.3+ &nbsp;|&nbsp; **.NET 10** &nbsp;|&nbsp; **Windows x64**

> 当前状态：推荐的新插件开发路线。与旧 Dotnet 托管插件相比，Native 通道是当前默认主线。

---

## 目录

1. [环境准备](#1-环境准备)
2. [创建项目](#2-创建项目)
3. [编写插件入口](#3-编写插件入口)
4. [编写工具类](#4-编写工具类)
5. [参数类型与返回类型](#5-参数类型与返回类型)
6. [依赖注入](#6-依赖注入)
7. [返回自定义对象](#7-返回自定义对象)
8. [构建与发布](#8-构建与发布)
9. [编译错误速查表](#9-编译错误速查表)
10. [常见问题](#10-常见问题)

---

## 1. 环境准备

**必需：**
- .NET 10 SDK
- Visual Studio 2022 17.12+（需安装「.NET 桌面开发」和「使用 C++ 的桌面开发」工作负载）

> ⚠️ 没装 C++ 工作负载会导致 AOT 发布失败，报 `NETSDK1099`。

**安装 NuGet 包：**

优先使用项目当前发布的内部 NuGet 源；如果团队另有包源约定，以团队源为准。命令行示例：

```bash
dotnet nuget add source http://nuget.netor.me/v3/index.json -n netor
dotnet add package Netor.Cortana.Plugin.Native
dotnet add package Netor.Cortana.Plugin.Native.Generator
```

> 如果本机已经配置好内部源，上述第一条可跳过。

---

## 2. 创建项目

```bash
dotnet new classlib -n MyPlugin -f net10.0
cd MyPlugin
```

编辑 `MyPlugin.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <OutputType>Library</OutputType>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.3" />
    <PackageReference Include="Netor.Cortana.Plugin.Native.Generator" Version="1.0.3" />
  </ItemGroup>

</Project>
```

> ⚠️ **以下配置缺一不可，否则无法正常工作：**
> - `PublishAot` = `true`
> - `OutputType` = `Library`（不是 Exe）
> - `RuntimeIdentifier` = 具体平台（如 `win-x64`）

---

## 3. 编写插件入口

创建 `Startup.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace MyPlugin;

[Plugin(
    Id = "my_plugin",
    Name = "我的插件",
    Version = "1.0.0",
    Description = "插件功能描述")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        // 注册你自己的服务（工具类会被 Generator 自动注册，不需要写在这里）
    }
}
```

**规则：**

| 要求 | 错误时报 |
|---|---|
| 必须是 `public static partial class` | CNPG011 |
| 必须有 `Configure(IServiceCollection)` 方法 | CNPG012 |
| 整个项目只能有一个 `[Plugin]` 类 | CNPG010 |
| `Id` 和 `Name` 必填 | CNPG009 |
| `Id` 只允许小写字母、数字、下划线 | CNPG019 |

**Id 示例：** ✅ `weather_query`、`my_tool_v2` &nbsp;&nbsp; ❌ `MyTool`、`my-tool`、`工具`

---

## 4. 编写工具类

每个工具类代表一组相关的工具方法。创建 `EchoTools.cs`：

```csharp
using Netor.Cortana.Plugin.Native;

namespace MyPlugin;

[Tool(Description = "回声工具集")]
public class EchoTools
{
    [Tool(Description = "将输入的文字原样返回")]
    public string Echo(
        [Parameter(Description = "要回声的文字")] string message)
    {
        return $"Echo: {message}";
    }

    [Tool(Name = "echo_upper", Description = "将文字转为大写后返回")]
    public string EchoUpper(
        [Parameter(Description = "要转换的文字")] string message)
    {
        return message.ToUpper();
    }
}
```

**关键规则：**

| 要求 | 说明 | 错误码 |
|---|---|---|
| 类必须标记 `[Tool]` | 未标记的类会被忽略，不会生成工具 | — |
| 类必须是 `public` | 非 public 类报错 | CNPG005 |
| 方法必须是 `public` | 非 public 方法报错 | CNPG006 |
| 方法必须标记 `[Tool]` | 未标记的方法会被跳过 | — |

**工具命名规则：**

- 自动命名：`{PluginId}_{方法名转snake_case}`
  - 例：Plugin Id = `my_plugin`，方法名 = `EchoUpper` → 工具名 = `my_plugin_echo_upper`
- 手动命名：通过 `[Tool(Name = "echo_upper")]` 覆盖方法名部分
- 如果方法名本身已经是 snake_case，会收到 `CNPG007` 警告（建议用 PascalCase）
- 工具名冲突（同名）会报 `CNPG004`

> ⚠️ `[Tool]` 需要同时标记在**类**和**方法**上。只标类不标方法 = 没有工具；只标方法不标类 = 类被忽略。

---

## 5. 参数类型与返回类型

### 支持的参数类型

| C# 类型 | JSON 传入示例 |
|---|---|
| `string` | `"hello"` |
| `int` | `42` |
| `long` | `100000` |
| `double` | `3.14` |
| `float` | `1.5` |
| `decimal` | `99.99` |
| `bool` | `true` |

> ⚠️ 不支持数组、集合、自定义类型作为参数。使用不支持的类型会报 `CNPG003`。

### `[Parameter]` 特性

```csharp
[Tool(Description = "加法")]
public double Add(
    [Parameter(Description = "第一个数")] double a,
    [Parameter(Description = "第二个数", Required = false)] double b)
{
    return a + b;
}
```

- `Required` 默认为 `true`
- 如果参数类型是 nullable（如 `double?`），自动推断为 `Required = false`
- `Description` 会出现在工具的 JSON Schema 中，建议填写

### 支持的返回类型

| 返回类型 | 处理方式 |
|---|---|
| `string` | 直接返回 |
| `int` / `long` / `double` / `float` / `decimal` / `bool` | `.ToString()` 后返回 |
| `void` | 返回固定字符串 `"ok"` |
| `string[]` / `int[]` / `double[]` 等基础数组 | 手动拼 JSON 数组 |
| 自定义类 / record | 需要 `PluginJsonContext`（见第7章）|

### 异步方法

支持 `Task<T>` / `ValueTask<T>` / `Task` / `ValueTask` 返回类型：

```csharp
[Tool(Description = "异步获取数据")]
public async Task<string> FetchDataAsync(
    [Parameter(Description = "URL")] string url)
{
    using var http = new HttpClient();
    return await http.GetStringAsync(url);
}
```

> 框架内部使用 `.GetAwaiter().GetResult()` 同步等待，因此方法签名可以是 async，但实际是同步调用。

---

## 6. 依赖注入

### 自动注册（无需手动操作）

以下服务由 Generator 自动注册，**不要**手动在 `Configure` 中重复注册：

| 服务 | 生命周期 | 说明 |
|---|---|---|
| 所有 `[Tool]` 类 | Singleton | 如 `EchoTools`、`MathTools` |
| `PluginSettings` | Singleton | 包含 `DataDirectory`、`WorkspaceDirectory`、`WsPort` |

### 使用 PluginSettings

通过构造函数注入获取插件运行时信息：

```csharp
[Tool(Description = "回声工具")]
public class EchoTools
{
    private readonly PluginSettings _settings;

    public EchoTools(PluginSettings settings)
    {
        _settings = settings;
    }

    [Tool(Description = "获取数据目录")]
    public string GetDataDir()
    {
        return _settings.DataDirectory;
    }
}
```

`PluginSettings` 属性：

| 属性 | 类型 | 说明 |
|---|---|---|
| `DataDirectory` | `string` | 插件数据目录 |
| `WorkspaceDirectory` | `string` | 工作区目录 |
| `WsPort` | `int` | WebSocket 端口 |

### 注册自定义服务

在 `Startup.Configure` 中注册你自己的服务：

```csharp
public static void Configure(IServiceCollection services)
{
    services.AddSingleton<QuoteRepository>();
}
```

然后在工具类中通过构造函数注入使用：

```csharp
[Tool(Description = "名言工具")]
public class QuoteTools
{
    private readonly QuoteRepository _repo;

    public QuoteTools(QuoteRepository repo)
    {
        _repo = repo;
    }

    [Tool(Description = "获取随机名言")]
    public string GetRandomQuote()
    {
        return _repo.GetRandom();
    }
}
```

---

## 7. 返回自定义对象

如果工具方法需要返回自定义类或 record，必须创建一个 `PluginJsonContext` 类来支持 AOT 序列化。

### 步骤一：定义返回类型

```csharp
public record WeatherInfo(string City, double Temperature, string Description);
```

### 步骤二：创建 PluginJsonContext

在项目中创建 `PluginJsonContext.cs`：

```csharp
using System.Text.Json.Serialization;

namespace MyPlugin;

[JsonSerializable(typeof(WeatherInfo))]
public partial class PluginJsonContext : JsonSerializerContext
{
}
```

### 步骤三：在工具方法中返回

```csharp
[Tool(Description = "查询天气")]
public WeatherInfo GetWeather(
    [Parameter(Description = "城市名")] string city)
{
    return new WeatherInfo(city, 25.5, "晴");
}
```

**关键规则：**

- 类名必须叫 `PluginJsonContext`，不能改名
- 必须继承 `JsonSerializerContext`
- 必须标记 `partial`
- 每个自定义返回类型都要加一个 `[JsonSerializable(typeof(...))]`
- 如果返回自定义类型但没有创建 `PluginJsonContext`，会报 `CNPG020`

> ⚠️ 基础类型（string、int 等）和基础数组（string[]、int[] 等）**不需要** PluginJsonContext，只有自定义类/record 才需要。

---

## 8. 构建与发布

### 日常开发：构建

```bash
dotnet build
```

构建时 Generator 会自动生成代码，可以在 IDE 中实时看到编译错误和警告。

### 发布 AOT 原生库

```bash
dotnet publish -c Release
```

发布成功后，输出目录 `bin/Release/net10.0/win-x64/publish/` 中会包含：

| 文件 | 说明 |
|---|---|
| `MyPlugin.dll` | 原生 AOT 编译的动态链接库（主文件） |
| `plugin.json` | 插件描述文件（包含工具列表、参数信息） |

> 这两个文件就是你交付给 Cortana 加载的完整插件。

### plugin.json 示例

```json
{
  "id": "my_plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "description": "插件功能描述",
  "tools": [
    {
      "name": "my_plugin_echo",
      "description": "将输入的文字原样返回",
      "parameters": {
        "type": "object",
        "properties": {
          "message": {
            "type": "string",
            "description": "要回声的文字"
          }
        },
        "required": ["message"]
      }
    }
  ]
}
```

> `plugin.json` 由 Generator 在编译时自动生成，不需要手动编写。

---

## 9. 编译错误速查表

| 错误码 | 级别 | 原因 | 解决方法 |
|---|---|---|---|
| CNPG003 | Error | 参数类型不受支持 | 改为 string/int/long/double/float/decimal/bool |
| CNPG004 | Error | 工具名冲突（两个方法生成了同名工具） | 用 `[Tool(Name = "...")]` 手动指定不同名称 |
| CNPG005 | Error | 工具类不是 public | 给类加上 `public` 修饰符 |
| CNPG006 | Error | 工具方法不是 public | 给方法加上 `public` 修饰符 |
| CNPG007 | Warning | 方法名已经是 snake_case | 建议改为 PascalCase，Generator 会自动转换 |
| CNPG008 | Error | 工具名包含非法字符 | 名称只允许小写字母、数字、下划线 |
| CNPG009 | Error | `[Plugin]` 缺少 Id 或 Name | 补全必填属性 |
| CNPG010 | Error | 项目中存在多个 `[Plugin]` 类 | 整个项目只能有一个 Startup 类 |
| CNPG011 | Error | Startup 类不是 `public static partial` | 同时加上三个修饰符 |
| CNPG012 | Error | Startup 类缺少 `Configure` 方法 | 添加 `public static void Configure(IServiceCollection)` |
| CNPG019 | Error | Plugin Id 格式非法 | 只用小写字母、数字、下划线（如 `my_plugin`） |
| CNPG020 | Error | 返回自定义类型但缺少 PluginJsonContext | 创建 `PluginJsonContext` 类（见第7章） |

---

## 10. 常见问题

### Q: 发布时报 `NETSDK1099` 错误

**原因：** 没有安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载。

**解决：** 打开 Visual Studio Installer，勾选「使用 C++ 的桌面开发」，安装后重试。

---

### Q: 修改了代码但生成的代码没有更新

**原因：** IDE 缓存了旧的 Generator 输出。

**解决：**
```bash
dotnet clean
dotnet build
```

如果还不行，删除 `bin` 和 `obj` 目录后重新构建。

---

### Q: 编译警告 CS8669（可空引用注释）

**原因：** Generator 生成的代码与你的 nullable 设置有细微差异。

**解决：** 可以安全忽略，不影响功能。如果想消除，在 csproj 中添加：

```xml
<NoWarn>$(NoWarn);CS8669</NoWarn>
```

---

### Q: 发布时报 `PublishSingleFile` 相关错误

**原因：** AOT 发布不能同时设置 `SelfContained`。

**解决：** 不要手动添加 `<SelfContained>true</SelfContained>`，`PublishAot` 会自动处理。

---

### Q: 生成的工具名不符合预期

**原因：** 自动命名是 `{PluginId}_{snake_case方法名}`。

**解决：** 用 `[Tool(Name = "my_custom_name")]` 手动指定工具名的方法名部分（不含 Plugin Id 前缀）。

---

### Q: 如何查看 Generator 生成的代码？

在 csproj 中添加（项目模板已默认包含）：

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
```

生成的代码会出现在 `obj/Debug/net10.0/generated/` 目录下。

---

> 📖 **更多问题？** 查看 Generator 源码中的 `DiagnosticDescriptors.cs` 获取完整的错误码列表。
