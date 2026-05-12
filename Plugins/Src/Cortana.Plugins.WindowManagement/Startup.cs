using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.WindowManagement;

/// <summary>
/// Windows 窗口管理插件入口，负责声明插件元数据并注册运行期依赖。
/// </summary>
[Plugin(
    Id = "window_management",
    Name = "Windows 窗口管理插件",
    Version = "1.0.14",
    Description = "提供 Windows 窗口枚举、查找、激活、最小化、最大化、恢复、关闭和移动能力。",
    Tags = ["窗口", "Windows", "系统", "桌面"],
    Instructions = "当用户要求查看当前窗口、查找窗口、切换窗口、最小化/最大化/恢复/关闭窗口或调整窗口位置大小时使用这些工具。")]
public static partial class Startup
{
    /// <summary>
    /// 注册窗口管理插件所需的服务。
    /// </summary>
    /// <param name="services">插件运行时服务容器。</param>
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<WindowManager>();
    }
}
