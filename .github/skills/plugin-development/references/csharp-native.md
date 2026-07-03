---
title: C# Native DLL 插件开发规范
version: 2
---

# C# Native DLL 插件

Cortana Native 通道要求**原生 DLL**（C ABI）。C# 走这条路**必须 AOT 发布**——IL DLL 宿主无法加载。

脚手架：[scripts/create-native-plugin.ps1](../scripts/create-native-plugin.ps1)

## 1. csproj 必需配置

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <OutputType>Library</OutputType>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.11" />
  <PackageReference Include="Netor.Cortana.Plugin.Native.Generator" Version="1.0.11"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

四项缺一不可：`PublishAot`、`OutputType=Library`、`RuntimeIdentifier`、`TargetFramework=net10.0`。

## 2. Startup

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
        // 注册自定义服务；工具类由 Generator 自动注册
    }
}
```

- **必须** `public static partial class`
- **必须** 有 `Configure(IServiceCollection)` 方法
- 整个项目只能有一个 `[Plugin]` 类
- `Id` 仅限**小写字母、数字、下划线**，以字母开头

## 3. 工具类

```csharp
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

- `[Tool]` **同时**标在**类**和**方法**上
- 类与方法必须 `public`
- 支持参数类型：`string` `int` `long` `double` `float` `decimal` `bool`
- **不支持**数组、集合、自定义类型作为参数
- 工具自动命名：`{PluginId}_{snake_case方法名}`；用 `[Tool(Name = "custom")]` 覆盖方法名部分

## 4. 返回自定义类型

需要返回 record/class 时，**必须**创建 `PluginJsonContext`（AOT 序列化）：

```csharp
using System.Text.Json.Serialization;

[JsonSerializable(typeof(MyResult))]
public partial class PluginJsonContext : JsonSerializerContext { }
```

基础类型（string/int/bool 等）和其数组不需要。

## 5. 依赖注入

| 自动注册（不要重复） | 手动注册（在 `Configure`） |
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

## 6. 发布

```powershell
.\scripts\publish-native-plugin.ps1 -ProjectDir Samples\MyPlugin
```

产物：`MyPlugin.dll` + `plugin.json`（Generator 自动生成）。只需这两个文件放到 `.cortana/plugins/<kebab-name>/`。

## 7. 常见错误

见 [csharp-aot-errors.md](./csharp-aot-errors.md)。
