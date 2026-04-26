using Cortana.Plugins.Memory;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

var queryText = args.Length > 0 ? string.Join(' ', args) : "TikTok 连接器";
Console.WriteLine($"MemoryRecallRunner query: {queryText}");

var services = new ServiceCollection();
services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Information));

var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "runner_data");
var workspaceDir = Path.Combine(Directory.GetCurrentDirectory(), "runner_workspace");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(workspaceDir);

var pluginDirectory = Directory.GetCurrentDirectory();
var settings = new PluginSettings(dataDir, workspaceDir, pluginDirectory, 0, string.Empty, string.Empty, string.Empty, string.Empty, 0);
services.AddSingleton(settings);
Startup.Configure(services);

var provider = services.BuildServiceProvider();
var recall = provider.GetRequiredService<IMemoryRecallService>();

var result = recall.Recall(new MemoryRecallRequest
{
    AgentId = "global",
    WorkspaceId = null,
    QueryText = queryText,
    QueryIntent = "manual-validation",
    TriggerSource = "runner",
    TraceId = Guid.NewGuid().ToString("N")
});

Console.WriteLine(result.Summary);
Console.WriteLine($"Confidence={result.Confidence:0.0000}, Windows={result.Windows.Count}, Items={result.Items.Count}");

var index = 0;
foreach (var item in result.Items.Take(10))
{
    index++;
    Console.WriteLine($"#{index} [{item.Kind}] topic={item.Topic} score={item.RecallScore:0.0000} confidence={item.Confidence:0.00} state={item.LifecycleState}/{item.ConfirmationState}");
    Console.WriteLine($"title={item.Title}");
    Console.WriteLine($"summary={TrimForConsole(item.Summary, 240)}");
    Console.WriteLine("---");
}

static string TrimForConsole(string value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return value.Length <= maxLength ? value : value[..maxLength];
}
