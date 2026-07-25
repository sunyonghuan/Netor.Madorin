using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeClientLifecycleStage7Tests
{
    private readonly TestContext _testContext;

    public RuntimeClientLifecycleStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The public API boundary test inspects the untrimmed test assembly.")]
    public void ClientAssembly_PublicSurface_DoesNotExposeTransportTypes()
    {
        var runtimeClientType = typeof(RuntimeClient);
        var exposedTypes = runtimeClientType.Assembly
            .GetExportedTypes()
            .SelectMany(GetPublicSignatureTypes)
            .Where(ContainsTransportType)
            .Select(type => type.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(
            exposedTypes,
            $"RuntimeClient public API exposes transport types: {string.Join(", ", exposedTypes)}");
        Assert.IsNull(runtimeClientType.GetProperty(
            nameof(RuntimeClient.ControlPeer),
            BindingFlags.Public | BindingFlags.Instance));
        Assert.IsNull(runtimeClientType.GetProperty(
            nameof(RuntimeClient.BlobChannel),
            BindingFlags.Public | BindingFlags.Instance));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CreateAsync_AttachExistingFailure_DoesNotLaunchConfiguredExecutable()
    {
        var options = new RuntimeClientOptions(
            $"madorin.stage7.missing.{Guid.NewGuid():N}",
            Guid.NewGuid().ToString("N"),
            ConnectTimeout: TimeSpan.FromMilliseconds(100),
            HandshakeSecret: "attach-secret",
            RuntimeExecutablePath: Path.Combine(Path.GetTempPath(), "must-not-launch.exe"),
            StartPolicy: RuntimeStartPolicy.AttachExisting,
            StartupTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsExactlyAsync<RuntimeClientConnectionException>(
            async () => await RuntimeClient.CreateAsync(
                options,
                _testContext.CancellationToken));

        Assert.IsTrue(exception.IsRetryable);
    }

    private static bool ContainsTransportType(Type type)
    {
        if (type.Namespace?.StartsWith("Madorin.AI.Runtime.Transport", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            return ContainsTransportType(elementType);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsTransportType);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The public API boundary test inspects the untrimmed test assembly.")]
    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        const BindingFlags publicDeclared = BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var interfaceType in type.GetInterfaces())
        {
            yield return interfaceType;
        }

        foreach (var constructor in type.GetConstructors(publicDeclared))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var method in type.GetMethods(publicDeclared))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties(publicDeclared))
        {
            yield return property.PropertyType;
        }

        foreach (var field in type.GetFields(publicDeclared))
        {
            yield return field.FieldType;
        }

        foreach (var eventInfo in type.GetEvents(publicDeclared))
        {
            if (eventInfo.EventHandlerType is { } eventHandlerType)
            {
                yield return eventHandlerType;
            }
        }
    }

    [TestMethod]
    public async Task CreateAsync_AlwaysStartWithMissingExecutable_ThrowsStartupException()
    {
        var options = CreateLaunchOptions(
            CreateWorkspacePath(),
            RuntimeStartPolicy.AlwaysStart) with
        {
            RuntimeExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")
        };

        await Assert.ThrowsExactlyAsync<RuntimeClientStartupException>(
            async () => await RuntimeClient.CreateAsync(
                options,
                _testContext.CancellationToken));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CreateAsync_StartIfMissingConnectionFailure_FallsBackToLaunch()
    {
        var options = CreateLaunchOptions(
            CreateWorkspacePath(),
            RuntimeStartPolicy.StartIfMissing) with
        {
            PipeName = $"madorin.stage7.missing.{Guid.NewGuid():N}",
            HandshakeSecret = "existing-runtime-secret",
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            RuntimeExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")
        };

        await Assert.ThrowsExactlyAsync<RuntimeClientStartupException>(
            async () => await RuntimeClient.CreateAsync(
                options,
                _testContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(RuntimeStartPolicy.AttachExisting)]
    [DataRow(RuntimeStartPolicy.StartIfMissing)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CreateAsync_ExistingRuntime_AttachesWithoutLaunchingConfiguredExecutable(
        RuntimeStartPolicy startPolicy)
    {
        var workspace = CreateWorkspacePath();
        var dataDirectory = Path.Combine(workspace, ".madorin");
        var instanceId = Guid.NewGuid().ToString("N");
        var pipePrefix = $"madorin.stage7.attach.{Guid.NewGuid():N}";
        var pipeName = $"{pipePrefix}.{instanceId[..8]}";
        var secret = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        Process? runtimeProcess = null;
        RuntimeClient? client = null;
        try
        {
            runtimeProcess = await StartRuntimeProcessAsync(
                workspace,
                dataDirectory,
                instanceId,
                pipePrefix,
                secret,
                _testContext.CancellationToken);
            var invalidExecutable = Path.Combine(
                Path.GetTempPath(),
                $"must-not-launch-{Guid.NewGuid():N}.exe");

            client = await RuntimeClient.CreateAsync(
                new RuntimeClientOptions(
                    pipeName,
                    Guid.NewGuid().ToString("N"),
                    ConnectTimeout: TimeSpan.FromSeconds(15),
                    HandshakeSecret: secret,
                    EnableBackgroundHeartbeat: false,
                    RuntimeExecutablePath: invalidExecutable,
                    WorkspaceDirectory: workspace,
                    DataDirectory: dataDirectory,
                    RuntimeInstanceId: instanceId,
                    PipePrefix: pipePrefix,
                    StartPolicy: startPolicy,
                    StartupTimeout: TimeSpan.FromSeconds(15)),
                _testContext.CancellationToken);

            var binding = client.InstanceBinding;
            Assert.AreEqual(RuntimeClientState.Connected, client.State);
            Assert.IsFalse(binding.IsProcessOwned);
            Assert.AreEqual(runtimeProcess.Id, binding.RuntimeProcessId);
            Assert.AreEqual(instanceId, binding.RuntimeInstanceId);
            Assert.AreEqual(Path.GetFullPath(workspace), binding.Workspace);
            Assert.AreEqual(pipeName, binding.ControlPipeName);

            await client.DisposeAsync();

            Assert.IsFalse(
                runtimeProcess.HasExited,
                $"{startPolicy} disposed a Runtime process it did not own.");
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await StopProcessAsync(runtimeProcess);
            await StopPidFileProcessAsync(dataDirectory);
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CreateAsync_InitializeRpcError_ThrowsProtocolException()
    {
        var exception = await AssertInitializationFailureAsync<RuntimeClientProtocolException>(
            (request, _) => new JsonRpcResponse(
                "2.0",
                request.Id,
                Error: new JsonRpcError(-32_000, "stage7 initialization rejected")));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("stage7 initialization rejected", exception.InnerException.Message);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CreateAsync_MissingBlobCapability_ThrowsHealthException()
    {
        var exception = await AssertInitializationFailureAsync<RuntimeClientHealthException>(
            (request, runtimeInstanceId) =>
            {
                var result = JsonSerializer.SerializeToElement(
                    new InitializeResponse(
                        runtimeInstanceId,
                        "1.0.0-stage7-fake",
                        ProtocolVersions.Current,
                        new RuntimeCapabilities(BlobTransfer: false),
                        new RuntimeLimits(),
                        "unused-event-pipe",
                        30),
                    RuntimeJsonContext.Default.InitializeResponse);
                return new JsonRpcResponse("2.0", request.Id, result);
            });

        Assert.Contains("Blob transfer", exception.Message);
        Assert.IsFalse(exception.IsRetryable);
    }

    [TestMethod]
    public void CreateRuntimeProcessStartInfo_PreservesEachArgumentWithoutShellParsing()
    {
        const string workspace = "  工作 区 \"quoted\"\nline  ";
        const string dataDirectory = "data 中文 \"quoted\"\nline ";
        const string logDirectory = " log 中文 \"quoted\"\nline  ";
        const string instanceId = "实例 id with spaces";
        const string pipePrefix = "  品牌.pipe \"quoted\"  ";
        const string secret = "must-never-appear-in-arguments";
        var options = new RuntimeClientOptions(
            string.Empty,
            "host",
            HandshakeSecret: secret,
            RuntimeExecutablePath: "runtime-host",
            WorkspaceDirectory: workspace,
            DataDirectory: dataDirectory,
            LogDirectory: logDirectory,
            RuntimeInstanceId: instanceId,
            PipePrefix: pipePrefix,
            StartPolicy: RuntimeStartPolicy.AlwaysStart);

        var startInfo = RuntimeClient.CreateRuntimeProcessStartInfo(options);

        Assert.AreEqual("runtime-host", startInfo.FileName);
        CollectionAssert.AreEqual(
            new[]
            {
                "serve",
                "--workspace",
                workspace,
                "--data-dir",
                dataDirectory,
                "--log-dir",
                logDirectory,
                "--instance",
                instanceId,
                "--pipe-prefix",
                pipePrefix
            },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.DoesNotContain(secret, startInfo.ArgumentList);
        Assert.DoesNotContain(secret, startInfo.Environment.Values);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CreateAsync_AlwaysStart_BindsOwnedProcessAndStopsItOnDispose()
    {
        var workspace = CreateWorkspacePath();
        var dataDirectory = Path.Combine(workspace, ".madorin data");
        RuntimeClient? client = null;
        Process? runtimeProcess = null;
        try
        {
            client = await RuntimeClient.CreateAsync(
                CreateLaunchOptions(workspace, RuntimeStartPolicy.AlwaysStart) with
                {
                    DataDirectory = dataDirectory,
                    LogDirectory = Path.Combine(workspace, "日志 logs"),
                    OwnedProcessShutdownTimeout = TimeSpan.FromMilliseconds(100)
                },
                _testContext.CancellationToken);

            var binding = client.InstanceBinding;
            Assert.AreEqual(RuntimeClientState.Connected, client.State);
            Assert.IsTrue(binding.IsProcessOwned);
            Assert.AreEqual(Path.GetFullPath(workspace), binding.Workspace);
            Assert.IsNotNull(binding.RuntimeProcessId);
            Assert.StartsWith("madorin.stage7.", binding.ControlPipeName);
            Assert.AreEqual($"{binding.ControlPipeName}.events", binding.EventPipeName);

            runtimeProcess = Process.GetProcessById(binding.RuntimeProcessId.Value);
            await client.DisposeAsync();
            await runtimeProcess.WaitForExitAsync(_testContext.CancellationToken);

            Assert.AreEqual(RuntimeClientState.Closed, client.State);
            Assert.IsTrue(runtimeProcess.HasExited);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await StopProcessAsync(runtimeProcess);
            await StopPidFileProcessAsync(dataDirectory);
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task DisposeAsync_KeepAlive_DoesNotStopOwnedRuntime()
    {
        var workspace = CreateWorkspacePath();
        var dataDirectory = Path.Combine(workspace, ".madorin");
        RuntimeClient? client = null;
        Process? runtimeProcess = null;
        try
        {
            client = await RuntimeClient.CreateAsync(
                CreateLaunchOptions(workspace, RuntimeStartPolicy.AlwaysStart) with
                {
                    DataDirectory = dataDirectory,
                    OwnedProcessClosePolicy = OwnedProcessClosePolicy.KeepAlive
                },
                _testContext.CancellationToken);
            runtimeProcess = Process.GetProcessById(client.InstanceBinding.RuntimeProcessId!.Value);

            await client.DisposeAsync();

            Assert.IsFalse(runtimeProcess.HasExited);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await StopProcessAsync(runtimeProcess);
            await StopPidFileProcessAsync(dataDirectory);
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CreateAsync_VersionMismatch_ReturnsStableExceptionAndCleansOwnedProcess()
    {
        const string expectedVersion = "0.0.0-stage7-mismatch";
        var workspace = CreateWorkspacePath();
        var dataDirectory = Path.Combine(workspace, ".madorin");
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<RuntimeClientVersionIncompatibleException>(
                async () => await RuntimeClient.CreateAsync(
                    CreateLaunchOptions(workspace, RuntimeStartPolicy.AlwaysStart) with
                    {
                        DataDirectory = dataDirectory,
                        ExpectedRuntimeVersion = expectedVersion,
                        OwnedProcessClosePolicy = OwnedProcessClosePolicy.KeepAlive,
                        OwnedProcessShutdownTimeout = TimeSpan.FromMilliseconds(100)
                    },
                    _testContext.CancellationToken));

            Assert.AreEqual(expectedVersion, exception.ExpectedVersion);
            Assert.IsNotNull(exception.ActualVersion);
            Assert.AreNotEqual(expectedVersion, exception.ActualVersion);
            await AssertPidFileProcessExitedAsync(dataDirectory);
        }
        finally
        {
            await StopPidFileProcessAsync(dataDirectory);
            DeleteWorkspace(workspace);
        }
    }

    private static RuntimeClientOptions CreateLaunchOptions(
        string workspace,
        RuntimeStartPolicy startPolicy)
    {
        var cliAssemblyPath = Path.Combine(AppContext.BaseDirectory, "madorin.dll");
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException(
                "The Runtime CLI assembly was not copied to the test output.",
                cliAssemblyPath);
        }

        return new RuntimeClientOptions(
            string.Empty,
            Guid.NewGuid().ToString("N"),
            RuntimeExecutablePath: cliAssemblyPath,
            WorkspaceDirectory: workspace,
            RuntimeInstanceId: Guid.NewGuid().ToString("N"),
            PipePrefix: "madorin.stage7",
            StartPolicy: startPolicy,
            StartupTimeout: TimeSpan.FromSeconds(15));
    }

    private async Task<TException> AssertInitializationFailureAsync<TException>(
        Func<JsonRpcRequest, string, JsonRpcResponse> responseFactory)
        where TException : Exception
    {
        var pipeName = $"madorin.stage7.fake.{Guid.NewGuid():N}";
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var runtimeInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        var serverTask = RunInitializationServerAsync(
            pipeName,
            hostInstanceId,
            runtimeInstanceId,
            secret,
            responseFactory,
            lifetime.Token);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<TException>(
                async () => await RuntimeClient.CreateAsync(
                    new RuntimeClientOptions(
                        pipeName,
                        hostInstanceId,
                        ConnectTimeout: TimeSpan.FromSeconds(5),
                        ExpectedRuntimeInstanceId: runtimeInstanceId,
                        HandshakeSecret: secret,
                        EnableBackgroundHeartbeat: false,
                        StartPolicy: RuntimeStartPolicy.AttachExisting),
                    _testContext.CancellationToken));
            await serverTask;
            return exception;
        }
        finally
        {
            await lifetime.CancelAsync();
            if (!serverTask.IsCompleted)
            {
                try
                {
                    await serverTask;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static async Task RunInitializationServerAsync(
        string pipeName,
        string expectedHostInstanceId,
        string runtimeInstanceId,
        string secret,
        Func<JsonRpcRequest, string, JsonRpcResponse> responseFactory,
        CancellationToken cancellationToken)
    {
        await using var transport = await NamedPipeTransport.CreateServerAsync(
            pipeName,
            cancellationToken);
        var handshake = new FramedChannel(transport);
        var clientHello = await handshake.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientHello,
            cancellationToken)
            ?? throw new EndOfStreamException("The Client closed during the test handshake.");
        if (!string.Equals(
            expectedHostInstanceId,
            clientHello.HostInstanceId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Client used an unexpected host instance identifier.");
        }

        var nonceC = Convert.FromBase64String(clientHello.NonceC);
        var nonceS = HandshakeProtocol.CreateNonce();
        await handshake.SendJsonAsync(
            new HandshakeServerHello(
                runtimeInstanceId,
                Convert.ToBase64String(nonceS),
                HandshakeProtocol.ComputeRuntimeResponse(
                    secret,
                    runtimeInstanceId,
                    clientHello.TimestampUnixMilliseconds,
                    nonceC,
                    nonceS)),
            cancellationToken);
        var confirmation = await handshake.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientConfirmation,
            cancellationToken)
            ?? throw new EndOfStreamException("The Client closed before confirming the test handshake.");
        var sessionKey = HandshakeProtocol.DeriveSessionKey(
            secret,
            expectedHostInstanceId,
            runtimeInstanceId,
            nonceC,
            nonceS);
        if (!string.Equals(
                expectedHostInstanceId,
                confirmation.HostInstanceId,
                StringComparison.Ordinal)
            || !HandshakeProtocol.VerifySessionProof(
                sessionKey,
                "control-channel",
                expectedHostInstanceId,
                runtimeInstanceId,
                confirmation.Proof))
        {
            throw new InvalidDataException("The Client confirmation proof was invalid.");
        }

        using var authenticated = new AuthenticatedFrameChannel(
            transport,
            sessionKey,
            "runtime-control",
            "host-control");
        await using var control = new FramedControlChannel(authenticated);
        control.SetRequestHandler((request, _) =>
        {
            if (!string.Equals(request.Method, "initialize", StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unexpected method '{request.Method}'.")));
            }

            return ValueTask.FromResult(responseFactory(request, runtimeInstanceId));
        });
        await control.Completion.WaitAsync(cancellationToken);
    }

    private static async Task<Process> StartRuntimeProcessAsync(
        string workspace,
        string dataDirectory,
        string instanceId,
        string pipePrefix,
        string secret,
        CancellationToken cancellationToken)
    {
        var cliAssemblyPath = Path.Combine(AppContext.BaseDirectory, "madorin.dll");
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException(
                "The Runtime CLI assembly was not copied to the test output.",
                cliAssemblyPath);
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add(cliAssemblyPath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(instanceId);
        startInfo.ArgumentList.Add("--pipe-prefix");
        startInfo.ArgumentList.Add(pipePrefix);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Runtime test process could not start.");
        try
        {
            await process.StandardInput.WriteLineAsync(
                secret.AsMemory(),
                cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            return process;
        }
        catch
        {
            await StopProcessAsync(process);
            throw;
        }
    }

    private static string CreateWorkspacePath() => Path.Combine(
        Path.GetTempPath(),
        "madorin stage7 中文",
        Guid.NewGuid().ToString("N"));

    private static async Task AssertPidFileProcessExitedAsync(string dataDirectory)
    {
        var pid = await TryReadPidAsync(dataDirectory);
        Assert.IsNotNull(pid, "The Runtime did not publish its PID before initialization.");
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            Assert.IsTrue(process.HasExited, $"Owned Runtime process {pid} was left running.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task StopPidFileProcessAsync(string dataDirectory)
    {
        var pid = await TryReadPidAsync(dataDirectory);
        if (pid is null)
        {
            return;
        }

        Process? process;
        try
        {
            process = Process.GetProcessById(pid.Value);
        }
        catch (ArgumentException)
        {
            return;
        }

        await StopProcessAsync(process);
    }

    private static async Task<int?> TryReadPidAsync(string dataDirectory)
    {
        var pidPath = Path.Combine(dataDirectory, "runtime.pid");
        if (!File.Exists(pidPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(pidPath);
        using var document = await JsonDocument.ParseAsync(stream);
        if (document.RootElement.TryGetProperty("pid", out var camelCasePid)
            && camelCasePid.TryGetInt32(out var pid))
        {
            return pid;
        }

        if (document.RootElement.TryGetProperty("Pid", out var pascalCasePid)
            && pascalCasePid.TryGetInt32(out pid))
        {
            return pid;
        }

        return null;
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
