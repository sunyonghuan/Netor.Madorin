using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DiagnosticCommands
{
    public static Command CreateVersion(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("version", "Display program and protocol versions.");
        command.SetAction(parseResult =>
        {
            var info = CreateVersionInfo();
            if (parseResult.GetValue(jsonOption))
            {
                var json = JsonSerializer.Serialize(
                    info,
                    RuntimeJsonContext.Default.RuntimeVersionInfo);
                output.WriteLine(json);
            }
            else
            {
                output.WriteLine(
                    $"madorin {info.Version} (protocol {info.ProtocolVersion})");
                output.WriteLine($"Runtime: {info.Product}");
                output.WriteLine(
                    $"Platform: {info.Framework} / {info.Platform} {info.Architecture}");
            }

            return ExitCodes.Success;
        });

        return command;
    }

    public static Command CreateDoctor(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextWriter output)
    {
        var command = new Command("doctor", "Check configuration, Provider access, and runtime health.")
            .AddOptions(
                CommandOptions.Create<string?>("--provider", "Check only one Provider."),
                CommandOptions.Create<bool>("--fix", "Back up data and repair supported issues."),
                CommandOptions.Create<bool>("--yes", "Skip interactive confirmation."));
        command.SetAction(parseResult =>
        {
            var workspace = parseResult.GetValue(workspaceOption)
                ?? Path.Combine(Environment.CurrentDirectory, ".madorin");
            var root = Path.GetFullPath(workspace);
            var checks = new (string Name, string Detail)[]
            {
                ("runtime", Environment.Version.ToString()),
                ("workspace", $"{root} ({(Directory.Exists(root) ? "ok" : "missing")})"),
                ("workspace-write", CanWrite(root)),
                ("transport", OperatingSystem.IsWindows()
                    ? "named-pipe available"
                    : "unix-socket available"),
                ("database", "NotImplemented")
            };

            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "doctor");
                    writer.WriteStartArray("checks");
                    foreach (var check in checks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", check.Name);
                        writer.WriteString("detail", check.Detail);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach (var check in checks)
                {
                    output.WriteLine($"{check.Name}: {check.Detail}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static string CanWrite(string path)
    {
        var createdDirectory = false;
        try
        {
            createdDirectory = !Directory.Exists(path);
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, ".doctor-write-test");
            File.WriteAllText(file, "ok");
            File.Delete(file);
            return "ok";
        }
        catch (UnauthorizedAccessException)
        {
            return "denied";
        }
        catch (IOException)
        {
            return "error";
        }
        finally
        {
            if (createdDirectory && Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
    }

    public static Command CreateProviders(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("providers", "View and manage Provider configuration.");
        command.Subcommands.Add(
            CreateProviderPlaceholder("list", "List configured Providers.", jsonOption, output));
        command.Subcommands.Add(
            CreateProviderCommand("show", "Show Provider capabilities.", jsonOption, output));
        command.Subcommands.Add(
            CreateProviderTest(jsonOption, output));
        command.Subcommands.Add(CreateAddProvider(jsonOption, output));
        command.Subcommands.Add(
            CreateProviderCommand("remove", "Remove a Provider.", jsonOption, output)
                .AddOptions(CommandOptions.Create<bool>("--confirm", "Confirm removal.")));
        return command;
    }

    private static Command CreateProviderCommand(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output)
    {
        var command = CreateProviderPlaceholder(name, description, jsonOption, output);
        command.Arguments.Add(new Argument<string>("providerId"));
        return command;
    }

    private static Command CreateAddProvider(Option<bool> jsonOption, TextWriter output)
    {
        return CreateProviderCommand("add", "Add a Provider.", jsonOption, output)
            .AddOptions(
                CommandOptions.Create<string?>("--type", "Set the Provider type."),
                CommandOptions.Create<string?>("--endpoint", "Set the Provider endpoint."),
                CommandOptions.Create<string?>("--api-key-env", "Read the API key from an environment variable."),
                CommandOptions.Create<string?>("--api-key-file", "Read the API key from a protected file."),
                CommandOptions.Create<string?>("--config-file", "Read complete configuration from a file."));
    }

    private static Command CreateProviderTest(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("test", "Test Provider connectivity.")
            .AddOptions(CommandOptions.Create<string?>("--model", "Test a specific model."));
        var providerArgument = new Argument<string>("providerId");
        command.Arguments.Add(providerArgument);
        command.SetAction(async parseResult =>
        {
            var providerId = parseResult.GetValue(providerArgument)
                ?? throw new InvalidOperationException("Provider ID is required.");
            var loader = new StandaloneConfigLoader();
            var config = await loader.LoadAsync().ConfigureAwait(false);
            if (config is null)
            {
                WriteProviderProbeFailure(parseResult, jsonOption, output, providerId, "configuration_missing");
                return ExitCodes.ConnectionFailed;
            }

            try
            {
                var adapter = StandaloneProviderFactory.CreateAdapter(config, providerId);
                using var adapterLifetime = adapter as IDisposable;
                var result = await adapter.ProbeCapabilitiesAsync().ConfigureAwait(false);
                WriteProviderProbeResult(parseResult, jsonOption, output, providerId, result);
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                WriteProviderProbeFailure(
                    parseResult,
                    jsonOption,
                    output,
                    providerId,
                    exception.GetType().Name);
                return ExitCodes.ConnectionFailed;
            }
        });
        return command;
    }

    private static void WriteProviderProbeResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string providerId,
        ProviderCapabilityProbeResult result)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Provider: {providerId}");
            output.WriteLine($"Status: ok ({result.Source})");
            output.WriteLine($"Configuration version: {result.ConfigurationVersion}");
            output.WriteLine($"Probed at: {result.ProbedAt:O}");
            output.WriteLine($"Missing capabilities: {string.Join(", ", result.MissingCapabilities)}");
            return;
        }

        var value = JsonSerializer.SerializeToElement(
            result,
            RuntimeProviderJsonContext.Default.ProviderCapabilityProbeResult);
        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "providers test");
            writer.WriteString("providerId", providerId);
            writer.WriteString("status", "ok");
            writer.WritePropertyName("probe");
            value.WriteTo(writer);
            writer.WriteEndObject();
        });
    }

    private static void WriteProviderProbeFailure(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string providerId,
        string reason)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Provider: {providerId}");
            output.WriteLine($"Status: failed ({reason})");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "providers test");
            writer.WriteString("providerId", providerId);
            writer.WriteString("status", "failed");
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        });
    }

    private static Command CreateProviderPlaceholder(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output)
    {
        var command = new Command(name, description);
        command.SetAction(parseResult =>
        {
            CliOutput.WriteNotImplemented(
                parseResult,
                jsonOption,
                output,
                $"providers {name}",
                $"providers {name}: NotImplemented");
            return ExitCodes.Success;
        });
        return command;
    }

    private static RuntimeVersionInfo CreateVersionInfo()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        return new RuntimeVersionInfo(
            "Madorin.AI.Runtime",
            version,
            ProtocolVersions.Current,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
