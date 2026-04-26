# Native 插件开发框架设计方案

> 状态：设计阶段 | 最后更新：2025-07

> 使用说明：本文档属于 Native 开发框架设计资料，整体方向仍然有效，但具体生成产物、工程命名和当前包版本应以仓库内现有项目和发布脚本为准。

## 目标

为 Cortana Native 通道提供一个基于 **Attribute + Source Generator** 的开发框架，让开发者只需编写业务类和方法，框架自动生成所有导出函数、参数解析、路由分发和 `plugin.json` 清单文件。

### 当前痛点

以 `NativeTestPlugin` 为例，开发者需要手动处理：

| 痛点 | 占比 | 说明 |
|------|------|------|
| 手写 JSON 元数据 | ~20% | 工具名、描述、参数定义硬编码在 JSON 字符串中 |
| Marshal 交互 | ~15% | `IntPtr`、`StringToCoTaskMemUTF8`、`PtrToStringUTF8` |
| 路由分发 | ~10% | `switch (toolName)` 手动匹配到处理方法 |
| 参数解析 | ~20% | `JsonDocument.Parse` → `TryGetProperty` → 类型转换 |
| 内存管理 | ~5% | `Free`、`CoTaskMem` 生命周期 |
| **业务代码** | **~30%** | 开发者真正关心的逻辑 |

### 框架目标

```
开发者只写业务类 + Attribute 标记（30% → 100%）
        ↓
Source Generator 编译时自动生成（70% 胶水代码）
        ↓
cortana_plugin_get_info / init / invoke / free / destroy
        ↓
plugin.json 清单文件
```

---

## 开发者体验

### 改造前（当前）

```csharp
// 开发者需要手写 ~200 行胶水代码
public static class PluginExports
{
    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_get_info")]
    public static IntPtr GetInfo()
    {
        var json = """{ "id": "...", "tools": [...] }""";  // 手写 JSON
        return Marshal.StringToCoTaskMemUTF8(json);
    }

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_invoke")]
    public static IntPtr Invoke(IntPtr toolNamePtr, IntPtr argsJsonPtr)
    {
        var toolName = Marshal.PtrToStringUTF8(toolNamePtr);  // 手动 marshal
        var argsJson = Marshal.PtrToStringUTF8(argsJsonPtr);
        return toolName switch                                // 手动路由
        {
            "echo_message" => ...,
            "math_add" => ...,
        };
    }
    // + Free, Init, Destroy ...
}
```

### 改造后（使用框架）

```csharp
using Netor.Cortana.Plugin.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ──────── Plugin 入口类（一个项目有且只有一个 Startup 类） ────────
// static partial class：开发者写 Configure 配置 DI，Generator 补充导出函数
// [Plugin] 标记在 Startup 上提供元数据，Configure 是 Generator 约束的必需方法

[Plugin(
    Id = "ntest",
    Name = "原生测试插件",
    Version = "1.0.0",
    Description = "用于验证 Native 通道端到端功能的测试插件",
    Tags = ["测试", "native"],
    Instructions = "ntest_echo_message 回显消息，ntest_math_add 两数相加，ntest_random_quote 随机名言。")]
public static partial class Startup
{
    /// <summary>
    /// 插件配置入口。
    /// 编译时：Generator 不分析此方法，工具发现通过全项目扫描 [Tool] 标记类完成。
    /// 运行时：Init 导出函数调用此方法，开发者通过 builder.Services 注册自定义服务。
    /// 约束：每个项目必须有且只有一个 Startup 类，且必须包含此方法，否则编译报错 CNPG012。
    /// </summary>
    public static void Configure(IPluginBuilder builder)
    {
        // builder.Services 是标准的 IServiceCollection，可以直接使用所有 DI API
        builder.Services.AddSingleton<QuoteRepository>();
        builder.Services.AddHostedService<HealthCheckService>();

        // 工具类无需手动注册！Generator 自动扫描所有 [Tool] 标记的类，
        // 在生成代码中自动调用 services.AddSingleton<T>() 注册到 DI 容器。
    }
}

// ──────── 工具类（每个类专注一组相关工具） ────────
// 所有工具类必须使用 [Tool] 标记，Generator 自动发现并注册到 DI

[Tool]
public class EchoTools
{
    private readonly PluginSettings _settings;

    // 构造函数参数由 ServiceProvider 自动解析，无需 Generator 分析
    public EchoTools(PluginSettings settings)
    {
        _settings = settings;
    }

    // 生成工具名：{plugin_id}_{method_snake}
    // → "ntest_echo_message"
    [Tool(Description = "回显传入的消息")]
    public string EchoMessage(
        [ParameterAttribute(Description = "要回显的消息内容")] string message)
    {
        Console.Error.WriteLine($"收到消息: {message}");  // stderr → 宿主日志
        return $"[回显] {message}（数据目录: {_settings.DataDirectory}）";
    }
}

[Tool]
public class MathTools
{
    // 生成工具名："ntest_math_add"
    [Tool(Description = "计算两个数字的和")]
    public double MathAdd(
        [ParameterAttribute(Description = "第一个加数")] double a,
        [ParameterAttribute(Description = "第二个加数")] double b)
    {
        return a + b;
    }
}

[Tool]
public class QuoteTools
{
    private readonly QuoteRepository _repo;

    public QuoteTools(QuoteRepository repo) => _repo = repo;

    // 生成工具名："ntest_random_quote"
    [Tool(Description = "返回一条随机编程名言")]
    public string RandomQuote() => _repo.GetRandom();

    // 支持异步
    // 生成工具名："ntest_fetch_data"
    [Tool(Description = "从网络获取数据")]
    public async Task<string> FetchData(
        [ParameterAttribute(Description = "请求地址")] string url)
    {
        using var http = new HttpClient();
        return await http.GetStringAsync(url);
    }

    // 支持复杂返回类型（自动 JSON 序列化）
    // 生成工具名："ntest_get_system_info"
    [Tool(Description = "获取系统信息")]
    public SystemInfo GetSystemInfo()
    {
        return new SystemInfo
        {
            OS = Environment.OSVersion.ToString(),
            Memory = GC.GetTotalMemory(false)
        };
    }
}

// ──────── 自定义服务（通过 builder.Services 直接注册） ────────

public class QuoteRepository
{
    private static readonly string[] Quotes =
    [
        "Talk is cheap. Show me the code. —— Linus Torvalds",
        "过早优化是万恶之源。—— Donald Knuth",
    ];

    public string GetRandom() => Quotes[Random.Shared.Next(Quotes.Length)];
}

// ──────── 后台服务（使用官方 IHostedService，通过 builder.Services 注册） ────────

public class HealthCheckService : IHostedService
{
    private readonly PluginSettings _settings;
    private Timer? _timer;

    public HealthCheckService(PluginSettings settings)
    {
        _settings = settings;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(_ => Console.Error.WriteLine("健康检查: 插件运行正常"),
            null, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }
}

public record SystemInfo
{
    public string OS { get; init; } = "";
    public long Memory { get; init; }
}
```

**代码量减少约 70%，零胶水代码，职责清晰分离。**

### 工具名称生成规则

工具名统一采用 **两段式** `{plugin_id}_{method_snake}` 格式，全部使用下划线分隔：

| 组成部分 | 来源 | 示例 |
|---------|------|------|
| `plugin_id` | `[Plugin(Id = "ntest")]` | `ntest` |
| `method_snake` | 方法名 `EchoMessage` → PascalCase → snake_case | `echo_message` |

**完整示例：** `ntest_echo_message`、`ntest_math_add`、`ntest_random_quote`

> **为什么用两段式而非三段式？** 工具名在每次 AI 调用中出现多次（请求、响应、思考链），
> 三段式 `{plugin_id}_{class}_{method}` 会导致名称过长（40-60 字符），浪费 token 并拉慢通信。
> 两段式将大多数工具名控制在 **15-25 字符**。
>
> **为什么需要 plugin_id 前缀？** AI 同时加载多个插件时，不同插件可能有同名工具。
> 加上插件 ID 前缀可以全局唯一，避免冲突。
>
> **Plugin Id 命名建议：** 使用简短有意义的缩写（如 `ntest`、`kgraph`、`devops`），
> 避免使用长命名空间风格（如 `com_netor_native_test`）。
>
> **为什么用下划线？** 某些 AI 框架不支持 `.` 作为工具名分隔符，
> 下划线是最广泛兼容的命名格式。
>
> **同名方法冲突：** 同一插件内不同工具类中的方法，如果 snake_case 后同名，
> 会触发编译错误 CNPG004。开发者通过 `[Tool(Name = "...")]` 手动消歧义。
>
> **可自定义**：`[Tool(Name = "my_custom_name")]` 手动指定时，仍会自动加上 `{plugin_id}_` 前缀。

---

## Attribute 体系

### 命名原则

- 使用通俗易懂的短名称
- 与现有 `IPlugin` / `IPluginContext` 概念统一
- `[Plugin]` — 插件入口，`[Tool]` — 统一标记工具类和工具方法，`[ParameterAttribute]` — 参数描述
- `[Tool]` 同时支持 `AttributeTargets.Class | Method`，一个属性两用，减少心智负担

### Attribute 定义

#### `[Plugin]` — 标记插件入口类

对应 `IPlugin` 接口的所有元数据属性。一个项目有且只有一个。

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>插件唯一标识。对应 IPlugin.Id</summary>
    public required string Id { get; init; }

    /// <summary>插件名称。对应 IPlugin.Name</summary>
    public required string Name { get; init; }

    /// <summary>插件版本。对应 IPlugin.Version</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>插件描述。对应 IPlugin.Description</summary>
    public string Description { get; init; } = "";

    /// <summary>分类标签。对应 IPlugin.Tags</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>AI 系统指令片段。对应 IPlugin.Instructions</summary>
    public string? Instructions { get; init; }
}
```

#### `[Tool]` — 统一标记工具类和工具方法

一个 Attribute 两种用途：

- **标记在类上** → 声明这是一个工具类（必须标记）
- **标记在方法上** → 声明这是一个工具方法，提供描述和自定义名称

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>
    /// 标记在方法上时：工具名称。不填则从方法名自动转换 PascalCase → snake_case。
    /// 标记在类上时：无效（忽略）。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 标记在方法上时：工具描述，告诉 AI 这个工具做什么（必填）。
    /// 标记在类上时：工具类的分组描述（可选）。
    /// </summary>
    public string Description { get; init; } = "";
}
```

所有工具类必须使用 `[Tool]` 标记，Generator 会自动扫描项目中所有标记了 `[Tool]` 的类并注册到 DI 容器：

```csharp
// ✅ 正确：使用 [Tool] 标记，Generator 自动发现并注册
[Tool]
public class EchoService { ... }

// ❌ 编译错误 CNPG005：工具类缺少 [Tool] 标记（含 [Tool] 方法但类上未标记）
public class MathTools
{
    [Tool(Description = "...")] 
    public string Calc() { ... }  // 方法有 [Tool]，但类没有 → 报错
}
```

> **无需手动注册**：不再需要 `AddTool<T>()`，Generator 在编译时扫描所有 `[Tool]` 类，
> 在生成的 `Init` 代码中自动调用 `services.AddSingleton<T>()` 注册。

#### `[ParameterAttribute]` — 标记参数描述

对应 `NativeToolParameter` 中的参数定义。

```csharp
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class ParameterAttribute : Attribute
{
    /// <summary>
    /// 参数名称。不填则使用方法参数名。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>参数描述，告诉 AI 这个参数的含义。</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// 是否必填。默认 true（值类型始终为 true，引用类型根据可空性推断）。
    /// </summary>
    public bool Required { get; init; } = true;
}
```

### 与 IPlugin / IPluginContext 的概念映射

| IPlugin 属性 | 框架对应 | 说明 |
|-------------|---------|------|
| `Id` | `[Plugin(Id = "...")]` | 插件唯一标识（必须使用下划线格式，如 `com_netor_native_test`） |
| `Name` | `[Plugin(Name = "...")]` | 显示名称 |
| `Version` | `[Plugin(Version = "...")]` | 版本号 |
| `Description` | `[Plugin(Description = "...")]` | 描述 |
| `Tags` | `[Plugin(Tags = [...])]` | 分类标签 |
| `Instructions` | `[Plugin(Instructions = "...")]` | AI 指令 |
| `Tools` | 所有 `[Tool]` 标记的类中 `[Tool]` 标记的方法 | Generator 自动扫描，无需手动注册 |
| `InitializeAsync` | Generator 生成的 `Init` 导出函数（自动注册 [Tool] 类到 DI → 调用 Configure → BuildServiceProvider → 启动 IHostedService） | 自动生成，开发者无需实现 |

| IPluginContext 属性 | Native 框架对应 | 说明 |
|-------------------|---------------|------|
| `DataDirectory` | `PluginSettings.DataDirectory` | 通过 ServiceProvider 构造函数注入 |
| `WorkspaceDirectory` | `PluginSettings.WorkspaceDirectory` | 通过 ServiceProvider 构造函数注入 |
| `LoggerFactory` | 开发者通过 `builder.Services` 自行注册 | 框架不内置，按需注册（stderr 可直接用 `Console.Error`） |
| `HttpClientFactory` | 开发者通过 `builder.Services` 自行注册 | 框架不内置，按需注册 |
| `WsPort` | `PluginSettings.WsPort` | 通过 ServiceProvider 构造函数注入 |

---

## DI 方案（编译时 [Tool] 扫描 + 运行时 ServiceCollection）

### 设计理念

框架采用最简化的双轨协作模式：

| 阶段 | 消费者 | 作用 |
|------|--------|------|
| **编译时** | Generator 全项目扫描 | 扫描所有 `[Tool]` 标记的类及其 `[Tool]` 方法，生成路由字典、桥接方法、JSON 元数据、`JsonSerializerContext` |
| **运行时** | 生成的 `Init` 导出函数 | 自动注册 [Tool] 类到 DI → 调用 `Configure`（开发者通过 `builder.Services` 注册自定义服务）→ `BuildServiceProvider` |

> **核心简化**：Generator 不分析 `Configure` 方法的语法树。工具发现完全基于 `[Tool]` 标记的全项目扫描。
> `Configure` 只是开发者注册自定义服务的入口，框架不对其内容做任何静态分析。

### Startup 入口类约束

```csharp
/// <summary>
/// Native 插件入口类。一个项目有且只有一个标记了 [Plugin] 的 static partial class，
/// 且必须包含 public static void Configure(IPluginBuilder builder) 方法。
///   - 编译时：Generator 扫描项目中所有 [Tool] 标记的类，生成路由字典和元数据。
///   - 运行时：Init 导出函数自动注册 [Tool] 类到 DI，然后调用 Configure 让开发者注册自定义服务。
/// </summary>
[Plugin(Id = "com_netor_native_test", Name = "...")]
public static partial class Startup
{
    public static void Configure(IPluginBuilder builder)
    {
        // builder.Services 是标准 IServiceCollection
        // 注册自定义服务、后台服务等
        builder.Services.AddSingleton<QuoteRepository>();
        builder.Services.AddHostedService<HealthCheckService>();
    }
}
```

### IPluginBuilder 接口

```csharp
/// <summary>
/// 插件构建器接口。
/// 由 Generator 生成实现，提供宿主传入的配置参数和标准 DI 容器。
/// 开发者在 Configure 中通过 Services 属性注册自定义服务。
/// </summary>
public interface IPluginBuilder
{
    /// <summary>
    /// 标准 DI 容器。开发者可直接使用所有 IServiceCollection 扩展方法。
    /// 内置服务（PluginSettings）已预先注册。
    /// [Tool] 标记的类由 Generator 在调用 Configure 前自动注册。
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// 插件配置（DataDirectory / WorkspaceDirectory / WsPort 等）。
    /// 从宿主 cortana_plugin_init 传入的 config JSON 解析而来。
    /// 已自动注册到 Services，此属性方便 Configure 中直接读取。
    /// </summary>
    PluginSettings Settings { get; }
}
```

### 内置可注入服务

| 服务类型 | 说明 | 来源 |
|---------|------|------|
| `PluginSettings` | 插件配置（DataDirectory/WorkspaceDirectory/WsPort） | `cortana_plugin_init` 传入的 config JSON |

### Generator 编译时分析过程

Generator 在编译阶段通过全项目扫描发现工具，**不分析 Configure 方法内容**：

**编译时扫描过程：**

1. 扫描项目中所有标记了 `[Tool]` 的类
2. 对每个 `[Tool]` 类，扫描其 `[Tool]` 标记的 public 方法
3. 收集方法签名（参数类型 + 返回类型），生成参数解析桥接方法和路由字典
4. 按 `{plugin_id}_{method_snake}` 规则生成工具名，检查全局唯一性
5. 收集所有非基础返回类型，生成 `PluginJsonContext`（`JsonSerializerContext`），确保 AOT 下 JSON 序列化安全
6. 从 `[Plugin]` + `[Tool]` + `[ParameterAttribute]` 元数据生成 `get_info` JSON 常量
7. 生成 `Startup.g.cs`（路由字典 + 桥接方法 + 5 个导出函数）

> **关键简化**：
> - Generator 不分析 `Configure` 方法语法树，不关心开发者注册了什么服务
> - Generator 不分析工具类构造函数——构造函数依赖由 `ServiceProvider` 在运行时自动解析
> - Generator 只关心 `[Tool]` 标记和方法签名，职责极其精简

### Generator 生成的运行时组件

#### 路由字典和桥接方法（生成在 Startup.g.cs 中）

Generator 在 `Startup.g.cs` 中直接生成路由字典和桥接方法，不再需要独立的 `GeneratedPluginBuilder` 类：

```csharp
// Generator 生成（Startup.g.cs 中的路由和桥接部分）
public static partial class Startup
{
    // 路由字典：工具名 → 桥接方法
    private static readonly Dictionary<string, Func<IServiceProvider, string, string>> s_toolRoutes = new()
    {
        ["ntest_echo_message"] = Invoke_EchoTools_EchoMessage,
        ["ntest_math_add"] = Invoke_MathTools_MathAdd,
        ["ntest_random_quote"] = (sp, _) => Invoke_QuoteTools_RandomQuote(sp),
        ["ntest_fetch_data"] = Invoke_QuoteTools_FetchData,
        ["ntest_get_system_info"] = (sp, _) => Invoke_QuoteTools_GetSystemInfo(sp),
    };

    // ──────── 桥接方法（Generator 生成，参数解析 + 业务调用） ────────

    private static string Invoke_EchoTools_EchoMessage(IServiceProvider sp, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var message = doc.RootElement.GetProperty("message").GetString() ?? "";
            var tool = sp.GetRequiredService<EchoTools>();
            return tool.EchoMessage(message);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResult(ex.Message, "ntest_echo_message"),
                PluginJsonContext.Default.ErrorResult);
        }
    }

    private static string Invoke_MathTools_MathAdd
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var a = doc.RootElement.GetProperty("a").GetDouble();
            var b = doc.RootElement.GetProperty("b").GetDouble();
            var tool = sp.GetRequiredService<MathTools>();
            return tool.MathAdd(a, b).ToString();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResult(ex.Message, "ntest_math_add"),
                PluginJsonContext.Default.ErrorResult);
        }
    }

    private static string Invoke_QuoteTools_RandomQuote
    {
        try
        {
            var tool = sp.GetRequiredService<QuoteTools>();
            return tool.RandomQuote();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResult(ex.Message, "ntest_random_quote"),
                PluginJsonContext.Default.ErrorResult);
        }
    }

    private static string Invoke_QuoteTools_FetchData
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var url = doc.RootElement.GetProperty("url").GetString() ?? "";
            var tool = sp.GetRequiredService<QuoteTools>();
            return tool.FetchData(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResult(ex.Message, "ntest_fetch_data"),
                PluginJsonContext.Default.ErrorResult);
        }
    }

    private static string Invoke_QuoteTools_GetSystemInfo(IServiceProvider sp)
    {
        try
        {
            var tool = sp.GetRequiredService<QuoteTools>();
            var result = tool.GetSystemInfo();
            return JsonSerializer.Serialize(result, PluginJsonContext.Default.SystemInfo);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResult(ex.Message, "ntest_get_system_info"),
                PluginJsonContext.Default.ErrorResult);
        }
    }
}
```

#### GeneratedPluginBuilder（IPluginBuilder 的生成实现）

Generator 为每个插件项目生成一个简单的 `IPluginBuilder` 实现：

```csharp
// Generator 生成（GeneratedPluginBuilder.g.cs）
internal sealed class GeneratedPluginBuilder : IPluginBuilder
{
    public IServiceCollection Services { get; }
    public PluginSettings Settings { get; }

    internal GeneratedPluginBuilder(IServiceCollection services, PluginSettings settings)
    {
        Services = services;
        Settings = settings;
    }
}
```

> **极简实现**：`GeneratedPluginBuilder` 只是 `IPluginBuilder` 接口的数据载体，
> 没有任何路由逻辑——路由和桥接方法都生成在 `Startup.g.cs` 中。

#### PluginJsonContext（自动生成的 JSON 序列化上下文）

Generator 扫描所有 `[Tool]` 方法的返回类型，自动生成 AOT 安全的 `JsonSerializerContext`：

```csharp
// Generator 生成（PluginJsonContext.g.cs）
[JsonSerializable(typeof(SystemInfo))]       // ← 从 QuoteTools.GetSystemInfo() 返回类型提取
[JsonSerializable(typeof(ErrorResult))]      // ← 框架内置错误结构
internal partial class PluginJsonContext : JsonSerializerContext { }

// 框架内置
internal record ErrorResult(string Error, string Tool);
```

> **开发者零额外工作**：Generator 自动发现所有非基础返回类型并注册到 `JsonSerializerContext`，
> 桥接方法中使用 `PluginJsonContext.Default.Xxx` 确保 AOT 安全序列化。

**方案优势：**

- **官方容器，AOT 安全**：`Microsoft.Extensions.DependencyInjection` 从 .NET 8+ 完全 AOT 兼容
- **开发者零学习成本**：`builder.Services` 就是标准 `IServiceCollection`，无新 API
- **生态兼容**：`AddHostedService`、`AddHttpClient` 等所有官方扩展方法直接可用
- **Generator 职责极简**：只扫描 `[Tool]` 标记 → 生成路由 + 桥接 + JSON 元数据 + `JsonSerializerContext`
- **无构造函数分析**：工具类依赖完全由 `ServiceProvider` 运行时解析，Generator 不参与
- **工具名全局唯一**：`{plugin_id}_{method}` 两段式格式，短小精悍，避免多插件同名冲突
- **官方 IHostedService**：后台服务使用 `AddHostedService<T>()`，无自定义接口

---

### 参数类型映射（C# → JSON Schema）

| C# 类型 | JSON type | 说明 |
|---------|-----------|------|
| `string` | `"string"` | ✅ 支持 |
| `int` / `long` | `"integer"` | ✅ 支持 |
| `double` / `float` / `decimal` | `"number"` | ✅ 支持 |
| `bool` | `"boolean"` | ✅ 支持 |
| `string?` | `"string"` + required=false | ✅ 可空推断 |
| `List<T>` / `T[]` / 嵌套对象 | — | ❌ 不支持，编译时报错 |

### 不支持的参数类型（编译时报错）

```
CNPG003: 工具方法 'ProcessItems' 的参数 'items' 类型为 'List<string>'，
         Native 插件不支持复杂参数类型。仅支持：string, int, long, double, float, decimal, bool
```

### 返回值类型映射

| C# 返回类型 | Generator 处理方式 |
|------------|------------------|
| `string` | 直接返回 |
| `int` / `double` / `bool` | `ToString()` 返回 |
| `Task<string>` | `.GetAwaiter().GetResult()` 同步等待后返回 |
| `Task<T>` | 同步等待 + `JsonSerializer.Serialize(result, PluginJsonContext.Default.T)` |
| `ValueTask<T>` | `AsTask()` + 同步等待 |
| 自定义类/record | `JsonSerializer.Serialize(result, PluginJsonContext.Default.T)` — Generator 自动生成 `JsonSerializerContext` |
| `void` / `Task` | 返回 `"ok"` |

> **自动 JSON 序列化上下文**：Generator 扫描所有 `[Tool]` 方法的返回类型，将非基础类型自动注册到
> 生成的 `PluginJsonContext`（`JsonSerializerContext` 子类），确保 AOT 安全。开发者无需手动编写 `[JsonSerializable]`。

> **⚠️ 异步方法限制：** Generator 对 `Task<T>` / `ValueTask<T>` 返回值使用 `.GetAwaiter().GetResult()` 进行同步等待。
> 这在 Native AOT 环境下是安全的（无 `SynchronizationContext`），但开发者应注意：
> - 不要在异步方法内部安装自定义 `SynchronizationContext`，否则可能死锁
> - 异步方法会阻塞调用线程直到完成，宿主侧应做好超时控制

---

## Source Generator 生成内容

### 生成文件清单

Generator 在编译时扫描标记了 `[Plugin]` 的 partial class 和所有 `[Tool]` 类，生成以下文件：

| 生成文件 | 内容 |
|---------|------|
| `Startup.g.cs` | Startup 类的另一半 partial：静态路由字典 + 桥接方法 + 5 个导出函数 + ServiceProvider 管理 |
| `GeneratedPluginBuilder.g.cs` | `IPluginBuilder` 实现：仅数据载体（Services / Settings） |
| `PluginJsonContext.g.cs` | `JsonSerializerContext` 子类：所有非基础返回类型的 AOT 安全序列化 |
| `plugin.json`（输出到构建目录） | 插件清单，从 `[Plugin]` 元数据生成 |

### Startup.g.cs 生成结构

Generator 为开发者的 `static partial class Startup` 补充另一半。路由字典、桥接方法、5 个导出函数都在此文件中：

```csharp
// <auto-generated/>
// 由 Netor.Cortana.Plugin.Native.Generator 自动生成

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static partial class Startup
{
    // 官方 DI 容器
    private static ServiceProvider? s_serviceProvider;

    // ──── 路由字典（Generator 从 [Tool] 标记扫描生成） ────
    // 见前文"路由字典和桥接方法"章节，此处省略重复

    // ──────── cortana_plugin_get_info ────────
    // 从 [Plugin] + 所有 [Tool] 类/方法 + [ParameterAttribute] 编译时生成 JSON 常量
    // 工具名格式：{plugin_id}_{method_snake}

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_get_info")]
    public static IntPtr GetInfo()
    {
        const string json = """
        {
            "id": "ntest",
            "name": "原生测试插件",
            "version": "1.0.0",
            "description": "用于验证 Native 通道端到端功能的测试插件",
            "instructions": "ntest_echo_message 回显消息...",
            "tags": ["测试", "native"],
            "tools": [
                {
                    "name": "ntest_echo_message",
                    "description": "回显传入的消息",
                    "parameters": [
                        { "name": "message", "type": "string", "description": "要回显的消息内容", "required": true }
                    ]
                },
                {
                    "name": "ntest_math_add",
                    "description": "计算两个数字的和",
                    "parameters": [
                        { "name": "a", "type": "number", "description": "第一个加数", "required": true },
                        { "name": "b", "type": "number", "description": "第二个加数", "required": true }
                    ]
                },
                {
                    "name": "ntest_random_quote",
                    "description": "返回一条随机编程名言",
                    "parameters": []
                },
                {
                    "name": "ntest_fetch_data",
                    "description": "从网络获取数据",
                    "parameters": [
                        { "name": "url", "type": "string", "description": "请求地址", "required": true }
                    ]
                },
                {
                    "name": "ntest_get_system_info",
                    "description": "获取系统信息",
                    "parameters": []
                }
            ]
        }
        """;

        return Marshal.StringToCoTaskMemUTF8(json);
    }

    // ──────── cortana_plugin_init ────────
    // Generator 自动注册 [Tool] 类 → 内置服务 → 调用 Configure → BuildServiceProvider → 启动 IHostedService

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_init")]
    public static int Init(IntPtr configJsonPtr)
    {
        try
        {
            var configJson = Marshal.PtrToStringUTF8(configJsonPtr) ?? "{}";

            // 1. 创建 ServiceCollection 并注册内置服务
            var services = new ServiceCollection();
            var settings = PluginSettings.FromJson(configJson);
            services.AddSingleton(settings);

            // 2. Generator 自动注册所有 [Tool] 标记的类（开发者无需手动注册）
            services.AddSingleton<EchoTools>();
            services.AddSingleton<MathTools>();
            services.AddSingleton<QuoteTools>();

            // 3. 调用开发者的 Configure 方法（开发者通过 builder.Services 注册自定义服务）
            var builder = new GeneratedPluginBuilder(services, settings);
            Configure(builder);

            // 4. 构建 ServiceProvider
            s_serviceProvider = services.BuildServiceProvider();

            // 5. 启动所有 IHostedService（开发者通过 AddHostedService 注册的后台服务）
            var hostedServices = s_serviceProvider.GetServices<IHostedService>();
            foreach (var svc in hostedServices)
            {
                _ = svc.StartAsync(CancellationToken.None);
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // ──────── cortana_plugin_invoke ────────
    // 通过静态路由字典分发

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_invoke")]
    public static IntPtr Invoke(IntPtr toolNamePtr, IntPtr argsJsonPtr)
    {
        var toolName = Marshal.PtrToStringUTF8(toolNamePtr) ?? "";
        var argsJson = Marshal.PtrToStringUTF8(argsJsonPtr) ?? "{}";

        try
        {
            string result;
            if (s_toolRoutes.TryGetValue(toolName, out var handler))
            {
                result = handler(s_serviceProvider!, argsJson);
            }
            else
            {
                result = JsonSerializer.Serialize(
                    new ErrorResult($"未知工具: {toolName}", toolName),
                    PluginJsonContext.Default.ErrorResult);
            }

            return Marshal.StringToCoTaskMemUTF8(result);
        }
        catch (Exception ex)
        {
            var error = JsonSerializer.Serialize(
                new ErrorResult(ex.Message, toolName),
                PluginJsonContext.Default.ErrorResult);
            return Marshal.StringToCoTaskMemUTF8(error);
        }
    }

    // ──────── cortana_plugin_free ────────

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_free")]
    public static void Free(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            Marshal.FreeCoTaskMem(ptr);
    }

    // ──────── cortana_plugin_destroy ────────
    // 停止 IHostedService → Dispose ServiceProvider

    [UnmanagedCallersOnly(EntryPoint = "cortana_plugin_destroy")]
    public static void Destroy()
    {
        if (s_serviceProvider is not null)
        {
            // 1. 停止所有 IHostedService
            var hostedServices = s_serviceProvider.GetServices<IHostedService>();
            foreach (var svc in hostedServices)
            {
                try { svc.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            }

            // 2. Dispose ServiceProvider（自动释放所有实现了 IDisposable 的 Singleton 实例）
            s_serviceProvider.Dispose();
            s_serviceProvider = null;
        }
    }
}
```

> **关键设计点：**
> - `Init`：Generator 自动生成 `services.AddSingleton<T>()` 注册所有 `[Tool]` 类，开发者无需手动注册
> - `Init`：调用 `Configure` 让开发者通过 `builder.Services` 注册自定义服务和 `IHostedService`
> - `Invoke`：使用 `Startup` 内的静态路由字典 `s_toolRoutes`，不再依赖 `GeneratedPluginBuilder`
> - `Destroy`：通过 `GetServices<IHostedService>()` 获取所有后台服务并停止，然后 `Dispose`
> - 路由字典和桥接方法都在 `Startup.g.cs` 中，`GeneratedPluginBuilder.g.cs` 只是 `IPluginBuilder` 的数据载体

### plugin.json 自动生成

Generator 同时生成 `plugin.json` 的内容，通过自定义 **MSBuild Target** 在构建后将其写入输出目录：

```json
{
    "id": "com_netor_native_test",
    "name": "原生测试插件",
    "version": "1.0.0",
    "description": "测试插件",
    "runtime": "native",
    "libraryName": "NativeTestPlugin.dll",
    "minHostVersion": "1.0.0"
}
```

- `id` / `name` / `version` / `description` — 从 `[Plugin]` 提取
- `runtime` — 固定为 `"native"`
- `libraryName` — 从项目的 `AssemblyName` 推断（`$(AssemblyName).dll`）

---

## 单 Plugin 入口约束

### 规则

一个项目**有且只有一个**标记了 `[Plugin]` 的 `public static partial class Startup`，且必须包含 `public static void Configure(IPluginBuilder builder)` 方法。

```csharp
// ✅ 正确
[Plugin(Id = "com_netor_test", Name = "测试")]
public static partial class Startup
{
    public static void Configure(IPluginBuilder builder) { ... }
}

// ❌ 编译错误 CNPG010：项目中发现多个 Startup 类
[Plugin(Id = "com_netor_a", Name = "A")]
public static partial class StartupA { ... }
[Plugin(Id = "com_netor_b", Name = "B")]
public static partial class StartupB { ... }

// ❌ 编译错误 CNPG011：Startup 类必须是 static partial class
[Plugin(Id = "com_netor_test", Name = "测试")]
public partial class Startup { ... }

// ❌ 编译错误 CNPG012：Startup 类缺少 Configure 方法
[Plugin(Id = "com_netor_test", Name = "测试")]
public static partial class Startup
{
    // 缺少 public static void Configure(IPluginBuilder builder)
}
```

### 工具名称冲突检测

工具名由 `{plugin_id}_{method_snake}` 生成（两段式）。由于不包含类名，**不同工具类中的同名方法会直接冲突**：

```csharp
// 假设 Plugin Id = "ntest"

[Tool]
public class DataTools
{
    [Tool(Description = "获取数据")]
    public string GetData() { ... }  // → "ntest_get_data"
}

[Tool]
public class ReportTools
{
    [Tool(Description = "获取报告数据")]
    public string GetData() { ... }  // → "ntest_get_data" ← 冲突！
}

// 编译错误 CNPG004：工具名称 'ntest_get_data' 冲突（DataTools.GetData 与 ReportTools.GetData）
// 解决方案 1：使用不同的方法名
[Tool]
public class ReportTools
{
    [Tool(Description = "获取报告数据")]
    public string GetReportData() { ... }  // → "ntest_get_report_data" ✅
}

// 解决方案 2：使用 [Tool(Name = "...")] 手动指定
[Tool]
public class ReportTools
{
    [Tool(Name = "report_data", Description = "获取报告数据")]
    public string GetData() { ... }  // → "ntest_report_data" ✅
}
```

> **设计取舍**：两段式牺牲了类名隔离，换来了更短的工具名（减少 50%+ token 消耗）。
> 实践中方法名冲突是少数情况，编译时 CNPG004 报错 + `[Tool(Name)]` 手动消歧义足够处理。

### 方法重载约束

由于工具名由方法名转换为 snake_case 后生成，**同一工具类中不支持同名方法重载**——重载方法会生成相同的工具名导致 CNPG004 冲突：

```csharp
[Tool]
public class MathTools
{
    [Tool(Description = "整数加法")]
    public int MathAdd(int a, int b) => a + b;        // → "ntest_math_add"

    [Tool(Description = "浮点加法")]
    public double MathAdd(double a, double b) => a + b; // → "ntest_math_add" ← 冲突！
}

// 编译错误 CNPG004：工具名称 'ntest_math_add' 冲突
// 解决方案：使用不同的方法名，或 [Tool(Name = "...")] 手动指定
[Tool]
public class MathTools
{
    [Tool(Description = "整数加法")]
    public int MathAddInt(int a, int b) => a + b;          // → "ntest_math_add_int"

    [Tool(Description = "浮点加法")]
    public double MathAddDouble(double a, double b) => a + b; // → "ntest_math_add_double"
}
```

---

## 错误处理约定

### 工具方法抛异常时

Generator 生成的桥接方法统一捕获异常，返回包含**工具名称**和**错误消息**的 JSON：

```json
{
    "error": "System.Net.Http.HttpRequestException: 无法连接到服务器",
    "tool": "ntest_fetch_data"
}
```

### 生成的错误处理代码模式

```csharp
// 桥接方法生成在 Startup.g.cs 中
// 方法名格式：Invoke_{ToolClass}_{MethodName}
private static string Invoke_QuoteTools_FetchData(IServiceProvider sp, string argsJson)
{
    try
    {
        // ... 参数解析 + sp.GetRequiredService<QuoteTools>() 获取工具实例 + 业务调用
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(
            new ErrorResult(ex.Message, "ntest_fetch_data"),
            PluginJsonContext.Default.ErrorResult);
    }
}
```

每个工具方法独立 try/catch，一个工具的异常不影响其他工具。

---

## 编译时诊断

Generator 在编译时检查开发者代码，发现问题立即报告编译错误/警告：

| 诊断 ID | 级别 | 说明 |
|---------|------|------|
| `CNPG003` | Error | `[Tool]` 方法参数使用了不支持的复杂类型（`List<T>`、`Dictionary`、嵌套对象等） |
| `CNPG004` | Error | 不同工具类中存在同名方法，或同一类中方法重载导致生成的工具名 `{plugin_id}_{method_snake}` 冲突 |
| `CNPG005` | Error | `[Plugin]` 类或 `[Tool]` 类不是 `public` 的。或 `[Tool]` 类标记在类上但类中无任何 `[Tool]` 标记的方法 |
| `CNPG006` | Error | `[Tool]` 方法不是 `public` 的 |
| `CNPG007` | Warning | `[Tool]` 方法名已经是 snake_case，无需转换 |
| `CNPG008` | Error | 方法名转换后的工具名为空或包含非法字符 |
| `CNPG009` | Error | `[Plugin]` 缺少必填属性 `Id` 或 `Name` |
| `CNPG010` | Error | 项目中发现多个 Startup 类 |
| `CNPG011` | Error | Startup 类必须是 `static partial class` |
| `CNPG012` | Error | Startup 类缺少 `Configure` 方法 |
| `CNPG019` | Error | `[Plugin]` 的 `Id` 包含非法字符（仅允许小写字母、数字和下划线） |

> **已移除的诊断**（因架构简化不再需要）：
> - `CNPG001`（构造函数参数未注册）→ 由 `ServiceProvider` 运行时自动处理
> - `CNPG002`（多个构造函数）→ 由 `ServiceProvider` 运行时自动选择
> - `CNPG013`（AddTool 的 T 缺少 [Tool]）→ 不再有 `AddTool<T>()`
> - `CNPG014`（AddTool 的 T 无 [Tool] 方法）→ 合并到 CNPG005
> - `CNPG015`（循环依赖）→ 由 `ServiceProvider` 运行时自动检测
> - `CNPG016`（[Tool] 类未注册）→ Generator 自动注册，不需要手动
> - `CNPG017`/`CNPG018`（AddBackgroundService 检查）→ 使用官方 `IHostedService`，无需自定义检查

---

## 项目结构

```
Src/Plugins/
├── Netor.Cortana.Plugin.Native/                  # Attribute + 运行时服务
│   │                                             # 目标框架：net10.0
│   ├── PluginAttribute.cs                        #   [Plugin] 定义
│   ├── ToolAttribute.cs                          #   [Tool] 定义（类 + 方法通用）
│   ├── ParameterAttribute.cs                     #   [ParameterAttribute] 定义
│   ├── IPluginBuilder.cs                         #   构建器接口（Services / Settings）
│   └── PluginSettings.cs                         #   配置模型（DataDirectory 等）
│
└── Netor.Cortana.Plugin.Native.Generator/        # 新增：Source Generator
    │                                             # 目标框架：netstandard2.0
    ├── NativePluginGenerator.cs                  #   [Generator] 入口
    ├── Emitters/
    │   ├── InfoJsonEmitter.cs                    #     生成 get_info JSON 常量
    │   ├── StartupEmitter.cs                     #     生成 Startup.g.cs（导出函数 + 路由 + 桥接方法）
    │   ├── PluginBuilderEmitter.cs               #     生成 GeneratedPluginBuilder.g.cs（IPluginBuilder 数据载体）
    │   ├── JsonContextEmitter.cs                 #     生成 PluginJsonContext.g.cs（AOT 安全 JSON 序列化上下文）
    │   ├── DestroyEmitter.cs                     #     生成 destroy 清理逻辑
    │   └── PluginJsonEmitter.cs                  #     生成 plugin.json
    ├── Analysis/
    │   ├── StartupAnalyzer.cs                    #     分析 [Plugin] 标记的 static partial class Startup
    │   ├── ToolClassAnalyzer.cs                  #     扫描项目中所有 [Tool] 类 + [Tool] 方法
    │   ├── ToolNameGenerator.cs                  #     生成 {plugin_id}_{class}_{method} 工具名 + 冲突检测
    │   └── TypeMapper.cs                         #     C# 类型 → JSON schema
    └── Diagnostics/
        └── DiagnosticDescriptors.cs              #     CNPG003-019 诊断定义
```

---

## NuGet 打包与发布

### 包结构

开发者只需引用一个 NuGet 包即可，该包内嵌 Generator：

```xml
<!-- 开发者的 csproj -->
<ItemGroup>
    <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.0" />
</ItemGroup>
```

### NuGet 包内部结构

```
Netor.Cortana.Plugin.Native.1.0.0.nupkg
├── lib/
│   └── net10.0/
│       └── Netor.Cortana.Plugin.Native.dll       # Attribute + PluginSettings + IPluginBuilder
│                                                  # 依赖：Microsoft.Extensions.DependencyInjection.Abstractions
│                                                  #        Microsoft.Extensions.Hosting.Abstractions
├── analyzers/
│   └── dotnet/
│       └── cs/
│           └── Netor.Cortana.Plugin.Native.Generator.dll  # Source Generator
└── build/
    └── Netor.Cortana.Plugin.Native.props          # 自动配置（PublishAot=true 等）
```

> **依赖说明**：
> - `Microsoft.Extensions.DependencyInjection.Abstractions` — `IServiceCollection` 接口（`IPluginBuilder.Services` 属性类型）
> - `Microsoft.Extensions.Hosting.Abstractions` — `IHostedService` 接口（后台服务）
> - `.props` 自动引入 `Microsoft.Extensions.DependencyInjection`（实现包）和 `Microsoft.Extensions.Hosting`（实现包）

### 自动配置 props

NuGet 包内附带 `.props` 文件，自动配置插件项目的 AOT 发布设置：

```xml
<!-- build/Netor.Cortana.Plugin.Native.props -->
<Project>
  <PropertyGroup>
    <!-- 确保 AOT 发布 -->
    <PublishAot>true</PublishAot>
    <IsAotCompatible>true</IsAotCompatible>

    <!-- 最小化体积 -->
    <OptimizationPreference>Size</OptimizationPreference>
    <IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <!-- Generator 生成的代码依赖这些实现包，使用版本范围允许消费者获取最新兼容版本 -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="[10.0.0,)" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="[10.0.0,)" />
  </ItemGroup>
</Project>
```

### 开发者的完整 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- PublishAot 等由 NuGet 包自动配置，无需手动设置 -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Netor.Cortana.Plugin.Native" Version="1.0.0" />
  </ItemGroup>
</Project>
```

**发布命令：**

```bash
dotnet publish -c Release -r win-x64
# 输出：bin/Release/net10.0/win-x64/publish/
#   ├── MyPlugin.dll        # AOT 原生 DLL
#   └── plugin.json         # Generator 自动生成
```

---

## AOT 兼容性说明

### 全链路 AOT 安全

本框架的所有组件均经过 AOT 兼容性验证：

| 组件 | AOT 兼容性 | 说明 |
|------|-----------|------|
| DI 容器 | ✅ 安全 | `Microsoft.Extensions.DependencyInjection` 从 .NET 8+ 完全 AOT 兼容 |
| IHostedService | ✅ 安全 | 官方接口，`Microsoft.Extensions.Hosting` 从 .NET 8+ AOT 兼容 |
| 工具路由 | ✅ 安全 | `Dictionary<string, Func<IServiceProvider, string, string>>` 静态初始化 |
| 参数解析 | ✅ 安全 | `JsonDocument`（DOM 模式）不依赖运行时代码生成 |
| 返回值序列化 | ✅ 安全 | Generator 自动生成 `PluginJsonContext`（`JsonSerializerContext`），所有序列化调用使用强类型 |
| 构造函数注入 | ✅ 安全 | `ServiceProvider` 在 AOT 下正常解析构造函数依赖，Generator 无需分析构造函数 |

### 架构简化历程

| 演进阶段 | 方案 | 问题 |
|---------|------|------|
| V1 自建容器 | Generator 生成 `PluginContainer`（工厂方法 + 依赖图 + 拓扑排序） | Generator 过于复杂，需分析构造函数、解决循环依赖 |
| V2 ServiceCollection + 自定义接口 | 用官方 DI 容器，但保留 `AddService/AddTool/AddBackgroundService` 和 `IBackgroundService` | 接口冗余：官方容器已支持全部功能 |
| **V3 最终方案** | **直接公开 `IServiceCollection`，Generator 自动扫描 `[Tool]` 类，使用官方 `IHostedService`** | **Generator 职责最小化，零自定义接口** |

> **结论：** 既然官方容器完全 AOT 安全，那就不封装、不自建——直接公开 `builder.Services`，
> 让开发者用最熟悉的方式注册服务。Generator 只做一件事：扫描 `[Tool]` 标记，生成路由和元数据。

---

## 实施路线

| 阶段 | 内容 | 产出 |
|------|------|------|
| **P0** | Attribute 定义 + PluginSettings + IPluginBuilder | `Netor.Cortana.Plugin.Native` 项目 |
| **P1** | Generator 核心：[Tool] 类扫描 + 路由生成 + get_info / init / invoke / free / destroy | `Startup.g.cs` + `GeneratedPluginBuilder.g.cs` 自动生成 |
| **P2** | PluginJsonContext 自动生成 + plugin.json 输出 | AOT 安全序列化 + 构建输出含 plugin.json |
| **P3** | 编译诊断（CNPG003-019） | 友好的编译时错误提示 |
| **P4** | NuGet 打包 + .props 自动配置 | 一个包搞定所有事 |
| **P5** | 改造 NativeTestPlugin 为框架版本 | 验证端到端功能 |




