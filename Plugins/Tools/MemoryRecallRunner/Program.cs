using Cortana.Plugins.Memory;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

var (agentId, workspaceId, queryArgs) = ParseArgs(args);
var queryText = queryArgs.Count > 0 ? string.Join(' ', queryArgs) : "TikTok 连接器";
Console.WriteLine($"MemoryRecallRunner query: {queryText}");
Console.WriteLine($"AgentId={agentId ?? "<all>"}, WorkspaceId={workspaceId ?? "<all>"}");

var services = new ServiceCollection();
services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Information));

var runnerDir = AppContext.BaseDirectory;
var projectDir = FindAncestorWithFile(runnerDir, "MemoryRecallRunner.csproj") ?? Directory.GetCurrentDirectory();
var dataDir = Path.Combine(projectDir, "data");
var workspaceDir = Path.Combine(projectDir, "runner_workspace");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(workspaceDir);

var pluginDirectory = projectDir;
var settings = new PluginSettings(dataDir, workspaceDir, pluginDirectory, 0, string.Empty, string.Empty, string.Empty, string.Empty, 0);
services.AddSingleton(settings);
Startup.Configure(services);

var provider = services.BuildServiceProvider();
var recall = provider.GetRequiredService<IMemoryRecallService>();

var result = recall.Recall(new MemoryRecallRequest
{
    AgentId = agentId,
    WorkspaceId = workspaceId,
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

static (string? AgentId, string? WorkspaceId, List<string> QueryArgs) ParseArgs(string[] args)
{
    string? agentId = null;
    string? workspaceId = null;
    var queryArgs = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            agentId = args[++i];
            continue;
        }

        if (string.Equals(arg, "--workspace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            workspaceId = args[++i];
            continue;
        }

        queryArgs.Add(arg);
    }

    return (agentId, workspaceId, queryArgs);
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
