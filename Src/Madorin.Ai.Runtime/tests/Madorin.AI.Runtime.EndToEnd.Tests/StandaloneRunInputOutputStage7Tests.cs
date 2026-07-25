using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneRunInputOutputStage7Tests
{
    private readonly TestContext _testContext;

    public StandaloneRunInputOutputStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void RunForTests_ConflictingInputAndOutputFormat_AreRejectedBeforeRuntimeStarts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var provider = new CapturingProvider();
            var dataDirectory = Path.Combine(root, "data");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var conflictExitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--data-dir", dataDirectory,
                    "--input", "inline",
                    "--input-file", Path.Combine(root, "missing.txt")
                ],
                stdout,
                new StringReader(string.Empty),
                Path.Combine(root, "config"),
                _ => _ => provider,
                error: stderr);

            Assert.AreEqual(ExitCodes.InvalidArguments, conflictExitCode);
            Assert.AreEqual(string.Empty, stdout.ToString());
            Assert.Contains("cannot be used together", stderr.ToString(), StringComparison.Ordinal);
            Assert.IsFalse(Directory.Exists(dataDirectory));
            Assert.IsEmpty(provider.Requests);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            var formatExitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--data-dir", dataDirectory,
                    "--input", "inline",
                    "--json",
                    "--output-format", "jsonl"
                ],
                stdout,
                new StringReader(string.Empty),
                Path.Combine(root, "config"),
                _ => _ => provider,
                error: stderr);

            Assert.AreEqual(ExitCodes.InvalidArguments, formatExitCode);
            Assert.AreEqual(string.Empty, stderr.ToString());
            using var errorDocument = JsonDocument.Parse(stdout.ToString());
            Assert.IsFalse(errorDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                "InvalidOutputFormat",
                errorDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.IsFalse(Directory.Exists(dataDirectory));
            Assert.IsEmpty(provider.Requests);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            var interactiveJsonExitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--data-dir", dataDirectory,
                    "--json"
                ],
                stdout,
                new StringReader(string.Empty),
                Path.Combine(root, "config"),
                _ => _ => provider,
                error: stderr);

            Assert.AreEqual(ExitCodes.InvalidArguments, interactiveJsonExitCode);
            Assert.AreEqual(string.Empty, stderr.ToString());
            using var interactiveErrorDocument = JsonDocument.Parse(stdout.ToString());
            Assert.AreEqual(
                "InteractiveOutputOptionsUnsupported",
                interactiveErrorDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.IsFalse(Directory.Exists(dataDirectory));
            Assert.IsEmpty(provider.Requests);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_InputFile_PreservesUtf8BomWhitespaceAndNewlines()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "工作区 with spaces");
            var inputPath = Path.Combine(root, "输入 prompt 文件.txt");
            var inputText = "  第一行 \"quoted\"  \r\n第二行\n末尾空白  ";
            await WriteConfigurationAsync(configDirectory);
            Directory.CreateDirectory(workspace);
            var inputBytes = Encoding.UTF8.GetBytes(inputText);
            await File.WriteAllBytesAsync(
                inputPath,
                [0xEF, 0xBB, 0xBF, .. inputBytes],
                _testContext.CancellationToken);

            var provider = new CapturingProvider();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--input-file", inputPath,
                    "--no-stream"
                ],
                stdout,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider,
                error: stderr);

            Assert.AreEqual(ExitCodes.Success, exitCode, stderr.ToString());
            Assert.AreEqual(ExpectedReply + Environment.NewLine, stdout.ToString());
            Assert.AreEqual(string.Empty, stderr.ToString());
            var request = Assert.ContainsSingle(provider.Requests);
            var userText = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.User)
                .SelectMany(static message => message.Content)
                .OfType<TextContentBlock>()
                .Last()
                .Text;
            Assert.AreEqual(inputText, userText);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RunForTests_TextJsonAndJsonLines_RespectStreamingContracts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            await WriteConfigurationAsync(configDirectory);

            var streamingText = RunOnce(
                root,
                configDirectory,
                "text-stream",
                ["--output-format", "text"]);
            var bufferedText = RunOnce(
                root,
                configDirectory,
                "text-buffered",
                ["--output-format", "text", "--no-stream"]);
            Assert.AreEqual(ExitCodes.Success, streamingText.ExitCode, streamingText.Stderr);
            Assert.AreEqual(ExitCodes.Success, bufferedText.ExitCode, bufferedText.Stderr);
            Assert.AreEqual(ExpectedReply + Environment.NewLine, streamingText.Stdout);
            Assert.AreEqual(streamingText.Stdout, bufferedText.Stdout);

            var json = RunOnce(
                root,
                configDirectory,
                "json",
                ["--output-format", "json"]);
            Assert.AreEqual(ExitCodes.Success, json.ExitCode, json.Stderr);
            Assert.AreEqual(string.Empty, json.Stderr);
            Assert.DoesNotContain('\u001b', json.Stdout);
            using (var document = JsonDocument.Parse(json.Stdout))
            {
                var result = document.RootElement;
                Assert.IsTrue(result.GetProperty("success").GetBoolean());
                Assert.AreEqual(
                    ExpectedReply,
                    result.GetProperty("data").GetProperty("text").GetString());
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    result.GetProperty("data").GetProperty("sessionId").GetString()));
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    result.GetProperty("data").GetProperty("runId").GetString()));
                Assert.AreEqual(JsonValueKind.Array, result.GetProperty("warnings").ValueKind);
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("diagnosticId").ValueKind);
            }

            var jsonLines = RunOnce(
                root,
                configDirectory,
                "jsonl-stream",
                ["--output-format", "jsonl"]);
            Assert.AreEqual(ExitCodes.Success, jsonLines.ExitCode, jsonLines.Stderr);
            Assert.AreEqual(string.Empty, jsonLines.Stderr);
            var lines = GetNonEmptyLines(jsonLines.Stdout);
            Assert.IsGreaterThan(2, lines.Length);
            foreach (var line in lines)
            {
                using var lineDocument = JsonDocument.Parse(line);
                Assert.IsTrue(lineDocument.RootElement.TryGetProperty("success", out _));
            }

            using (var terminalDocument = JsonDocument.Parse(lines[^1]))
            {
                var terminal = terminalDocument.RootElement;
                Assert.IsTrue(terminal.GetProperty("success").GetBoolean());
                Assert.AreEqual(
                    MessageTypes.RunCompleted,
                    terminal.GetProperty("data")
                        .GetProperty("event")
                        .GetProperty("messageType")
                        .GetString());
            }

            var bufferedJsonLines = RunOnce(
                root,
                configDirectory,
                "jsonl-buffered",
                ["--output-format", "jsonl", "--no-stream"]);
            Assert.AreEqual(ExitCodes.Success, bufferedJsonLines.ExitCode, bufferedJsonLines.Stderr);
            var bufferedLines = GetNonEmptyLines(bufferedJsonLines.Stdout);
            Assert.HasCount(1, bufferedLines);
            using var bufferedTerminalDocument = JsonDocument.Parse(bufferedLines[0]);
            Assert.IsTrue(bufferedTerminalDocument.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_OutputFile_AtomicallyReplacesExistingFileAndKeepsStdoutEmpty()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var outputPath = Path.Combine(root, "result.json");
            await WriteConfigurationAsync(configDirectory);
            await File.WriteAllTextAsync(
                outputPath,
                "old-result",
                _testContext.CancellationToken);

            var result = RunOnce(
                root,
                configDirectory,
                "output-success",
                ["--output-format", "json", "--output-file", outputPath]);

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.Stderr);
            Assert.AreEqual(string.Empty, result.Stdout);
            Assert.AreEqual(string.Empty, result.Stderr);
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(outputPath, _testContext.CancellationToken));
            Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                ExpectedReply,
                document.RootElement.GetProperty("data").GetProperty("text").GetString());
            Assert.IsEmpty(Directory.GetFiles(root, ".result.json.*.tmp"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_OutputCommitFailure_PreservesExistingFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var outputPath = Path.Combine(root, "locked-result.txt");
            const string oldResult = "previous-valid-result";
            await WriteConfigurationAsync(configDirectory);
            await File.WriteAllTextAsync(outputPath, oldResult, _testContext.CancellationToken);

            using (var heldFile = new FileStream(
                       outputPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                var result = RunOnce(
                    root,
                    configDirectory,
                    "output-locked",
                    ["--output-file", outputPath]);

                Assert.AreEqual(ExitCodes.WorkspaceError, result.ExitCode);
                Assert.AreEqual(string.Empty, result.Stdout);
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.Stderr));
                Assert.AreEqual(oldResult, await File.ReadAllTextAsync(
                    outputPath,
                    _testContext.CancellationToken));
            }

            Assert.IsEmpty(Directory.GetFiles(root, ".locked-result.txt.*.tmp"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_InvalidUtf8InputFile_ReturnsArgumentErrorWithoutStartingRuntime()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var inputPath = Path.Combine(root, "invalid.txt");
            var dataDirectory = Path.Combine(root, "data");
            await File.WriteAllBytesAsync(
                inputPath,
                [0xC3, 0x28],
                _testContext.CancellationToken);
            var provider = new CapturingProvider();
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--data-dir", dataDirectory,
                    "--input-file", inputPath
                ],
                stdout,
                new StringReader(string.Empty),
                Path.Combine(root, "config"),
                _ => _ => provider,
                error: stderr);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode);
            Assert.AreEqual(string.Empty, stdout.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(stderr.ToString()));
            Assert.IsEmpty(provider.Requests);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static RunInvocationResult RunOnce(
        string root,
        string configDirectory,
        string caseName,
        string[] additionalArguments)
    {
        var workspace = Path.Combine(root, caseName);
        Directory.CreateDirectory(workspace);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var provider = new CapturingProvider();
        var arguments = new List<string>
        {
            "run",
            "--workspace", workspace,
            "--input", "test input"
        };
        arguments.AddRange(additionalArguments);

        var exitCode = CliApplication.RunForTests(
            [.. arguments],
            stdout,
            new StringReader(string.Empty),
            configDirectory,
            _ => _ => provider,
            error: stderr);
        return new RunInvocationResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string[] GetNonEmptyLines(string value) =>
        value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private async Task WriteConfigurationAsync(string configDirectory)
    {
        var loader = new StandaloneConfigLoader(configDirectory);
        var config = new StandaloneConfig
        {
            DefaultProvider = "fake",
            DefaultModel = "fake-model",
            DefaultAgent = "default",
            Providers =
            [
                new ProviderEntry
                {
                    Name = "fake",
                    Protocol = "OpenAI",
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = "test-key",
                    Models = ["fake-model"]
                }
            ]
        };
        var agent = new AgentConfig
        {
            Id = "default",
            Name = "Default",
            SystemPrompt = "Test system prompt",
            Provider = "fake",
            Model = "fake-model"
        };

        await loader.SaveAsync(config, _testContext.CancellationToken);
        await loader.SaveAgentAsync(agent, _testContext.CancellationToken);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-run-output-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private const string ExpectedReply = "reply-第一段";

    private sealed record RunInvocationResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed class CapturingProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "reply-");
            yield return new TextDeltaProviderEvent(request.InvocationId, "第一段");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
