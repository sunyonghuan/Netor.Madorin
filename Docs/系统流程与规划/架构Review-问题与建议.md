# Native Plugin Framework 架构 Review — 问题与建议

> **审查范围**：`native-plugin-framework.md`
> **审查时间**：2025-07
> **状态**：待讨论 → 确认后合入主文档

---

## 问题 1：`IPluginBuilder` 接口是否还有存在的必要？

### 问题位置

- 第 407-430 行：`IPluginBuilder` 接口定义
- 第 565-581 行：`GeneratedPluginBuilder` 实现
- 第 766-767 行：Init 中唯一使用处

### 当前设计

```csharp
// IPluginBuilder 接口（第 415-430 行）
public interface IPluginBuilder
{
    IServiceCollection Services { get; }
    PluginSettings Settings { get; }
}

// GeneratedPluginBuilder 实现（第 571-581 行）
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

// Init 中唯一使用处（第 766-767 行）
var builder = new GeneratedPluginBuilder(services, settings);
Configure(builder);
```

### 问题分析

1. **`IPluginBuilder` 只是两个属性的包装**：`Services`（`IServiceCollection`）和 `Settings`（`PluginSettings`），没有任何方法。
2. **`GeneratedPluginBuilder` 是纯数据载体**：没有路由逻辑，没有行为，只是把两个参数包成一个对象。
3. **文档自相矛盾**：第 462 行说"不再需要独立的 `GeneratedPluginBuilder` 类"，但第 565 行又定义了它。
4. **`PluginSettings` 已经注册到 DI**：第 758 行 `services.AddSingleton(settings)`，所以 `Configure` 中可以通过 `services.BuildServiceProvider().GetRequiredService<PluginSettings>()` 获取——但这太绕了。

### 建议方案

**方案 A：保留 `IPluginBuilder`，但简化为直接传参（推荐）**

去掉 `GeneratedPluginBuilder` 类，`IPluginBuilder` 接口也去掉，`Configure` 直接接收两个参数：

```csharp
// 开发者编写
public static partial class Startup
{
    public static void Configure(IServiceCollection services, PluginSettings settings)
    {
        services.AddSingleton<QuoteRepository>();
        services.AddHostedService<HealthCheckService>();
    }
}

// Generator 生成的 Init 中：
var settings = PluginSettings.FromJson(configJson);
services.AddSingleton(settings);
// ... 自动注册 [Tool] 类 ...
Configure(services, settings);
s_serviceProvider = services.BuildServiceProvider();
```

**优势**：
- 减少一个接口 + 一个生成类 = 更少的概念
- `IServiceCollection` 是 .NET 开发者最熟悉的 API
- Generator 生成文件从 3 个减少到 2 个（去掉 `GeneratedPluginBuilder.g.cs`）

**方案 B：保留 `IPluginBuilder`，作为扩展点**

如果未来可能给 `IPluginBuilder` 加更多能力（比如 `IConfiguration`、`ILoggingBuilder` 等），保留接口有扩展价值：

```csharp
public interface IPluginBuilder
{
    IServiceCollection Services { get; }
    PluginSettings Settings { get; }
    // 未来可扩展：
    // IConfiguration Configuration { get; }
    // ILoggingBuilder Logging { get; }
}
```

**但当前阶段只有两个属性，过度设计的嫌疑较大。**

### 结论

**建议采用方案 A**，去掉 `IPluginBuilder` 和 `GeneratedPluginBuilder`，直接传参。如果未来需要扩展，再引入接口也不迟（因为 `Configure` 签名变更只影响开发者的 `Startup` 类，不影响生成代码的架构）。

---

## 问题 2：工具类实例化应从 `ServiceProvider` 中获取

### 问题位置

- 第 575-581 行：`GeneratedPluginBuilder` 构造函数接收 `IServiceCollection`
- 第 480-561 行：桥接方法中通过 `sp.GetRequiredService<T>()` 获取工具实例

### 当前设计（正确部分）

桥接方法中的工具类实例化**已经是正确的**：

```csharp
// 第 486 行 — 正确：从 ServiceProvider 解析
var tool = sp.GetRequiredService<EchoTools>();
```

路由字典的签名也是正确的：
```csharp
// 第 469 行 — 正确：接收 IServiceProvider
Dictionary<string, Func<IServiceProvider, string, string>> s_toolRoutes
```

### 问题所在

问题不在桥接方法，而在 **`GeneratedPluginBuilder` 的职责混淆**：

1. `GeneratedPluginBuilder` 持有 `IServiceCollection`（注册阶段的容器）
2. 但工具类需要从 `ServiceProvider`（构建后的容器）中解析
3. 这两个阶段在 Init 中是分开的：先 `Configure`（注册），再 `BuildServiceProvider()`（构建），再通过 `s_serviceProvider` 使用
4. **`GeneratedPluginBuilder` 只参与注册阶段，不参与解析阶段**——这本身没有错，但它的存在让读者误以为它负责工具类的实例化

### 修复建议

结合问题 1 的方案 A，去掉 `GeneratedPluginBuilder` 后，Init 流程更清晰：

```csharp
[UnmanagedCallersOnly(EntryPoint = "cortana_plugin_init")]
public static int Init(IntPtr configJsonPtr)
{
    try
    {
        var configJson = Marshal.PtrToStringUTF8(configJsonPtr) ?? "{}";

        // 1. 创建 DI 容器
        var services = new ServiceCollection();

        // 2. 注册内置服务
        var settings = PluginSettings.FromJson(configJson);
        services.AddSingleton(settings);

        // 3. Generator 自动注册所有 [Tool] 类
        services.AddSingleton<EchoTools>();
        services.AddSingleton<MathTools>();
        services.AddSingleton<QuoteTools>();

        // 4. 调用开发者的 Configure（直接传 IServiceCollection + PluginSettings）
        Configure(services, settings);

        // 5. 构建 ServiceProvider（此后所有工具类从这里解析）
        s_serviceProvider = services.BuildServiceProvider();

        // 6. 启动后台服务
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
```

**关键改进**：
- 去掉了 `GeneratedPluginBuilder` 的中间层
- `Configure(services, settings)` 直接传参，语义清晰
- 工具类实例化仍然在桥接方法中通过 `s_serviceProvider.GetRequiredService<T>()` 完成（不变）
- 生命周期清晰：**注册阶段**（步骤 1-4）→ **构建阶段**（步骤 5）→ **使用阶段**（Invoke 中的桥接方法）

---

## 问题 3（附带发现）：文档前后矛盾点汇总

| 位置 | 矛盾内容 | 建议 |
|------|---------|------|
| 第 462 行 vs 第 565 行 | "不再需要独立的 GeneratedPluginBuilder" vs 仍然定义了它 | 统一：要么去掉，要么改措辞 |
| 第 395 行 vs 设计讨论 | `public static partial class Startup` vs 讨论中的 `internal` | 统一为 `public`（因为 `[UnmanagedCallersOnly]` 方法需要） |
| 第 664-666 行生成文件清单 | 列出了 `GeneratedPluginBuilder.g.cs` | 如果采用方案 A，需从清单中移除 |

---

## 待确认事项

- [ ] 是否采用方案 A（去掉 `IPluginBuilder` + `GeneratedPluginBuilder`）？
- [ ] `Startup` 类可见性确认：`public` 还是 `internal`？（建议 `public`，因 AOT 导出需要）
- [ ] `Configure` 签名确认：`Configure(IServiceCollection services, PluginSettings settings)` 还是保留 `Configure(IPluginBuilder builder)`？
- [ ] 确认后需要同步更新主文档 `native-plugin-framework.md` 中的所有相关章节

---

## 变更影响范围（确认后需更新的章节）

如果采用方案 A，以下章节需要同步修改：

1. **开发者体验示例**（第 80-101 行）：`Configure` 签名改为双参数
2. **IPluginBuilder 接口**（第 407-430 行）：整段删除
3. **内置可注入服务表**（第 433-437 行）：保留，但移到 Init 流程说明中
4. **Generator 编译时分析过程**（第 439-456 行）：措辞微调
5. **GeneratedPluginBuilder**（第 565-581 行）：整段删除
6. **生成文件清单**（第 662-667 行）：移除 `GeneratedPluginBuilder.g.cs`
7. **Startup.g.cs 生成结构**（第 669-858 行）：Init 方法中去掉 `GeneratedPluginBuilder`
8. **单 Plugin 入口约束**（第 882-900 行）：`Configure` 签名更新
