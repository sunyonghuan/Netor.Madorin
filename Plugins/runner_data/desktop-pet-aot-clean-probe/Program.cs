using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using Silk.NET.Maths;
using Silk.NET.Windowing.Glfw;
using Silk.NET.Windowing;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

if (args is ["--silk-window"])
{
    RunSilkWindowProbe();
    return;
}

if (args is ["--avalonia-window"])
{
    RunAvaloniaWindowProbe(args);
    return;
}

Console.WriteLine("DesktopPet clean AOT probe starting.");

RunSourceGeneratedJsonProbe();
RunAvaloniaProbe();
RunSilkWindowingProbe();
RunVorticeD3D11Probe();
RunMcpRegistrationProbe();

Console.WriteLine("DesktopPet clean AOT probe completed.");

static void RunSourceGeneratedJsonProbe()
{
    var message = new ProbeMessage("think", "AOT clean probe");
    var json = JsonSerializer.Serialize(message, ProbeJsonContext.Default.ProbeMessage);
    var roundTrip = JsonSerializer.Deserialize(json, ProbeJsonContext.Default.ProbeMessage);

    Console.WriteLine($"json: {roundTrip?.Command}/{roundTrip?.Text}");
}

static void RunAvaloniaProbe()
{
    var builder = AppBuilder.Configure<ProbeAvaloniaApp>().UsePlatformDetect();
    Console.WriteLine($"avalonia: {builder.GetType().Name}");
}

static void RunSilkWindowingProbe()
{
    var options = WindowOptions.Default;
    options.Size = new Vector2D<int>(240, 160);
    options.Title = "DesktopPet Clean AOT Probe";

    Console.WriteLine($"silk-windowing: {options.Title}/{options.Size.X}x{options.Size.Y}");
}

static void RunSilkWindowProbe()
{
    GlfwWindowing.Use();

    var options = WindowOptions.Default;
    options.Size = new Vector2D<int>(320, 220);
    options.Title = "DesktopPet Silk AOT Window";
    options.IsVisible = true;

    using var window = Silk.NET.Windowing.Window.Create(options);
    var frames = 0;

    window.Load += () => Console.WriteLine("silk-window: loaded");
    window.Render += _ =>
    {
        frames++;
        if (frames >= 5)
        {
            Console.WriteLine($"silk-window: closing after {frames} frames");
            window.Close();
        }
    };
    window.Closing += () => Console.WriteLine("silk-window: closing");

    window.Run();
    Console.WriteLine("silk-window: completed");
}

static void RunVorticeD3D11Probe()
{
    var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
    var result = D3D11.D3D11CreateDevice(
        (IDXGIAdapter?)null,
        DriverType.Hardware,
        DeviceCreationFlags.BgraSupport,
        featureLevels,
        out var device,
        out var selectedFeatureLevel,
        out var context);

    if (result.Failure)
    {
        Console.WriteLine($"vortice-d3d11: skipped ({result.Code})");
        return;
    }

    Console.WriteLine($"vortice-d3d11: {selectedFeatureLevel}");

    context.Dispose();
    device.Dispose();
}

static void RunMcpRegistrationProbe()
{
    var services = new ServiceCollection();
    services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<PetProbeTools>();

    Console.WriteLine($"mcp: services={services.Count}");
}

static void RunAvaloniaWindowProbe(string[] args)
{
    AppBuilder
        .Configure<ProbeAvaloniaWindowApp>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);
}

public sealed class ProbeAvaloniaApp : Application
{
}

public sealed class ProbeAvaloniaWindowApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Avalonia.Controls.Window
            {
                Width = 320,
                Height = 220,
                Title = "DesktopPet Avalonia AOT Window",
                Content = new TextBlock
                {
                    Text = "Avalonia AOT window probe",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };

            desktop.MainWindow = window;
            window.Opened += (_, _) =>
            {
                Console.WriteLine("avalonia-window: opened");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Console.WriteLine("avalonia-window: closing");
                    window.Close();
                };
                timer.Start();
            };
            window.Closed += (_, _) => Console.WriteLine("avalonia-window: completed");
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed record ProbeMessage(string Command, string Text);

[JsonSerializable(typeof(ProbeMessage))]
public sealed partial class ProbeJsonContext : JsonSerializerContext
{
}

[McpServerToolType]
public sealed class PetProbeTools
{
    [McpServerTool]
    [Description("Returns a static desktop pet status for AOT probing.")]
    public static string PetStatus() => "ok";
}
