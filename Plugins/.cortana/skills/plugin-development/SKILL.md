---
name: plugin-development
description: 'Cortana 插件开发全流程技能。创建、编写、发布、部署 Cortana 插件。支持两种插件类型：Native（AOT 进程隔离）和 Dotnet（进程内 ALC 隔离）。触发关键词：插件开发、创建插件、发布插件、Native 插件、Dotnet 插件、AOT 发布。'
user-invocable: true
---

# Plugin Development

Cortana 插件开发全流程。

## 插件类型选择

| | Native 插件 | Dotnet 插件 |
|---|---|---|
| 隔离级别 | 进程级（子进程） | 进程内（ALC） |
| 编译方式 | **AOT 原生编译** | 普通 IL |
| 崩溃影响 | 不影响宿主 | 可能影响宿主 |
| NuGet 包 | `Netor.Cortana.Plugin.Native` + `.Generator` | `Netor.Cortana.Plugin.Abstractions`（项目引用） |
| plugin.json | **Generator 自动生成** | 手动编写 |
| DI 支持 | 通过 `Startup.Configure` | 无（自行管理） |
| 适用场景 | 独立工具、高隔离需求 | 需要完整 .NET 生态、I/O 密集型 |

**不确定选哪个？选 Native。** 隔离性更好，开发更简单。

---

## 流程总览

```
0. 环境检测 → 1. 创建项目 → 2. 编写代码 → 3. 构建验证 → 4. 发布部署
```

---

## 零、环境准备

> **开始开发前，必须先运行环境检测脚本。** 脚本会自动检测并安装缺失的组件。

运行 [setup-dev-environment.ps1](./scripts/setup-dev-environment.ps1)：

```powershell
.\skills\plugin-development\scripts\setup-dev-environment.ps1
```

脚本自动完成：

| 检测项 | 缺失时的处理 |
|---|---|
| .NET 10+ SDK | 通过 `dotnet-install.ps1` 自动安装最新稳定版 |
| C++ 构建工具（AOT 必需） | 下载并安装 VS Build Tools（仅 C++ 桌面组件，不装 VS/VSCode） |

如果只开发 **Dotnet 托管插件**（不需要 AOT），可跳过 C++ 工具链检测：

```powershell
.\skills\plugin-development\scripts\setup-dev-environment.ps1 -SkipAot
```

---

## 一、创建 Native 插件

### 1.1 脚手架

运行 [create-native-plugin.ps1](./scripts/create-native-plugin.ps1)：

```powershell
.\skills\plugin-development\scripts\create-native-plugin.ps1 -Name MyPlugin -Id my_plugin
```

生成结构：

```
Samples/MyPlugin/
├── MyPlugin.csproj
├── Startup.cs
└── MyTools.cs
```

### 1.2 csproj 必需配置

```xml
<PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <OutputType>Library</OutputType>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.3" />
    <PackageReference Include="Netor.Cortana.Plugin.Native.Generator" Version="1.0.3" />
</ItemGroup>
```

> ⚠️ **`PublishAot`、`OutputType=Library`、`RuntimeIdentifier` 三项缺一不可。**

### 1.3 编写 Startup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace MyPlugin;

[Plugin(Id = "my_plugin", Name = "我的插件", Version = "1.0.0",
        Description = "插件描述",
        Instructions = "告诉 AI 何时使用这些工具")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        // 注册自定义服务（工具类由 Generator 自动注册）
    }
}
```

**规则：**
- 必须 `public static partial class`
- 必须有 `Configure(IServiceCollection)` 方法
- 整个项目只能有一个 `[Plugin]` 类
- `Id` 只允许**小写字母、数字、下划线**（如 `my_plugin`，不能用 `-` 或大写）

### 1.4 编写工具类

```csharp
using Netor.Cortana.Plugin.Native;

namespace MyPlugin;

[Tool]
public class MyTools
{
    [Tool(Name = "do_something", Description = "工具描述")]
    public string DoSomething(
        [Parameter(Description = "参数说明")] string input)
    {
        return $"结果: {input}";
    }
}
```

**规则：**
- `[Tool]` 必须同时标记在**类**和**方法**上
- 类和方法都必须 `public`
- 支持参数类型：`string`、`int`、`long`、`double`、`float`、`decimal`、`bool`
- **不支持数组、集合、自定义类型作为参数**
- 工具自动命名：`{PluginId}_{snake_case方法名}`
- 手动覆盖方法名部分：`[Tool(Name = "custom_name")]`

### 1.5 返回自定义对象

如果工具需要返回自定义类/record，**必须**创建 `PluginJsonContext`：

```csharp
using System.Text.Json.Serialization;

namespace MyPlugin;

[JsonSerializable(typeof(MyResult))]
public partial class PluginJsonContext : JsonSerializerContext { }
```

> 基础类型（string、int 等）和基础数组不需要。

### 1.6 依赖注入

| 自动注册（不要手动重复） | 手动注册（在 Configure 中） |
|---|---|
| 所有 `[Tool]` 类 | 你自己的服务类 |
| `PluginSettings`（DataDirectory、WorkspaceDirectory、WsPort） | |

```csharp
[Tool]
public class MyTools
{
    private readonly PluginSettings _settings;
    public MyTools(PluginSettings settings) => _settings = settings;
}
```

---

## 二、创建 Dotnet 插件

### 2.1 脚手架

运行 [create-dotnet-plugin.ps1](./scripts/create-dotnet-plugin.ps1)：

```powershell
.\skills\plugin-development\scripts\create-dotnet-plugin.ps1 -Name MyPlugin -Id com.example.my-plugin
```

### 2.2 实现 IPlugin

```csharp
using Microsoft.Extensions.AI;
using Netor.Cortana.Plugin.Abstractions;

public sealed class MyPlugin : IPlugin
{
    private readonly List<AITool> _tools = [];

    public string Id => "com.example.my-plugin";
    public string Name => "我的插件";
    public Version Version => new(1, 0, 0);
    public string Description => "描述";
    public IReadOnlyList<string> Tags => ["标签"];
    public IReadOnlyList<AITool> Tools => _tools;
    public string? Instructions => "告诉 AI 何时使用工具";

    public Task InitializeAsync(IPluginContext context)
    {
        _tools.Add(AIFunctionFactory.Create(
            MyMethod, "tool_name", "工具描述"));
        return Task.CompletedTask;
    }

    private string MyMethod(string input, int count = 5)
    {
        return $"结果: {input}, 数量: {count}";
    }
}
```

### 2.3 手动编写 plugin.json

```json
{
  "id": "com.example.my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "description": "描述",
  "runtime": "dotnet",
  "assemblyName": "MyPlugin.dll",
  "minHostVersion": "1.0.0"
}
```

---

## 三、AOT 发布（重点）

### ⚠️ AOT 发布前置条件

> **需要 .NET 10+ SDK 和 C++ 构建工具（MSVC 链接器）。**
> 如果没装，运行环境检测脚本自动安装：
> ```powershell
> .\skills\plugin-development\scripts\setup-dev-environment.ps1
> ```
> 不需要安装完整的 Visual Studio 或 VS Code。

### 3.1 Native 插件发布

运行 [publish-native-plugin.ps1](./scripts/publish-native-plugin.ps1)：

```powershell
.\skills\plugin-development\scripts\publish-native-plugin.ps1 -ProjectDir Samples\MyPlugin
```

等效手动命令：

```powershell
dotnet publish MyPlugin.csproj -c Release -r win-x64
```

产出文件（`bin/Release/net10.0/win-x64/publish/`）：

| 文件 | 说明 |
|---|---|
| `MyPlugin.dll` | AOT 原生 DLL |
| `plugin.json` | Generator 自动生成的插件描述 |

**只需这两个文件。** 部署到 `.cortana/plugins/{插件名}/` 即可。

### 3.2 Dotnet 插件发布

运行 [publish-dotnet-plugin.ps1](./scripts/publish-dotnet-plugin.ps1)：

```powershell
.\skills\plugin-development\scripts\publish-dotnet-plugin.ps1 -ProjectDir Samples\SamplePlugins
```

等效手动命令：

```powershell
dotnet publish MyPlugin.csproj -c Release
```

**Dotnet 插件不需要 AOT。** 需要部署 `.dll` + `.deps.json` + `plugin.json` + 私有依赖。

> ⚠️ **不要**携带宿主共享程序集的副本（Abstractions、Microsoft.Extensions.AI.Abstractions 等），否则可能导致类型不匹配。

### 3.3 部署说明

**开发环境（IDE 调试）：**

发布脚本自动部署到 Cortana 的 Debug 输出目录：

```
Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins\{插件名}\
```

可通过 `-PluginRoot` 参数自定义部署位置。

**运行时环境（用户使用）：**

软件运行时，插件目录为 `{WorkspaceDirectory}\.cortana\plugins\`，由 Cortana 自行管理。

### 3.4 插件目录结构

```
.cortana/plugins/
├── my-native-plugin/       # Native 插件（只需 2 个文件）
│   ├── plugin.json
│   └── MyPlugin.dll
└── my-dotnet-plugin/       # Dotnet 插件
    ├── plugin.json
    ├── MyPlugin.dll
    ├── MyPlugin.deps.json
    └── SomeDependency.dll   # 私有依赖（不含宿主共享程序集）
```

> ⚠️ 插件目录只保留干净的运行文件（`.dll`、`.json`、`.deps.json`）。
> **不要**包含 `.xml`、`.pdb`、`.xml` 等开发文件。

宿主启动时自动扫描加载。

---

## 四、AOT 编译错误速查

| 错误 | 原因 | 解决 |
|---|---|---|
| `NETSDK1099` | **没装 C++ 工作负载** | VS Installer → 勾选「使用 C++ 的桌面开发」 |
| `CNPG003` | 参数类型不支持 | 改为 string/int/long/double/float/decimal/bool |
| `CNPG004` | 工具名冲突 | 用 `[Tool(Name = "...")]` 指定不同名 |
| `CNPG005/006` | 类或方法不是 public | 加 `public` |
| `CNPG009` | Plugin 缺 Id 或 Name | 补全 |
| `CNPG010` | 多个 `[Plugin]` 类 | 只留一个 |
| `CNPG011` | Startup 不是 `public static partial` | 三个修饰符都加上 |
| `CNPG012` | 缺少 `Configure` 方法 | 添加 `Configure(IServiceCollection)` |
| `CNPG019` | Id 格式非法 | 只用小写字母、数字、下划线 |
| `CNPG020` | 返回自定义类型没有 PluginJsonContext | 创建 `PluginJsonContext` 类 |
| `PublishSingleFile` 错误 | AOT 不能同时设 SelfContained | 删掉 `<SelfContained>` |

### Generator 代码不更新？

```powershell
dotnet clean; dotnet build
```

还不行就删 `bin/` 和 `obj/` 重来。

---

## 五、参考示例

| 示例 | 类型 | 位置 |
|---|---|---|
| NativeTestPlugin | Native AOT | `Samples/NativeTestPlugin/` |
| SamplePlugins | Dotnet | `Samples/SamplePlugins/` |
