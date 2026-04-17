using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Reminder;

[Plugin(
    Id = "reminder",
    Name = "定时提醒插件",
    Version = "1.0.5",
    Description = "提供定时提醒和循环任务功能。支持单次、每天、每周、每月和自定义间隔的提醒，到期后自动通过 AI 通知用户。",
    Tags = ["提醒", "定时", "日程"],
    Instructions = "重要：创建或修改提醒前，必须先调用 get_current_time 获取系统当前真实时间，严禁自行假设或编造当前时间。使用 create_reminder 创建提醒(支持 once/daily/weekly/monthly/custom 重复类型，可用逗号分隔添加标签)，list_reminders 查看提醒(支持按标签筛选)，search_reminders 按关键词搜索，update_reminder 修改提醒(含标签)，delete_reminder 删除单条提醒，delete_reminders_by_tag 批量删除某标签下所有提醒。提醒到期后会自动通知。")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();

        services.AddSingleton<ILoggerProvider>(sp =>
        {
            var settings = sp.GetRequiredService<PluginSettings>();
            var logsDirectory = Path.Combine(settings.PluginDirectory, "logs");
            return new HourlyFileLoggerProvider(logsDirectory);
        });

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<PluginSettings>();
            return new ReminderStore(settings.DataDirectory);
        });

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<PluginSettings>();
            var logger = sp.GetRequiredService<ILogger<CortanaWsClient>>();
            return new CortanaWsClient(settings.WsPort, logger);
        });

        services.AddHostedService<ReminderScheduler>();
    }
}