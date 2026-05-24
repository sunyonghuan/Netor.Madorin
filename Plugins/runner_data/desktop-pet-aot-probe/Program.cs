using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using SharpGLTF.Schema2;

using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

Console.WriteLine("DesktopPet AOT probe starting.");

RunSourceGeneratedJsonProbe();
RunAvaloniaProbe();
RunSilkWindowingProbe();
RunVorticeD3D11Probe();
RunSharpGltfProbe();
RunMcpRegistrationProbe();

Console.WriteLine("DesktopPet AOT probe completed.");

static void RunSourceGeneratedJsonProbe()
{
    var message = new ProbeMessage("say", "hello from AOT probe");
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
    options.Size = new Vector2D<int>(320, 240);
    options.Title = "DesktopPet AOT Probe";

    Console.WriteLine($"silk-windowing: {options.Title}/{options.Size.X}x{options.Size.Y}");
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

    context?.Dispose();
    device?.Dispose();
}

static void RunSharpGltfProbe()
{
    var path = Path.Combine(Path.GetTempPath(), $"desktop-pet-aot-probe-{Guid.NewGuid():N}.glb");
    var model = ModelRoot.CreateModel();
    model.Asset.Generator = "DesktopPetAotProbe";
    model.SaveGLB(path);

    var loaded = ModelRoot.Load(path);
    Console.WriteLine($"sharpgltf: scenes={loaded.LogicalScenes.Count}");

    File.Delete(path);
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

public sealed class ProbeAvaloniaApp : Application
{
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
