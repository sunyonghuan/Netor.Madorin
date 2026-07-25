using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class Stage7CommandMatrixTests
{
    private const string CommandMatrixSnapshot = "stage7-cli-command-matrix.v1.json";
    private const string JsonEnvelopeSnapshot = "stage7-cli-json-envelope.v1.json";
    private static readonly string[] ConfigSubcommands = ["init", "edit", "show", "validate"];

    [TestMethod]
    public void CommandTree_AllPathsAndHelpRemainStable()
    {
        using var snapshot = LoadSnapshot(CommandMatrixSnapshot);
        var snapshotRoot = snapshot.RootElement;
        var root = CliApplication.CreateRootCommand(
            new StringWriter(),
            new StringReader(string.Empty));
        var commands = EnumerateCommands(root, string.Empty)
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var commandEntries = snapshotRoot.GetProperty("commands").EnumerateArray().ToArray();
        var commandsByPath = commands.ToDictionary(static item => item.Path, StringComparer.Ordinal);

        Assert.HasCount(snapshotRoot.GetProperty("commandCount").GetInt32(), commands);
        CollectionAssert.AreEqual(
            commandEntries
                .Select(static entry => entry.GetProperty("path").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            commands.Select(static item => item.Path).ToArray());

        var globalOptions = snapshotRoot.GetProperty("globalOptions")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        foreach (var entry in commandEntries)
        {
            var path = entry.GetProperty("path").GetString()!;
            var item = commandsByPath[path];
            var help = GetHelp(path, out var exitCode);
            var normalizedHelp = NormalizeHelp(help);

            Assert.AreEqual(ExitCodes.Success, exitCode, $"{path}: {help}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(help));
            Assert.Contains("[options]", help, StringComparison.Ordinal);
            Assert.DoesNotContain("ai-runtime", help, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NotImplemented", help, StringComparison.Ordinal);
            Assert.AreEqual(
                entry.GetProperty("usage").GetString(),
                ExtractUsage(normalizedHelp, path),
                path);
            Assert.AreEqual(
                entry.GetProperty("helpSha256").GetString(),
                ComputeSha256(normalizedHelp),
                path);

            AssertOptionSet(path, help, entry, globalOptions);
            AssertArgumentArity(path, item.Command, entry);

            var example = entry.GetProperty("example")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            var parseResult = root.Parse(example);
            Assert.IsEmpty(
                parseResult.Errors,
                $"{path}: {string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message))}");
        }
    }

    [TestMethod]
    public void RequiredArguments_WhenMissing_ReturnInvalidArguments()
    {
        using var snapshot = LoadSnapshot(CommandMatrixSnapshot);
        foreach (var entry in snapshot.RootElement.GetProperty("commands").EnumerateArray())
        {
            if (!entry.GetProperty("arguments")
                    .EnumerateArray()
                    .Any(static argument => argument.GetProperty("min").GetInt32() > 0))
            {
                continue;
            }

            var path = entry.GetProperty("path").GetString()!;
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = CliApplication.Run(
                SplitPath(path),
                output,
                new StringReader(string.Empty),
                error);

            Assert.AreEqual(
                ExitCodes.InvalidArguments,
                exitCode,
                $"{path}: {output}{error}");
        }
    }

    [TestMethod]
    public void InvalidOptionCombinations_ReturnInvalidArguments()
    {
        using var snapshot = LoadSnapshot(CommandMatrixSnapshot);
        foreach (var invocation in snapshot.RootElement
                     .GetProperty("invalidInvocations")
                     .EnumerateArray())
        {
            var args = invocation.GetProperty("args")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = CliApplication.Run(
                args,
                output,
                new StringReader(string.Empty),
                error);
            var text = output.ToString() + error;

            Assert.AreEqual(
                ExitCodes.InvalidArguments,
                exitCode,
                $"{string.Join(' ', args)}: {text}");
            Assert.Contains(
                invocation.GetProperty("message").GetString()!,
                text,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void Defaults_AreVisibleInCommandHelp()
    {
        using var snapshot = LoadSnapshot(CommandMatrixSnapshot);
        foreach (var item in snapshot.RootElement.GetProperty("defaults").EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()!;
            var help = GetHelp(path, out var exitCode);

            Assert.AreEqual(ExitCodes.Success, exitCode, path);
            Assert.Contains(item.GetProperty("option").GetString()!, help, StringComparison.Ordinal);
            Assert.Contains(
                item.GetProperty("helpFragment").GetString()!,
                help,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void ExitCodes_RemainStable()
    {
        using var snapshot = LoadSnapshot(CommandMatrixSnapshot);
        var exitCodes = snapshot.RootElement.GetProperty("exitCodes");

        Assert.AreEqual(ExitCodes.Success, exitCodes.GetProperty("success").GetInt32());
        Assert.AreEqual(ExitCodes.GeneralError, exitCodes.GetProperty("generalError").GetInt32());
        Assert.AreEqual(
            ExitCodes.InvalidArguments,
            exitCodes.GetProperty("invalidArguments").GetInt32());
        Assert.AreEqual(
            ExitCodes.AuthenticationFailed,
            exitCodes.GetProperty("authenticationFailed").GetInt32());
        Assert.AreEqual(ExitCodes.WorkspaceError, exitCodes.GetProperty("workspaceError").GetInt32());
        Assert.AreEqual(
            ExitCodes.ConnectionFailed,
            exitCodes.GetProperty("connectionFailed").GetInt32());
        Assert.AreEqual(
            ExitCodes.UserInterrupted,
            exitCodes.GetProperty("userInterrupted").GetInt32());
    }

    [TestMethod]
    public void JsonOutput_SuccessAndFailureConformToEnvelopeSchema()
    {
        var schema = JsonSchema.FromText(ReadSnapshot(JsonEnvelopeSnapshot));

        var successOutput = new StringWriter();
        var successExitCode = CliApplication.Run(
            ["version", "--json"],
            successOutput,
            new StringReader(string.Empty));
        Assert.AreEqual(ExitCodes.Success, successExitCode, successOutput.ToString());
        AssertConformsToSchema(schema, successOutput.ToString(), "version --json");

        var failureOutput = new StringWriter();
        var failureError = new StringWriter();
        var failureExitCode = CliApplication.Run(
            ["doctor", "--yes", "--json"],
            failureOutput,
            new StringReader(string.Empty),
            failureError);
        Assert.AreEqual(
            ExitCodes.InvalidArguments,
            failureExitCode,
            failureOutput.ToString() + failureError);
        AssertConformsToSchema(
            schema,
            failureOutput.ToString() + failureError,
            "doctor --yes --json");
    }

    [TestMethod]
    public void RootCommand_UsesConfigAndOmitsLegacyProvidersGroup()
    {
        var root = CliApplication.CreateRootCommand(new StringWriter(), new StringReader(string.Empty));

        Assert.Contains("config", root.Subcommands.Select(static command => command.Name));
        Assert.DoesNotContain("providers", root.Subcommands.Select(static command => command.Name));
        var config = root.Subcommands.Single(static command => command.Name == "config");
        CollectionAssert.AreEquivalent(
            ConfigSubcommands,
            config.Subcommands.Select(static command => command.Name).ToArray());
    }

    [TestMethod]
    public void ConfigCommand_DoesNotExposePlaintextApiKeyOption()
    {
        var root = CliApplication.CreateRootCommand(new StringWriter(), new StringReader(string.Empty));
        var config = root.Subcommands.Single(static command => command.Name == "config");

        var optionNames = config.Subcommands
            .SelectMany(static command => command.Options)
            .Select(static option => option.Name)
            .ToArray();
        Assert.DoesNotContain("--api-key", optionNames);
    }

    [TestMethod]
    public void LegacyProvidersCommand_DoesNotReturnSuccessfulPlaceholder()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["providers", "list"],
            output,
            new StringReader(string.Empty),
            error);

        Assert.AreNotEqual(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("NotImplemented", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("NotImplemented", error.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void VersionCommand_HumanOutputIncludesProductAndProtocol()
    {
        var output = new StringWriter();

        var exitCode = CliApplication.Run(["version"], output, new StringReader(string.Empty));

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("madorin ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"protocol {ProtocolVersions.Current}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Runtime: Madorin.AI.Runtime", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ConfigShowAndValidate_HumanAndJsonOutputsRedactApiKey()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string apiKey = "stage7-secret-key";
            var loader = new StandaloneConfigLoader(root);
            await loader.SaveAsync(new StandaloneConfig
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
                        ApiKey = apiKey,
                        Models = ["fake-model"]
                    }
                ]
            });
            await loader.SaveAgentAsync(new AgentConfig
            {
                Id = "default",
                Name = "Default",
                SystemPrompt = "Test prompt",
                Provider = "fake",
                Model = "fake-model"
            });

            var humanOutput = new StringWriter();
            var humanExitCode = RunForTests(["config", "show"], humanOutput, root);
            Assert.AreEqual(ExitCodes.Success, humanExitCode, humanOutput.ToString());
            Assert.DoesNotContain(apiKey, humanOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains(StandaloneConfigLoader.MaskApiKey(apiKey), humanOutput.ToString(), StringComparison.Ordinal);

            var jsonOutput = new StringWriter();
            var jsonExitCode = RunForTests(["config", "show", "--json"], jsonOutput, root);
            Assert.AreEqual(ExitCodes.Success, jsonExitCode, jsonOutput.ToString());
            Assert.DoesNotContain(apiKey, jsonOutput.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(jsonOutput.ToString());
            var provider = document.RootElement.GetProperty("data").GetProperty("providers")[0];
            Assert.AreEqual(
                StandaloneConfigLoader.MaskApiKey(apiKey),
                provider.GetProperty("apiKey").GetString());

            var validateOutput = new StringWriter();
            var validateExitCode = RunForTests(
                ["config", "validate", "--json"],
                validateOutput,
                root);
            Assert.AreEqual(ExitCodes.Success, validateExitCode, validateOutput.ToString());
            using var validateDocument = JsonDocument.Parse(validateOutput.ToString());
            Assert.IsTrue(
                validateDocument.RootElement.GetProperty("data").GetProperty("valid").GetBoolean());
            Assert.DoesNotContain(apiKey, validateOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertOptionSet(
        string path,
        string help,
        JsonElement commandEntry,
        string[] globalOptions)
    {
        var expectedOptions = commandEntry.GetProperty("options")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .Concat(globalOptions)
            .Append("--help")
            .Concat(path.Length == 0 ? ["--version"] : [])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualOptions = ExtractLongOptions(help)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expectedOptions, actualOptions, path);
    }

    private static void AssertArgumentArity(
        string path,
        Command command,
        JsonElement commandEntry)
    {
        var expectedArguments = commandEntry.GetProperty("arguments").EnumerateArray().ToArray();
        Assert.HasCount(expectedArguments.Length, command.Arguments, path);
        for (var index = 0; index < expectedArguments.Length; index++)
        {
            var expected = expectedArguments[index];
            var actual = command.Arguments[index];
            Assert.AreEqual(expected.GetProperty("name").GetString(), actual.Name, path);
            Assert.AreEqual(
                expected.GetProperty("min").GetInt32(),
                actual.Arity.MinimumNumberOfValues,
                path);
            Assert.AreEqual(
                expected.GetProperty("max").GetInt32(),
                actual.Arity.MaximumNumberOfValues,
                path);
        }
    }

    private static void AssertConformsToSchema(JsonSchema schema, string json, string caseName)
    {
        using var instance = JsonDocument.Parse(json);
        var result = schema.Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.IsTrue(result.IsValid, $"{caseName}: {result}");
    }

    private static string GetHelp(string path, out int exitCode)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            exitCode = CliApplication.Run(
                [.. SplitPath(path), "--help"],
                output,
                new StringReader(string.Empty),
                error);
            return output.ToString() + error;
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static string ExtractUsage(string help, string path)
    {
        using var reader = new StringReader(help);
        var inUsageSection = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("Usage:", StringComparison.Ordinal))
            {
                inUsageSection = true;
                continue;
            }

            if (inUsageSection && !string.IsNullOrWhiteSpace(line))
            {
                return NormalizeUsageLine(line);
            }
        }

        Assert.Fail($"{path}: Help output did not contain a usage section.{Environment.NewLine}{help}");
        return string.Empty;
    }

    private static IEnumerable<string> ExtractLongOptions(string help)
    {
        using var reader = new StringReader(help);
        while (reader.ReadLine() is { } line)
        {
            var optionStart = line.IndexOf("--", StringComparison.Ordinal);
            if (optionStart < 0)
            {
                continue;
            }

            var optionEnd = optionStart + 2;
            while (optionEnd < line.Length
                   && (char.IsAsciiLetterOrDigit(line[optionEnd]) || line[optionEnd] == '-'))
            {
                optionEnd++;
            }

            yield return line[optionStart..optionEnd];
        }
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string NormalizeHelp(string help) =>
        NormalizeUsageExecutable(
            help.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());

    private static string NormalizeUsageExecutable(string help)
    {
        var lines = help.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Trim().Equals("Usage:", StringComparison.Ordinal))
            {
                continue;
            }

            for (var usageIndex = index + 1; usageIndex < lines.Length; usageIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[usageIndex]))
                {
                    continue;
                }

                lines[usageIndex] = $"  {NormalizeUsageLine(lines[usageIndex])}";
                return string.Join('\n', lines);
            }

            break;
        }

        return string.Join('\n', lines);
    }

    private static string NormalizeUsageLine(string usageLine)
    {
        var usage = usageLine.Trim();
        var commandSeparator = usage.IndexOf(' ');
        return commandSeparator < 0
            ? "madorin"
            : $"madorin{usage[commandSeparator..]}";
    }

    private static string[] SplitPath(string path) =>
        path.Length == 0
            ? []
            : path.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static JsonDocument LoadSnapshot(string name) =>
        JsonDocument.Parse(ReadSnapshot(name));

    private static string ReadSnapshot(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", name));

    private static int RunForTests(string[] args, TextWriter output, string configDirectory) =>
        CliApplication.RunForTests(
            args,
            output,
            new StringReader(string.Empty),
            configDirectory,
            static _ => static _ => throw new InvalidOperationException("Provider resolution was not expected."));

    private static IEnumerable<(Command Command, string Path)> EnumerateCommands(
        Command command,
        string path)
    {
        yield return (command, path);
        foreach (var child in command.Subcommands)
        {
            var childPath = path.Length == 0 ? child.Name : $"{path} {child.Name}";
            foreach (var item in EnumerateCommands(child, childPath))
            {
                yield return item;
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage7-command-matrix",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
