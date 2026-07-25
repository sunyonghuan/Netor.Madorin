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
        TextReader? input = null) =>
        Run(args, output, input, error: null);

    public static int Run(
        string[] args,
        TextWriter? output,
        TextReader? input,
        TextWriter? error)
    {
        var normalizedArgs = NormalizeArgs(args);
        var commandOutput = output ?? Console.Out;
        var commandError = error ?? (output is null ? Console.Error : commandOutput);
        return Invoke(
            CreateRootCommand(commandOutput, input, commandError),
            normalizedArgs,
            commandOutput,
            commandError);
    }

    public static RootCommand CreateRootCommand(
        TextWriter? output = null,
        TextReader? input = null) =>
        CreateRootCommand(output, input, error: null);

    public static RootCommand CreateRootCommand(
        TextWriter? output,
        TextReader? input,
        TextWriter? error) =>
        CreateRootCommandCore(output, input, error, null, null);

    internal static int RunForTests(
        string[] args,
        TextWriter output,
        TextReader input,
        string configDirectory,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>
            providerResolverFactory,
        string? memoryUserHome = null,
        TextWriter? error = null,
        IReplInterruptSource? replInterruptSource = null,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter>? providerAdapterFactory = null)
    {
        var normalizedArgs = NormalizeArgs(args);
        return Invoke(
            CreateRootCommandCore(
                output,
                input,
                error,
                configDirectory,
                providerResolverFactory,
                memoryUserHome,
                replInterruptSource,
                providerAdapterFactory),
            normalizedArgs,
            output,
            error ?? output);
    }

    private static RootCommand CreateRootCommandCore(
        TextWriter? output,
        TextReader? input,
        TextWriter? error,
        string? configDirectory,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory,
        string? memoryUserHome = null,
        IReplInterruptSource? replInterruptSource = null,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter>? providerAdapterFactory = null)
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
        var commandError = error ?? (output is null ? Console.Error : commandOutput);
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
            commandError,
            configDirectory,
            providerResolverFactory,
            memoryUserHome,
            replInterruptSource ?? ConsoleReplInterruptSource.Instance));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateVersion(jsonOption, output ?? Console.Out));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateDoctor(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandInput,
            commandOutput,
            configDirectory,
            memoryUserHome,
            providerAdapterFactory));
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
        rootCommand.Subcommands.Add(MemoryCommands.Create(
            jsonOption,
            workspaceOption,
            commandInput,
            commandOutput,
            memoryUserHome));
        rootCommand.Subcommands.Add(SessionCommands.Create(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandOutput,
            configDirectory,
            memoryUserHome,
            providerResolverFactory));
        rootCommand.Subcommands.Add(DatabaseCommands.Create(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandInput,
            commandOutput));
        rootCommand.Subcommands.Add(StorageCommands.Create(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandOutput));
        rootCommand.Subcommands.Add(ControlCommands.Create(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            commandOutput,
            commandError));

        return rootCommand;
    }

    private static int Invoke(
        RootCommand rootCommand,
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        var parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count == 0)
        {
            return parseResult.Invoke(new InvocationConfiguration
            {
                Output = output,
                Error = error
            });
        }

        var message = string.Join(
            Environment.NewLine,
            parseResult.Errors.Select(static parseError => parseError.Message));
        if (RequestsMachineOutput(args))
        {
            CliOutput.WriteFailure(
                output,
                "InvalidArguments",
                message);
        }
        else
        {
            error.WriteLine($"Error: {message}");
        }

        return ExitCodes.InvalidArguments;
    }

    private static bool RequestsMachineOutput(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--json=true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.StartsWith("--output-format=", StringComparison.OrdinalIgnoreCase))
            {
                return IsMachineFormat(argument["--output-format=".Length..]);
            }

            if (argument.Equals("--output-format", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                return IsMachineFormat(args[index + 1]);
            }
        }

        return false;
    }

    private static bool IsMachineFormat(string value) =>
        value.Equals("json", StringComparison.OrdinalIgnoreCase)
        || value.Equals("jsonl", StringComparison.OrdinalIgnoreCase);

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
