using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.ApplicationLauncher;

/// <summary>
/// 应用启动插件入口，负责声明插件元数据并注册运行期依赖。
/// </summary>
[Plugin(
    Id = "application_launcher",
    Name = "应用启动插件",
    Version = "1.0.15",
    Description = "提供 Windows 应用发现、应用启动和使用指定应用打开文件的能力。",
    Tags = ["应用", "启动", "Windows", "系统"],
    Instructions = "当用户要求列出本机可启动应用、启动应用程序、查询应用信息，或使用指定应用打开文件时使用这些工具。")]
public static partial class Startup
{
    /// <summary>
    /// 注册应用启动插件所需的服务。
    /// </summary>
    /// <param name="services">插件运行时服务容器。</param>
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<ApplicationLauncher>();
    }
}
