using DesktopPet.Ai;
using DesktopPet.Behaviors;
using DesktopPet.Configuration;
using DesktopPet.Platform.Win32;
using DesktopPet.Rendering.D3D11;
using System.Diagnostics;

if (args.Any(argument => string.Equals(argument, "--mcp", StringComparison.OrdinalIgnoreCase)))
{
    await new PetMcpServer().RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    return 0;
}

var paths = new DesktopPetAppPaths();
var logger = new DesktopPetLogger(paths);
var startupRegistration = new StartupRegistrationService();
var settingsStore = new DesktopPetSettingsStore(paths.ConfigurationDirectory);
GltfStartup.TryLogSelectedModel(args, logger);
var enableD3D11 = !args.Any(argument => string.Equals(argument, "--no-d3d11", StringComparison.OrdinalIgnoreCase));
var window = new DesktopPetWindow(settingsStore, useLayeredWindow: !enableD3D11);
window.Create();
WireWindowCommands(window, paths, startupRegistration, logger);

var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "live2d", "models");
var availableModels = Directory.Exists(modelsDirectory)
    ? Directory.GetDirectories(modelsDirectory)
        .Select(d => Path.GetFileName(d)!)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray()
    : [];

var behaviorStateMachine = new PetBehaviorStateMachine();
var disableLive2D = args.Any(argument => string.Equals(argument, "--no-live2d", StringComparison.OrdinalIgnoreCase));
var initialLive2DModel = disableLive2D ? null : Live2DStartup.TryLoadDefaultModel(args, logger);
if (disableLive2D)
{
    logger.Info("Live2D model loading disabled by --no-live2d.");
}
var initialMouthLoop = initialLive2DModel is null ? null : new SimpleMouthMotionLoop(initialLive2DModel);
using var behaviorRuntime = new PetBehaviorRuntime(behaviorStateMachine, initialMouthLoop, logger.Error);
initialMouthLoop?.Start();

// Mutable references to the active Live2D session; updated on model switch.
var activeLive2DModel = initialLive2DModel;
var activeMouthLoop = initialMouthLoop;

window.SetAvailableModels(availableModels);
window.CurrentModelName = activeLive2DModel?.Info.Name;
if (activeMouthLoop is not null && args.Any(argument => string.Equals(argument, "--preview-speak", StringComparison.OrdinalIgnoreCase)))
{
    behaviorRuntime.Apply(new PetEvent(PetEventKind.Speak, "预览说话中"));
    activeMouthLoop.SpeakFor(TimeSpan.FromSeconds(4));
}
else if (activeMouthLoop is not null && args.Any(argument => string.Equals(argument, "--preview-think", StringComparison.OrdinalIgnoreCase)))
{
    behaviorRuntime.Apply(new PetEvent(PetEventKind.Think, "思考中"));
}

var webSocketUri = ResolveWebSocketUri(args);
if (webSocketUri is not null)
{
    behaviorRuntime.StartWebSocket(webSocketUri);
}

using var renderHost = enableD3D11 ? TryCreateRenderHost(window, logger, args) : null;
if (!enableD3D11)
{
    logger.Info("D3D11 renderer disabled by --no-d3d11.");
}
var enableLive2DSubmit = !args.Any(argument => string.Equals(argument, "--no-live2d-submit", StringComparison.OrdinalIgnoreCase));
var enableLive2DMotion = !args.Any(argument => string.Equals(argument, "--no-live2d-motion", StringComparison.OrdinalIgnoreCase));
var activeLive2DLoop = activeLive2DModel is not null && renderHost is not null && enableLive2DSubmit
    ? new Live2DRenderSubmissionLoop(activeLive2DModel, renderHost, enableLive2DMotion)
    : null;
activeLive2DLoop?.Start();
using var preview3DSubmissionLoop =
    renderHost is not null && args.Any(argument => string.Equals(argument, "--preview-3d", StringComparison.OrdinalIgnoreCase))
        ? new Preview3DSubmissionLoop(renderHost)
        : null;
preview3DSubmissionLoop?.Start();
using var gltfMeshSubmissionLoop = renderHost is not null
    ? GltfStartup.TryCreateMeshSubmissionLoop(args, renderHost, logger)
    : null;
gltfMeshSubmissionLoop?.Start();

if (renderHost is not null)
{
    window.UsePlaceholderPaint = false;
    window.Resized += (_, e) => renderHost.Resize(e.Width, e.Height);
    window.MouseWheelScrolled += (_, direction) =>
    {
        const float step = 0.1f;
        renderHost.ModelScale = Math.Clamp(renderHost.ModelScale + direction * step, 0.1f, 5.0f);
    };

    // --preview-subtitle：启动后持续循环显示演示字幕
    if (args.Any(a => string.Equals(a, "--preview-subtitle", StringComparison.OrdinalIgnoreCase)))
    {
        var subtitleTexts = new[]
        {
            "你好！我是你的桌面助手 Cortana ✨",
            "今天天气不错，适合编程 ☀️",
            "字幕功能已就绪，支持中文与 Emoji 🎉",
            "这是第四条测试字幕，稍后会自动循环 🔁",
        };
        var subtitleIndex = 0;
        var subtitleTimer = new System.Threading.Timer(_ =>
        {
            renderHost.SetSubtitle(subtitleTexts[subtitleIndex % subtitleTexts.Length]);
            subtitleIndex++;
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        // Timer 生命周期绑定到 window.Closing
        window.Closing += (_, _) => subtitleTimer.Dispose();
    }
}

window.ModelSwitchRequested += (_, modelName) =>
{
    if (renderHost is null || !enableLive2DSubmit) return;

    // Tear down the current Live2D session.
    activeLive2DLoop?.Dispose();
    activeLive2DLoop = null;
    activeMouthLoop?.Dispose();
    activeMouthLoop = null;
    activeLive2DModel?.Dispose();
    activeLive2DModel = null;

    // Load the new model.
    var newModel = Live2DStartup.TryLoadModel(modelName, modelsDirectory, logger);
    if (newModel is null) return;

    var newMouthLoop = new SimpleMouthMotionLoop(newModel);
    var newLoop = new Live2DRenderSubmissionLoop(newModel, renderHost, enableLive2DMotion);

    activeLive2DModel = newModel;
    activeMouthLoop = newMouthLoop;
    activeLive2DLoop = newLoop;

    newMouthLoop.Start();
    newLoop.Start();

    behaviorRuntime.SetMouthMotionLoop(newMouthLoop);
    window.CurrentModelName = modelName;
};

var renderShutdownStarted = 0;
window.Closing += (_, _) =>
{
    if (Interlocked.Exchange(ref renderShutdownStarted, 1) != 0)
    {
        return;
    }

    logger.Info("DesktopPet render shutdown: stopping preview submission.");
    preview3DSubmissionLoop?.Dispose();
    logger.Info("DesktopPet render shutdown: stopping GLB mesh submission.");
    gltfMeshSubmissionLoop?.Dispose();
    logger.Info("DesktopPet render shutdown: stopping mouth motion.");
    activeMouthLoop?.Dispose();
    logger.Info("DesktopPet render shutdown: stopping Live2D submission.");
    activeLive2DLoop?.Dispose();
    logger.Info("DesktopPet render shutdown: disposing Live2D model.");
    activeLive2DModel?.Dispose();
    logger.Info("DesktopPet render shutdown: disposing D3D11 renderer.");
    renderHost?.Dispose();
    logger.Info("DesktopPet render shutdown completed.");
};

static Uri? ResolveWebSocketUri(string[] args)
{
    var value = ReadArgumentValue(args, "--ws");
    if (value is null && args.Any(argument => string.Equals(argument, "--ws", StringComparison.OrdinalIgnoreCase)))
    {
        return CortanaRealtimeClient.CreateDefaultUri();
    }

    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri
        : CortanaRealtimeClient.CreateDefaultUri(value);
}

static string? ReadArgumentValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    var prefix = name + "=";
    return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}

window.Show();
try
{
    logger.Info("DesktopPet started.");
    return window.RunMessageLoop();
}
catch (Exception ex)
{
    logger.Error(ex, "DesktopPet crashed");
    throw;
}

static D3D11RenderHost? TryCreateRenderHost(DesktopPetWindow window, DesktopPetLogger logger, string[] args)
{
    D3D11RenderHost? renderHost = null;

    try
    {
        renderHost = new D3D11RenderHost(new D3D11RenderSurface(window.Handle, window.Width, window.Height));
        renderHost.EnableLive2DClipping = !args.Any(argument => string.Equals(argument, "--no-live2d-clipping", StringComparison.OrdinalIgnoreCase));
        renderHost.EnableLive2DTextures = !args.Any(argument => string.Equals(argument, "--no-live2d-textures", StringComparison.OrdinalIgnoreCase));
        renderHost.EnableLive2DDrawing = !args.Any(argument => string.Equals(argument, "--no-live2d-draw", StringComparison.OrdinalIgnoreCase));
        renderHost.MaxLive2DDrawItems = ReadIntArgumentValue(args, "--live2d-max-draw", int.MaxValue);
        renderHost.Start();
        return renderHost;
    }
    catch (Exception ex)
    {
        renderHost?.Dispose();
        logger.Error(ex, "D3D11 renderer failed, fallback to placeholder paint");
        Console.Error.WriteLine($"D3D11 renderer failed, fallback to placeholder paint: {ex.Message}");
        return null;
    }
}

static int ReadIntArgumentValue(string[] args, string name, int defaultValue)
{
    var value = ReadArgumentValue(args, name);
    return int.TryParse(value, out var parsed) ? parsed : defaultValue;
}

static void WireWindowCommands(
    DesktopPetWindow window,
    DesktopPetAppPaths paths,
    StartupRegistrationService startupRegistration,
    DesktopPetLogger logger)
{
    window.OpenModelsDirectoryRequested += (_, _) => OpenDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "live2d", "models"),
        logger);
    window.OpenConfigurationDirectoryRequested += (_, _) => OpenDirectory(paths.ConfigurationDirectory, logger);
    window.OpenLogsDirectoryRequested += (_, _) => OpenDirectory(paths.LogsDirectory, logger);
    window.StartupToggleRequested += (_, _) =>
    {
        try
        {
            var enabled = !startupRegistration.IsEnabled();
            startupRegistration.SetEnabled(enabled, Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
            logger.Info($"Startup registration set to {enabled}.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to toggle startup registration");
        }
    };
    window.ExitRequested += (_, _) => logger.Info("DesktopPet exit requested.");
}

static void OpenDirectory(string directory, DesktopPetLogger logger)
{
    try
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(directory),
            UseShellExecute = false
        });
    }
    catch (Exception ex)
    {
        logger.Error(ex, $"Failed to open directory {directory}");
    }
}

static string QuoteArgument(string value)
{
    return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
