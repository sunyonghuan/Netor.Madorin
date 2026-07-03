using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Networks;

using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(b => b.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Information))
    .ConfigureServices(services =>
    {
        services
            .AddHttpClient()
            .AddSingleton<CortanaDbContext>()
            .AddTransient<SystemSettingsService>()
            .AddSingleton<WebSocketFeedServerService>()
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketFeedServerService>());
    })
    .Build();

await host.StartAsync();
Console.WriteLine("FeedHostConsole started. Press Ctrl+C to exit.");
await Task.Delay(TimeSpan.FromSeconds(30));
