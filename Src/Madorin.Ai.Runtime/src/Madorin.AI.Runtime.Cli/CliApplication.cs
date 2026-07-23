using System.CommandLine;
using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli;

public static class CliApplication
{
    public static int Run(
        string[] args,
        TextWriter? output = null,
        TextReader? input = null)
    {
        var normalizedArgs = NormalizeArgs(args);
        return CreateRootCommand(output, input).Parse(normalizedArgs).Invoke();
    }

    public static RootCommand CreateRootCommand(
        TextWriter? output = null,
        TextReader? input = null) =>
        CreateRootCommandCore(output, input, null, null);

    internal static int RunForTests(
        string[] args,
        TextWriter output,
        TextReader input,
        string configDirectory,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>
            providerResolverFactory,
        string? memoryUserHome = null)
    {
        var normalizedArgs = NormalizeArgs(args);
        return CreateRootCommandCore(
                output,
                input,
                configDirectory,
                providerResolverFactory,
                memoryUserHome)
            .Parse(normalizedArgs)
            .Invoke();
    }

    private static RootCommand CreateRootCommandCore(
        TextWriter? output,
        TextReader? input,
        string? configDirectory,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory,
        string? memoryUserHome = null)
    {
        var rootCommand = new RootCommand(
            "Independent AI Runtime server, client, diagnostics, and maintenance CLI.");

        var jsonOption = CommandOptions.Create<bool>(
            "--json",
            "Write machine-readable JSON output.",
            recursive: true);
        var workspaceOption = CommandOptions.Create<string?>(
            "--workspace",
            "Override the workspace path.",
            recursive: true);
        var dataDirectoryOption = CommandOptions.Create<string?>(
            "--data-dir",
            "Override the .madorin data directory.",
            recursive: true);

        rootCommand.Options.Add(workspaceOption);
        rootCommand.Options.Add(dataDirectoryOption);
        rootCommand.Options.Add(CommandOptions.Create<string?>(
            "--log-level",
            "Set Debug, Information, Warning, or Error logging.",
            recursive: true));
        rootCommand.Options.Add(CommandOptions.Create<bool>(
            "--no-color",
            "Disable colored output.",
            recursive: true));
        rootCommand.Options.Add(jsonOption);

        var commandOutput = output ?? Console.Out;
        var commandInput = input ?? Console.In;
        var terminal = new CliTerminal(commandInput, commandOutput);
        rootCommand.Subcommands.Add(ServiceCommands.CreateServe(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandInput,
            commandOutput));
        rootCommand.Subcommands.Add(ServiceCommands.CreateRun(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            terminal,
            commandOutput,
            configDirectory,
            providerResolverFactory,
            memoryUserHome));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateVersion(jsonOption, output ?? Console.Out));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateDoctor(
            jsonOption,
            workspaceOption,
            commandOutput));
        rootCommand.Subcommands.Add(ConfigCommands.Create(
            jsonOption,
            terminal,
            commandOutput,
            configDirectory));
        rootCommand.Subcommands.Add(AgentCommands.Create(
            jsonOption,
            terminal,
            commandOutput,
            configDirectory));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateProviders(jsonOption, commandOutput));
        rootCommand.Subcommands.Add(MemoryCommands.Create(
            jsonOption,
            workspaceOption,
            commandInput,
            commandOutput,
            memoryUserHome));
        rootCommand.Subcommands.Add(SessionCommands.Create(jsonOption, commandOutput));
        rootCommand.Subcommands.Add(DatabaseCommands.Create(jsonOption, commandOutput));
        rootCommand.Subcommands.Add(StorageCommands.Create(jsonOption, commandOutput));
        rootCommand.Subcommands.Add(ControlCommands.Create(jsonOption, commandOutput));

        return rootCommand;
    }

    private static string[] NormalizeArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return ["run"];
        }

        if (args is ["--version"])
        {
            return ["version"];
        }

        if (args is [var modeOption, var modeValue]
            && modeOption.Equals("--mode", StringComparison.OrdinalIgnoreCase)
            && modeValue.Equals("standalone", StringComparison.OrdinalIgnoreCase))
        {
            return ["run"];
        }

        if (args is [var inlineMode]
            && inlineMode.Equals("--mode=standalone", StringComparison.OrdinalIgnoreCase))
        {
            return ["run"];
        }

        return args;
    }
}
