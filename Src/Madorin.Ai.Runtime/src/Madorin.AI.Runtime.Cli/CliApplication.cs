using System.CommandLine;
using Madorin.AI.Runtime.Cli.Commands;

namespace Madorin.AI.Runtime.Cli;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter? output = null)
    {
        var normalizedArgs = args is ["--version"] ? new[] { "version" } : args;
        return CreateRootCommand(output).Parse(normalizedArgs).Invoke();
    }

    public static RootCommand CreateRootCommand(TextWriter? output = null)
    {
        var rootCommand = new RootCommand(
            "Independent AI Runtime server, client, diagnostics, and maintenance CLI.");

        var jsonOption = CommandOptions.Create<bool>(
            "--json",
            "Write machine-readable JSON output.",
            recursive: true);

        rootCommand.Options.Add(CommandOptions.Create<string?>(
            "--workspace",
            "Override the workspace path.",
            recursive: true));
        rootCommand.Options.Add(CommandOptions.Create<string?>(
            "--data-dir",
            "Override the .madorin data directory.",
            recursive: true));
        rootCommand.Options.Add(CommandOptions.Create<string?>(
            "--log-level",
            "Set Debug, Information, Warning, or Error logging.",
            recursive: true));
        rootCommand.Options.Add(CommandOptions.Create<bool>(
            "--no-color",
            "Disable colored output.",
            recursive: true));
        rootCommand.Options.Add(jsonOption);

        rootCommand.Subcommands.Add(ServiceCommands.CreateServe());
        rootCommand.Subcommands.Add(ServiceCommands.CreateRun());
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateVersion(jsonOption, output ?? Console.Out));
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateDoctor());
        rootCommand.Subcommands.Add(DiagnosticCommands.CreateProviders());
        rootCommand.Subcommands.Add(SessionCommands.Create());
        rootCommand.Subcommands.Add(DatabaseCommands.Create());
        rootCommand.Subcommands.Add(StorageCommands.Create());
        rootCommand.Subcommands.Add(ControlCommands.Create());

        return rootCommand;
    }
}
