using DesktopPet.Ai;
using DesktopPet.Behaviors;
using DesktopPet.Configuration;
using DesktopPet.Models.Gltf;
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

// ── Live2D 模型列表 ────────────────────────────────────────────────────────
var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "live2d", "models");
var availableModels = Directory.Exists(modelsDirectory)
    ? Directory.GetDirectories(modelsDirectory)
        .Select(d => Path.GetFileName(d)!)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray()
    : [];

// ── GLB/VRM 模型列表 ──────────────────────────────────────────────────────
var gltfModelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "gltf", "models");
var loader = new GltfModelLoader();
var availableGltfModels = Directory.Exists(gltfModelsDirectory)
    ? loader.FindModelFiles(gltfModelsDirectory)
        .Select(p => Path.GetFileNameWithoutExtension(p)!)
        .Where(n => !string.IsNullOrWhiteSpace(n)
                    && !string.Equals(n, "Triangle", StringComparison.OrdinalIgnoreCase)) // 跳过最小测试用三角形
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

// 在 behaviorRuntime 创建之后才能注册设置对话框（需要传入 behaviorRuntime）
WireWindowCommands(window, paths, startupRegistration, settingsStore, behaviorRuntime, logger);

// Mutable references to the active Live2D session; updated on model switch.
var activeLive2DModel = initialLive2DModel;
var activeMouthLoop = initialMouthLoop;

window.SetAvailableModels(availableModels);
window.CurrentModelName = activeLive2DModel?.Info.Name;
window.SetAvailableGltfModels(availableGltfModels);

if (activeMouthLoop is not null && args.Any(argument => string.Equals(argument, "--preview-speak", StringComparison.OrdinalIgnoreCase)))
{
    behaviorRuntime.Apply(new PetEvent(PetEventKind.Speak, "预览说话中"));
    activeMouthLoop.SpeakFor(TimeSpan.FromSeconds(4));
}
else if (activeMouthLoop is not null && args.Any(argument => string.Equals(argument, "--preview-think", StringComparison.OrdinalIgnoreCase)))
{
    behaviorRuntime.Apply(new PetEvent(PetEventKind.Think, "思考中"));
}

// ── WebSocket 连接：命令行优先，否则读取持久化设置（AutoConnect）──────────
var webSocketUri = ResolveWebSocketUri(args);
if (webSocketUri is null)
{
    var conn = settingsStore.Load().Connection;
    if (conn.AutoConnect)
    {
        webSocketUri = CortanaRealtimeClient.CreateDefaultUri(conn.Host, conn.Port);
        logger.Info($"AutoConnect enabled: connecting to {webSocketUri}");
    }
}
if (webSocketUri is not null)
{
    behaviorRuntime.StartWebSocket(webSocketUri);
}

using var renderHost = enableD3D11 ? TryCreateRenderHost(window, logger, args) : null;
if (!enableD3D11)
{
    logger.Info("D3D11 renderer disabled by --no-d3d11.");
}

// ── 字幕联动：把 AI 行为事件里的字幕推给渲染器 ───────────────────────────
if (renderHost is not null)
{
    behaviorRuntime.SubtitleChanged += subtitle => renderHost.SetSubtitle(subtitle);
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

// ── GLB/VRM mesh submission（可在运行时热切换）────────────────────────────
// 启动时若命令行指定了 --gltf-model，则立即加载
var activeGltfLoop = renderHost is not null
    ? GltfStartup.TryCreateMeshSubmissionLoop(args, renderHost, logger)
    : null;
activeGltfLoop?.Start();
window.CurrentGltfModelName = activeGltfLoop is not null
    ? ReadArgumentValue(args, "--gltf-model")
    : null;

if (renderHost is not null)
{
    window.UsePlaceholderPaint = false;
    window.Resized += (_, e) => renderHost.Resize(e.Width, e.Height);
    window.MouseWheelScrolled += (_, direction) =>
    {
        const float step = 0.1f;
        renderHost.ModelScale = Math.Clamp(renderHost.ModelScale + direction * step, 0.1f, 5.0f);
    };

    // 鼠标进入/离开窗口时显示/隐藏边框提示线
    window.MouseHoverChanged += (_, inside) => renderHost.ShowBorder = inside;

    // Right-mouse-drag: rotate the 3D mesh model.
    // Sensitivity: 0.5° per pixel (≈ 0.00873 rad/px).
    window.RightMouseDragged += (_, drag) =>
    {
        const float sensitivity = 0.5f * MathF.PI / 180f;
        var (yaw, pitch) = renderHost.MeshExtraRotation;
        yaw   += drag.DeltaX * sensitivity;
        pitch  = Math.Clamp(pitch + drag.DeltaY * sensitivity, -MathF.PI / 2f, MathF.PI / 2f);
        renderHost.MeshExtraRotation = (yaw, pitch);
    };

    // --preview-subtitle：启动后持续循环显示演示字幕
    if (args.Any(a => string.Equals(a, "--preview-subtitle", StringComparison.OrdinalIgnoreCase)))
    {
        var subtitleTexts = new[]
        {
            "你好！我是你的桌面助手 Cortana",
            "今天天气不错，适合编程",
            "字幕功能已就绪，支持中文",
            "这是第四条测试字幕，稍后会自动循环",
        };
        var subtitleIndex = 0;
        var subtitleTimer = new System.Threading.Timer(_ =>
        {
            renderHost.SetSubtitle(subtitleTexts[subtitleIndex % subtitleTexts.Length]);
            subtitleIndex++;
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        window.Closing += (_, _) => subtitleTimer.Dispose();
    }
}

// ── Live2D 模型热切换 ─────────────────────────────────────────────────────
window.ModelSwitchRequested += (_, modelName) =>
{
    if (renderHost is null || !enableLive2DSubmit) return;

    // 切换到 Live2D 时先停掉 GLB（两者互斥）
    activeGltfLoop?.Dispose();
    activeGltfLoop = null;
    window.CurrentGltfModelName = null;

    activeLive2DLoop?.Dispose();
    activeLive2DLoop = null;
    activeMouthLoop?.Dispose();
    activeMouthLoop = null;
    activeLive2DModel?.Dispose();
    activeLive2DModel = null;

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

// ── GLB/VRM 模型热切换 ────────────────────────────────────────────────────
window.GltfModelSwitchRequested += (_, modelName) =>
{
    if (renderHost is null) return;

    // 切换到 GLB 时先停掉 Live2D（两者互斥）
    activeLive2DLoop?.Dispose();
    activeLive2DLoop = null;
    renderHost.SubmitRenderItems([]);  // 立即清空 Live2D 画面，不等渲染线程
    activeMouthLoop?.Dispose();
    activeMouthLoop = null;
    activeLive2DModel?.Dispose();
    activeLive2DModel = null;
    window.CurrentModelName = null;
    behaviorRuntime.SetMouthMotionLoop(null);

    // 停掉当前 GLB 循环，并立即清空 mesh 队列避免旧模型残留
    activeGltfLoop?.Dispose();
    activeGltfLoop = null;
    renderHost.SubmitMeshItems([]);   // 确保旧帧立即清空，不等渲染线程
    window.CurrentGltfModelName = null;

    // 加载并启动新模型
    var newLoop = GltfStartup.TryCreateMeshSubmissionLoopByName(modelName, gltfModelsDirectory, renderHost, logger);
    if (newLoop is null) return;

    newLoop.Start();
    activeGltfLoop = newLoop;
    window.CurrentGltfModelName = modelName;
    logger.Info($"GLB/VRM model switched: {modelName}");
};

// ── 关闭时清理 ────────────────────────────────────────────────────────────
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
    activeGltfLoop?.Dispose();
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
    DesktopPetSettingsStore settingsStore,
    PetBehaviorRuntime behaviorRuntime,
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
    window.SettingsRequested += (_, _) =>
    {
        try
        {
            var dialog = new DesktopPetSettingsDialog();
            var newConn = dialog.ShowModal(window.Handle, window.ConnectionSettings);
            if (newConn is null) return; // 用户取消

            window.SaveConnectionSettings(newConn);
            logger.Info($"Connection settings saved: {newConn.Host}:{newConn.Port}, AutoConnect={newConn.AutoConnect}");

            // 立即用新设置重启 WebSocket 连接
            var newUri = CortanaRealtimeClient.CreateDefaultUri(newConn.Host, newConn.Port);
            behaviorRuntime.RestartWebSocket(newUri);
            logger.Info($"WebSocket restarted: {newUri}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Settings dialog error");
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
