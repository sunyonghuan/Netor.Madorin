using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class BuiltinExecutionToolTests
{
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"madorin-builtin-exec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProcessRun_AllowlistedDotnet_UsesArgumentListAndReturnsBoundedOutput()
    {
        var permission = CreatePermission(allowedExecutables: ["dotnet"]);
        var result = await ExecuteAsync(
            new BuiltinProcessToolExecutor(),
            BuiltinToolRegistry.ProcessRunToolId,
            """{"fileName":"dotnet","arguments":["--version"]}""",
            permission);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(output.RootElement.GetProperty("stdout").GetString(), ".");
    }

    [TestMethod]
    public async Task ProcessRun_UnallowlistedEnvironmentVariable_IsRejected()
    {
        var result = await ExecuteAsync(
            new BuiltinProcessToolExecutor(),
            BuiltinToolRegistry.ProcessRunToolId,
            """{"fileName":"dotnet","arguments":["--version"],"environment":{"SECRET":"value"}}""",
            CreatePermission(allowedExecutables: ["dotnet"]));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "SECRET");
    }

    [TestMethod]
    public async Task PowerShellRun_DefaultDisabled_IsRejected()
    {
        var result = await ExecuteAsync(
            new BuiltinPowerShellToolExecutor(),
            BuiltinToolRegistry.PowerShellRunToolId,
            """{"script":"Write-Output 'blocked'"}""",
            CreatePermission());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "disabled");
    }

    [TestMethod]
    public async Task PowerShellRun_ApprovedScript_ExecutesThroughStandardInput()
    {
        var result = await ExecuteAsync(
            new BuiltinPowerShellToolExecutor(),
            BuiltinToolRegistry.PowerShellRunToolId,
            """{"script":"Write-Output 'stage5-stdin'"}""",
            CreatePermission(allowPowerShell: true));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(
            output.RootElement.GetProperty("stdout").GetString(),
            "stage5-stdin");
    }

    [TestMethod]
    public async Task PowerShellRun_OutputExceedsLimit_ReturnsTruncationMarker()
    {
        var result = await ExecuteAsync(
            new BuiltinPowerShellToolExecutor(),
            BuiltinToolRegistry.PowerShellRunToolId,
            """{"script":"[Console]::Out.Write('x' * (1024 * 1024 + 4096))"}""",
            CreatePermission(allowPowerShell: true));

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("stdoutTruncated").GetBoolean());
        Assert.IsFalse(output.RootElement.GetProperty("stderrTruncated").GetBoolean());
        Assert.IsLessThanOrEqualTo(
            1024 * 1024,
            Encoding.UTF8.GetByteCount(
                output.RootElement.GetProperty("stdout").GetString() ?? string.Empty));
    }

    [TestMethod]
    public async Task PowerShellRun_Cancellation_KillsEntireProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Process-tree termination is exercised on Windows in this test.");
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var childExecutable = Path.Join(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(File.Exists(childExecutable), "Windows PowerShell was not found.");
        var pidPath = Path.Join(_root, "child.pid");
        var script = $$"""
            $child = Start-Process -FilePath '{{EscapePowerShellLiteral(childExecutable)}}' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60') -PassThru
            [System.IO.File]::WriteAllText('{{EscapePowerShellLiteral(pidPath)}}', [string]$child.Id)
            Wait-Process -Id $child.Id
            """;
        var invocation = new ToolInvocation(
            "call-process-tree",
            BuiltinToolRegistry.PowerShellRunToolId,
            "agent-1",
            ParentAgentId: null,
            CreateArguments(("script", script)),
            "run-1",
            "session-1",
            PermissionContext: CreatePermission(allowPowerShell: true));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        var execution = new BuiltinPowerShellToolExecutor().ExecuteAsync(
            invocation,
            cancellation.Token);
        var childPid = 0;

        try
        {
            await WaitForFileAsync(pidPath, TestContext.CancellationToken);
            childPid = int.Parse(
                await File.ReadAllTextAsync(pidPath, TestContext.CancellationToken),
                CultureInfo.InvariantCulture);

            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await execution);
            await WaitForProcessExitAsync(childPid, TestContext.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            TryTerminateProcess(childPid);
        }
    }

    [TestMethod]
    public async Task HttpRequest_DefaultDeny_IsRejectedBeforeConnect()
    {
        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"GET","url":"http://127.0.0.1/"}""",
            CreatePermission());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "denied");
    }

    [TestMethod]
    public async Task HttpRequest_ExplicitLoopbackPolicy_UsesPinnedAddress()
    {
        var server = StartServer(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello");
        await using var serverScope = server;
        var policy = CreateLoopbackPolicy(server.Port, maxResponseBytes: 1024);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/value"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(200, output.RootElement.GetProperty("statusCode").GetInt32());
        Assert.AreEqual("hello", output.RootElement.GetProperty("body").GetString());
    }

    [TestMethod]
    public async Task HttpRequest_ResponseExceedsPolicy_IsRejected()
    {
        var server = StartServer(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 8\r\nConnection: close\r\n\r\ntoo-long");
        await using var serverScope = server;
        var policy = CreateLoopbackPolicy(server.Port, maxResponseBytes: 4);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/large"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "limit");
    }

    [TestMethod]
    public async Task HttpRequest_LoopbackWithoutExplicitAddressRange_IsRejected()
    {
        var policy = new NetworkPolicy(
            DenyAll: false,
            AllowedHosts: ["127.0.0.1"],
            AllowedSchemes: ["http"],
            AllowedPorts: [8080]);
        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            """{"method":"GET","url":"http://127.0.0.1:8080/"}""",
            CreatePermission(networkPolicy: policy));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "unauthorized address");
    }

    [TestMethod]
    public async Task HttpRequest_RedirectToUnauthorizedAddress_IsRejectedBeforeSecondConnect()
    {
        var server = StartServer(
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var serverScope = server;
        var policy = new NetworkPolicy(
            DenyAll: false,
            AllowedHosts: ["127.0.0.1", "169.254.169.254"],
            AllowedSchemes: ["http"],
            AllowedPorts: [server.Port, 80],
            AllowedAddressRanges: ["127.0.0.0/8"],
            MaxRedirects: 1,
            MaxResponseBytes: 1024);
        var arguments = $$"""{"method":"GET","url":"http://127.0.0.1:{{server.Port}}/redirect"}""";

        var result = await ExecuteAsync(
            new BuiltinHttpToolExecutor(),
            BuiltinToolRegistry.HttpRequestToolId,
            arguments,
            CreatePermission(networkPolicy: policy));
        await server.Completion.WaitAsync(TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "unauthorized address");
    }

    private Task<ToolResult> ExecuteAsync(
        IToolExecutor executor,
        string toolId,
        string argumentsJson,
        ToolPermissionContext permission) => executor.ExecuteAsync(
        new ToolInvocation(
            Guid.NewGuid().ToString("N"),
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            PermissionContext: permission),
        TestContext.CancellationToken);

    private ToolPermissionContext CreatePermission(
        string[]? allowedExecutables = null,
        bool allowPowerShell = false,
        NetworkPolicy? networkPolicy = null) => new(
        "grant-1",
        "run-1",
        _root,
        [_root],
        [_root],
        AllowedExecutables: allowedExecutables ?? [],
        AllowPowerShell: allowPowerShell,
        DenyNetwork: networkPolicy?.DenyAll ?? true,
        AllowedEnvironmentVariables: [],
        NetworkPolicy: networkPolicy);

    private static NetworkPolicy CreateLoopbackPolicy(int port, long maxResponseBytes) => new(
        DenyAll: false,
        AllowedHosts: ["127.0.0.1"],
        AllowedSchemes: ["http"],
        AllowedPorts: [port],
        AllowedAddressRanges: ["127.0.0.0/8"],
        MaxRedirects: 0,
        MaxResponseBytes: maxResponseBytes);

    private static string CreateArguments(params (string Name, string Value)[] values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForProcessExitAsync(int processId, CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void TryTerminateProcess(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static TestHttpServer StartServer(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var completion = ServeOnceAsync(listener, response);
        return new TestHttpServer(listener, port, completion);
    }

    private static async Task ServeOnceAsync(TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (received.Length > 64 * 1024)
            {
                throw new InvalidDataException("Test HTTP request headers exceeded the limit.");
            }
        }

        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private sealed class TestHttpServer(
        TcpListener listener,
        int port,
        Task completion) : IAsyncDisposable
    {
        public Task Completion { get; } = completion;

        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
