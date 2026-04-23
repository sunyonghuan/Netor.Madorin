using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Plugin;

namespace NativeTestPlugin;

/// <summary>
/// 原生测试插件入口。Generator 会基于此类生成导出函数和 plugin.json。
/// </summary>
[Plugin(
    Id = "ntest",
    Name = "原生测试插件",
    Version = "1.0.0",
    Description = "用于验证 Native 通道（进程隔离）端到端功能的测试插件。包含回显、数学运算和随机名言三个工具。",
    Tags = ["测试", "native", "示例"],
    Instructions = "这是一个测试插件。echo_message 用于回显消息，math_add 用于两数相加，random_quote 用于获取随机编程名言。")]
public static partial class Startup
{
    /// <summary>
    /// 注册插件所需的服务。
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<QuoteRepository>();
    }
}
