using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneSessionExportStage7Tests
{
    private const string ProviderId = "session-export-provider";
    private const string ModelId = "session-export-model";
    private const string AgentId = "session-export-agent";
    private const string VisibleText = "visible export text";
    private const string ReasoningSecret = "private reasoning detail";
    private const string ToolSecret = "private tool detail";

    private readonly TestContext _testContext;

    public StandaloneSessionExportStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionExport_Markdown_DefaultFiltersSensitiveDetailsAndFlagsIncludeThem()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture);
            await AppendMixedMessageAsync(fixture, sessionId);

            var filtered = RunCommand(fixture, "session", "export", sessionId);

            Assert.AreEqual(ExitCodes.Success, filtered.ExitCode, filtered.AllOutput);
            Assert.Contains("# Session", filtered.Output, StringComparison.Ordinal);
            Assert.Contains(VisibleText, filtered.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(ReasoningSecret, filtered.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(ToolSecret, filtered.Output, StringComparison.Ordinal);

            var complete = RunCommand(
                fixture,
                "session", "export", sessionId,
                "--include-reasoning",
                "--include-tool-calls");

            Assert.AreEqual(ExitCodes.Success, complete.ExitCode, complete.AllOutput);
            Assert.Contains(ReasoningSecret, complete.Output, StringComparison.Ordinal);
            Assert.Contains(ToolSecret, complete.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionExport_TxtAndJsonl_ReturnStableReadableFormats()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture);
            await AppendMixedMessageAsync(fixture, sessionId);

            var text = RunCommand(
                fixture,
                "session", "export", sessionId,
                "--format", "txt");
            Assert.AreEqual(ExitCodes.Success, text.ExitCode, text.AllOutput);
            Assert.Contains("USER:", text.Output, StringComparison.Ordinal);
            Assert.Contains(VisibleText, text.Output, StringComparison.Ordinal);

            var jsonl = RunCommand(
                fixture,
                "session", "export", sessionId,
                "--format", "jsonl");
            Assert.AreEqual(ExitCodes.Success, jsonl.ExitCode, jsonl.AllOutput);
            var lines = jsonl.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.IsGreaterThanOrEqualTo(4, lines.Length);
            var emptyContentMessages = 0;
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.IsTrue(document.RootElement.TryGetProperty("sequence", out _));
                Assert.IsTrue(document.RootElement.TryGetProperty("content", out var content));
                if (content.GetArrayLength() == 0)
                {
                    emptyContentMessages++;
                }
            }

            Assert.IsGreaterThanOrEqualTo(1, emptyContentMessages);
            Assert.DoesNotContain(ReasoningSecret, jsonl.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(ToolSecret, jsonl.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionExport_OutputFile_IsAtomicAndFailurePreservesExistingContent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture);
            await AppendMixedMessageAsync(fixture, sessionId);
            var outputPath = Path.Combine(root, "session.md");
            await File.WriteAllTextAsync(
                outputPath,
                "old content",
                _testContext.CancellationToken);

            var exported = RunCommand(
                fixture,
                "session", "export", sessionId,
                "--output", outputPath);

            Assert.AreEqual(ExitCodes.Success, exported.ExitCode, exported.AllOutput);
            Assert.IsEmpty(exported.Output);
            var content = await File.ReadAllTextAsync(outputPath, _testContext.CancellationToken);
            Assert.Contains(VisibleText, content, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                outputPath,
                "preserve me",
                _testContext.CancellationToken);
            var failed = RunCommand(
                fixture,
                "session", "export", "missing-session",
                "--output", outputPath,
                "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, failed.ExitCode, failed.AllOutput);
            Assert.AreEqual(
                "preserve me",
                await File.ReadAllTextAsync(outputPath, _testContext.CancellationToken));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("yaml", "InvalidSessionExportFormat")]
    [DataRow("", "InvalidSessionExportFormat")]
    public async Task SessionExport_InvalidFormat_ReturnsInvalidArguments(
        string format,
        string expectedCode)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture);

            var result = RunCommand(
                fixture,
                "session", "export", sessionId,
                "--format", format,
                "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.AllOutput);
            using var document = JsonDocument.Parse(result.Output);
            Assert.AreEqual(
                expectedCode,
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private async Task<SessionExportFixture> CreateFixtureAsync(string root)
    {
        var configDirectory = Path.Combine(root, "config");
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);

        var loader = new StandaloneConfigLoader(configDirectory);
        await loader.SaveAsync(new StandaloneConfig
        {
            DefaultProvider = ProviderId,
            DefaultModel = ModelId,
            DefaultAgent = AgentId,
            Providers =
            [
                new ProviderEntry
                {
                    Name = ProviderId,
                    Protocol = "OpenAI",
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = "session-export-test-key",
                    Models = [ModelId]
                }
            ]
        }, _testContext.CancellationToken);
        await loader.SaveAgentAsync(new AgentConfig
        {
            Id = AgentId,
            Name = "Session Export Agent",
            SystemPrompt = "Answer deterministically.",
            Provider = ProviderId,
            Model = ModelId
        }, _testContext.CancellationToken);

        return new SessionExportFixture(root, configDirectory, workspace, dataDirectory);
    }

    private async Task AppendMixedMessageAsync(
        SessionExportFixture fixture,
        string sessionId)
    {
        var store = new ConversationStore(fixture.DataDirectory);
        var content = ParseElement($$"""
            [
              { "type": "text", "text": "{{VisibleText}}" },
              { "type": "reasoning", "content": "{{ReasoningSecret}}" },
              {
                "type": "tool_call",
                "callId": "export-call",
                "toolId": "test.export",
                "name": "export-test",
                "arguments": { "detail": "{{ToolSecret}}" }
              },
              {
                "type": "tool_result",
                "callId": "export-call",
                "toolId": "test.export",
                "success": true,
                "content": [{ "type": "text", "text": "{{ToolSecret}} result" }]
              }
            ]
            """);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "export-invocation",
                AgentId,
                "assistant",
                content,
                DateTimeOffset.UtcNow),
            _testContext.CancellationToken);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "export-private-invocation",
                AgentId,
                "assistant",
                ParseElement($$"""
                    [{ "type": "reasoning", "content": "{{ReasoningSecret}}" }]
                    """),
                DateTimeOffset.UtcNow),
            _testContext.CancellationToken);
    }

    private static string RunSession(SessionExportFixture fixture)
    {
        var result = RunCommand(
            fixture,
            "run",
            "--input", "export this conversation",
            "--no-stream",
            "--json");
        Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement
            .GetProperty("data")
            .GetProperty("sessionId")
            .GetString()
            ?? throw new AssertFailedException("The run result did not contain a Session id.");
    }

    private static CliResult RunCommand(
        SessionExportFixture fixture,
        params string[] commandArguments)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        string[] args =
        [
            .. commandArguments,
            "--workspace", fixture.Workspace,
            "--data-dir", fixture.DataDirectory
        ];
        var exitCode = CliApplication.RunForTests(
            args,
            output,
            new StringReader(string.Empty),
            fixture.ConfigDirectory,
            _ => _ => new DeterministicProvider(),
            memoryUserHome: fixture.Root,
            error: error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"madorin-session-export-stage7-{Guid.NewGuid():N}");
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

    private sealed record SessionExportFixture(
        string Root,
        string ConfigDirectory,
        string Workspace,
        string DataDirectory);

    private sealed record CliResult(int ExitCode, string Output, string Error)
    {
        public string AllOutput => Output + Error;
    }

    private sealed class DeterministicProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => StandaloneSessionExportStage7Tests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(ModelId, ModelId, ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "base export reply");
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
