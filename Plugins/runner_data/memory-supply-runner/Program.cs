using Cortana.Plugins.Memory;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

var agentId = args.Length > 0 ? args[0] : "800efd2e3668454ea0d506ce19e0465c";
var currentTask = args.Length > 1 ? args[1] : "分析工作流、提示词设计、输出风格和交易系统设计偏好";
var workspaceId = args.Length > 2 ? NullIfEmpty(args[2]) : null;
var useControlHandler = args.Any(static arg => string.Equals(arg, "--control", StringComparison.OrdinalIgnoreCase));

Console.WriteLine($"MemorySupplyRunner AgentId={agentId}");
Console.WriteLine($"WorkspaceId={workspaceId ?? "<null>"}");
Console.WriteLine($"CurrentTask={currentTask}");
Console.WriteLine($"Mode={(useControlHandler ? "control-handler" : "service")}");

var services = new ServiceCollection();
services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Warning));

var runnerDir = AppContext.BaseDirectory;
var projectDir = FindAncestorWithFile(runnerDir, "MemoryRecallRunner.csproj") ?? Path.GetFullPath(@"e:\Netor.me\Cortana\Plugins\Tools\MemoryRecallRunner");
var dataDir = Path.Combine(projectDir, "data");
var workspaceDir = Path.Combine(projectDir, "runner_workspace");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(workspaceDir);

var pluginDirectory = projectDir;
var settings = new PluginSettings(dataDir, workspaceDir, pluginDirectory, 0, string.Empty, string.Empty, string.Empty, string.Empty, 0);
services.AddSingleton(settings);
Startup.Configure(services);

using var provider = services.BuildServiceProvider();

if (useControlHandler)
{
    var handler = provider.GetRequiredService<MemorySupplyControlHandler>();
    var package = handler.Handle(new MemoryContextSupplyRequest
    {
        AgentId = agentId,
        WorkspaceId = workspaceId,
        WorkspaceDirectory = workspaceId,
        Scenario = "chat",
        CurrentTask = currentTask,
        SessionTitle = "供应测试",
        RecentMessages = [new MemoryContextMessage { Role = "user", Content = currentTask }],
        TriggerSource = "supply-runner-control",
        MaxMemoryCount = 12,
        MaxTokenBudget = 1200,
        TraceId = Guid.NewGuid().ToString("N")
    });

    Console.WriteLine(package.Summary);
    Console.WriteLine($"Enabled={package.Enabled}, Confidence={package.Confidence:0.0000}, Groups={package.Groups.Count}, Items={package.Items.Count}");
    Console.WriteLine($"Budget: max={package.Budget.MaxMemoryCount}, used={package.Budget.UsedMemoryCount}, tokens={package.Budget.EstimatedTokens}");
    Console.WriteLine($"Policy: ranking={package.AppliedPolicy.Ranking}, minConfidence={package.AppliedPolicy.RecallMinimumConfidence}");

    var packageIndex = 0;
    foreach (var item in package.Items)
    {
        packageIndex++;
        Console.WriteLine($"#{packageIndex} [{item.Kind}] id={item.Id} topic={item.Topic} score={item.Score:0.0000} confidence={item.Confidence:0.00} state={item.LifecycleState}/{item.ConfirmationState}");
        Console.WriteLine($"title={item.Title}");
        Console.WriteLine($"content={TrimForConsole(item.Content, 280)}");
        Console.WriteLine("---");
    }

    return;
}

var supply = provider.GetRequiredService<IMemorySupplyService>();
var result = supply.Supply(new MemorySupplyRequest
{
    AgentId = agentId,
    WorkspaceId = workspaceId,
    Scenario = "chat",
    CurrentTask = currentTask,
    SessionTitle = "供应测试",
    RecentMessages = [$"user: {currentTask}"],
    TriggerSource = "supply-runner",
    MaxMemoryCount = 12,
    MaxTokenBudget = 1200,
    TraceId = Guid.NewGuid().ToString("N")
});

Console.WriteLine(result.Summary);
Console.WriteLine($"Enabled={result.Enabled}, Confidence={result.Confidence:0.0000}, Groups={result.Groups.Count}, Items={result.Items.Count}");
Console.WriteLine($"Budget: max={result.Budget.MaxMemoryCount}, used={result.Budget.UsedMemoryCount}, tokens={result.Budget.EstimatedTokens}");
Console.WriteLine($"Policy: ranking={result.AppliedPolicy.Ranking}, minConfidence={result.AppliedPolicy.RecallMinimumConfidence}");

var index = 0;
foreach (var item in result.Items)
{
    index++;
    Console.WriteLine($"#{index} [{item.Kind}] id={item.Id} topic={item.Topic} score={item.Score:0.0000} confidence={item.Confidence:0.00} state={item.LifecycleState}/{item.ConfirmationState}");
    Console.WriteLine($"title={item.Title}");
    Console.WriteLine($"content={TrimForConsole(item.Content, 280)}");
    Console.WriteLine("---");
}

static string TrimForConsole(string value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return value.Length <= maxLength ? value : value[..maxLength];
}

static string? NullIfEmpty(string? value)
{
    return string.IsNullOrWhiteSpace(value) || string.Equals(value, "<null>", StringComparison.OrdinalIgnoreCase)
        ? null
        : value.Trim();
}

static string? FindAncestorWithFile(string startDirectory, string fileName)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, fileName))) return directory.FullName;
        directory = directory.Parent;
    }

    return null;
}
