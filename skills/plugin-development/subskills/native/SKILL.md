---
name: native
description: 'Madorin Native 插件开发子技能。位置：subskills/native。用于 Native AOT 插件创建、Startup 组合、Tool 编写、JsonContext、后台服务、AOT 规则。触发关键词：Native 插件、AOT 插件、Startup.Configure、ToolAttribute、原生插件。'
version: 3
user-invocable: true
---

# Plugin Development Native

## Flow

1. setup-dev-environment.ps1。
2. create-native-plugin.ps1。
3. 有外部包时切到 packages 子技能。
4. 按 architecture 子技能约束实现；如果需要宿主权限申请或设置界面，读取 `../architecture/resources/plugin-metadata-protocol.md`。
5. 在 `Startup` 入口上声明 `[Plugin]`，并按需要叠加 `[RequiredHostCapability]`、`[PluginSetting]`。
6. dotnet build。
7. 检查生成的 `plugin.json` / `get_info` 是否已包含 `requiredHostCapabilities`、`settingsSchema` 等声明字段。
8. 运行 Debug Console，通过 Native Debugger REPL 逐个验证工具。
9. publish-native-plugin.ps1；需要安装包时加 -CreateZip。
10. 运行时安装切到 publish-install 子技能。

## Rules

- 只能有一个 PluginAttribute 入口。
- Startup 只注册依赖。
- 宿主能力申请使用类级别 `RequiredHostCapabilityAttribute`；它是声明，不是已授权结果。
- 宿主设置界面字段使用类级别 `PluginSettingAttribute`；敏感设置必须显式标记 `Sensitive = true`。
- `Capabilities` 表示插件对外提供的能力，不要和 `RequiredHostCapability` 混用。
- Tool 参数只用支持的基础类型。
- 自定义返回模型必须进入 PluginJsonContext。
- 后台任务用 IHostedService。
- 运行时代码统一通过 `PluginSettings` 读取宿主注入的目录、端口和 `Extensions`，不要手工解析 init 负载。
- 发布前必须通过 Native Debugger 本地调试；调试通过不等于 AOT 发布通过，仍需执行发布脚本验证 AOT。

## Metadata Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

[Plugin(
    Id = "image_helper",
    Name = "Image Helper",
    Version = "1.0.0",
    Description = "图片处理示例",
    Capabilities = ["image.resize"])]
[RequiredHostCapability(
    Id = "host.fs.workspace.read.v1",
    Purpose = "image.load",
    Required = true,
    Reason = "需要读取工作区图片。")]
[PluginSetting(
    Key = "output_dir",
    Label = "输出目录",
    Description = "图片处理结果输出目录",
    Type = PluginSettingType.Path,
    Required = true,
    Scope = ["workspace"])]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<ImageService>();
    }
}
```

这些声明会被 Generator 写入 `plugin.json` 和导出的 `get_info` 元数据，不需要手工拼装。

## Native Debugger

C# Native 插件使用 `Netor.Cortana.Plugin.Native.Debugger` 调试。脚手架会生成 `Debug\{PluginName}.Debug.csproj`，该项目引用插件项目和 Debugger 包。

调试命令：

```powershell
dotnet build
dotnet run --project Debug\PluginName.Debug.csproj
```

调试入口固定使用：

```csharp
using Netor.Cortana.Plugin.Native.Debugger;
using PluginName;

await PluginDebugRunner.RunAsync(typeof(Startup).Assembly, options =>
{
	options.DataDirectory = Path.Combine(AppContext.BaseDirectory, ".debug_data");
	options.WorkspaceDirectory = Path.Combine(AppContext.BaseDirectory, ".debug_workspace");
	options.PluginDirectory = AppContext.BaseDirectory;
	options.WsPort = 9090;
});
```

进入 REPL 后：

- `help` 查看工具列表。
- `<工具名> help` 查看参数。
- `<工具名>` 进入交互式参数输入。
- `<工具名> --name value` 使用命名参数。
- `<工具名> {"name":"value"}` 使用 JSON 参数；单参数 DTO 最稳定。
- `exit` 退出。

最低调试要求：

1. `dotnet build` 成功。
2. Debug Console 能启动并发现唯一 `[Plugin]`。
3. REPL 能列出所有 `[Tool]`。
4. 每个 `[Tool]` 至少调用一次成功路径。
5. 有输入约束的工具至少补一条边界值或失败路径。
6. 依赖 DI 的工具必须确认服务能正常解析。
7. 使用 `DataDirectory` / `WorkspaceDirectory` 的工具必须确认 `.debug_data` / `.debug_workspace` 行为正确。
8. 注册 `IHostedService` 时必须确认启动和退出没有异常。

注意：Native Debugger 调试的是开发态托管程序集。最终发布仍走 `PublishAot=true` 和 `publish-native-plugin.ps1`，不要用 Debug Console 产物替代发布产物。

## Scripts

- scripts/setup-dev-environment.ps1
- scripts/create-native-plugin.ps1
- scripts/publish-native-plugin.ps1

## Resources

- resources/layout.md
- ../architecture/resources/plugin-metadata-protocol.md
