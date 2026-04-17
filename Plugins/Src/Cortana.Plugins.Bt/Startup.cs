using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;
using System.Net;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 宝塔插件入口，负责注册工具运行所需的依赖。
/// </summary>
[Plugin(
    Id = "bt",
    Name = "宝塔面板插件",
    Version = "1.0.2",
    Description = "提供宝塔面板系统查询、网站管理与配置读写能力。",
    Tags = ["宝塔", "运维", "网站", "部署"],
    Instructions = "使用宝塔相关工具前，必须提供 panelUrl 与 apiSk。执行删除、关闭、覆盖配置等高风险操作前，必须确认用户明确要求。")]
public static partial class Startup
{
    /// <summary>
    /// 向插件容器注册宝塔插件使用的服务。
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<BtRequestSigner>();

        // 使用 DI 管理 HttpClient 生命周期，避免手动 new 带来的连接复用和资源管理问题。
        services.AddHttpClient<BtApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        });
    }
}
