using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Stage7UnifiedOutputTests
{
    [TestMethod]
    public void VersionJson_UsesUnifiedSuccessEnvelope()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliApplication.Run(
            ["version", "--json", "--no-color"],
            stdout,
            new StringReader(string.Empty),
            stderr);

        Assert.AreEqual(ExitCodes.Success, exitCode, stderr.ToString());
        Assert.AreEqual(string.Empty, stderr.ToString());
        AssertNoAnsi(stdout.ToString());
        using var document = JsonDocument.Parse(stdout.ToString());
        var data = AssertSuccessEnvelope(document.RootElement);
        Assert.AreEqual(
            ProtocolVersions.Current,
            data.GetProperty("protocolVersion").GetString());
    }

    [TestMethod]
    public void ConfigShowJson_PreservesCommandDataInsideUnifiedEnvelope()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = RunForTests(
                ["config", "show", "--json"],
                root);

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.Stderr);
            Assert.AreEqual(string.Empty, result.Stderr);
            using var document = JsonDocument.Parse(result.Stdout);
            var data = AssertSuccessEnvelope(document.RootElement);
            Assert.AreEqual("config show", data.GetProperty("command").GetString());
            Assert.IsFalse(data.GetProperty("configured").GetBoolean());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ParseErrorJson_WritesSingleFailureEnvelopeToStdout()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliApplication.Run(
            ["version", "--unknown-option", "--json"],
            stdout,
            new StringReader(string.Empty),
            stderr);

        Assert.AreEqual(ExitCodes.InvalidArguments, exitCode);
        Assert.AreEqual(string.Empty, stderr.ToString());
        AssertNoAnsi(stdout.ToString());
        using var document = JsonDocument.Parse(stdout.ToString());
        AssertFailureEnvelope(document.RootElement, "InvalidArguments");
    }

    [TestMethod]
    public void ParseErrorHuman_WritesOnlyToStderr()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliApplication.Run(
            ["version", "--unknown-option", "--no-color"],
            stdout,
            new StringReader(string.Empty),
            stderr);

        Assert.AreEqual(ExitCodes.InvalidArguments, exitCode);
        Assert.AreEqual(string.Empty, stdout.ToString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(stderr.ToString()));
        AssertNoAnsi(stderr.ToString());
    }

    [TestMethod]
    public void StorageFailureJson_UsesUnifiedFailureEnvelope()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = RunForTests(
                [
                    "storage", "check",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--data-dir", Path.Combine(root, "missing-data"),
                    "--json"
                ],
                root);

            Assert.AreEqual(ExitCodes.GeneralError, result.ExitCode);
            Assert.AreEqual(string.Empty, result.Stderr);
            using var document = JsonDocument.Parse(result.Stdout);
            AssertFailureEnvelope(document.RootElement, "CommandFailed");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static JsonElement AssertSuccessEnvelope(JsonElement root)
    {
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.AreEqual(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("diagnosticId").ValueKind);
        Assert.IsFalse(root.TryGetProperty("error", out _));
        return root.GetProperty("data");
    }

    private static void AssertFailureEnvelope(JsonElement root, string expectedCode)
    {
        Assert.IsFalse(root.GetProperty("success").GetBoolean());
        var error = root.GetProperty("error");
        Assert.AreEqual(expectedCode, error.GetProperty("code").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.AreEqual(JsonValueKind.False, error.GetProperty("isRetryable").ValueKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("diagnosticId").GetString()));
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.AreEqual(
            error.GetProperty("diagnosticId").GetString(),
            root.GetProperty("diagnosticId").GetString());
    }

    private static void AssertNoAnsi(string value) =>
        Assert.IsFalse(value.Contains('\u001b', StringComparison.Ordinal));

    private static CliResult RunForTests(string[] args, string root)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApplication.RunForTests(
            args,
            stdout,
            new StringReader(string.Empty),
            Path.Combine(root, "config"),
            _ => _ => throw new InvalidOperationException("Provider must not be resolved."),
            memoryUserHome: Path.Combine(root, "home"),
            error: stderr);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"madorin-output-stage7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
