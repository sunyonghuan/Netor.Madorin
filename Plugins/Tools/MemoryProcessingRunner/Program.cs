using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;
using Cortana.Plugins.Memory;
using Cortana.Plugins.Memory.Processing;

Console.WriteLine("MemoryProcessingRunner starting...");
var maxObservationCount = args.Length > 0 && int.TryParse(args[0], out var parsedLimit) ? parsedLimit : 100;

var services = new ServiceCollection();
services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Debug));

// Prepare plugin settings
var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "runner_data");
var workspaceDir = Path.Combine(Directory.GetCurrentDirectory(), "runner_workspace");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(workspaceDir);

var pluginDirectory = Directory.GetCurrentDirectory();
var settings = new PluginSettings(dataDir, workspaceDir, pluginDirectory, 0, string.Empty, string.Empty, string.Empty, string.Empty, 0);
services.AddSingleton(settings);

// Register plugin services via Startup
Cortana.Plugins.Memory.Startup.Configure(services);

var provider = services.BuildServiceProvider();

// Ensure DB initialized
var store = provider.GetRequiredService<Cortana.Plugins.Memory.Storage.IMemoryStore>();
store.EnsureInitialized();

var processing = provider.GetRequiredService<IMemoryProcessingService>();

var request = new MemoryProcessingRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    AgentId = null,
    WorkspaceId = null,
    MaxObservationCount = maxObservationCount,
    TriggerSource = "runner",
    TraceId = Guid.NewGuid().ToString("N")
};

Console.WriteLine($"Invoking Process... MaxObservationCount={maxObservationCount}");
var result = processing.Process(request);
Console.WriteLine($"Process finished: State={result.State}, Processed={result.ProcessedObservationCount}, CreatedFragments={result.CreatedFragmentCount}, CreatedAbstractions={result.CreatedAbstractionCount}");

Console.WriteLine("Done.");
