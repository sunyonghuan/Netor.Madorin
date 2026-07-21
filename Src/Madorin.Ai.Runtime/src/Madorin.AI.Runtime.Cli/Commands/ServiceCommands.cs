using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ServiceCommands
{
    public static Command CreateServe()
    {
        return new Command("serve", "Start the persistent Runtime server.")
            .AddOptions(
                CommandOptions.Create<string?>("--config-dir", "Override the configuration directory."),
                CommandOptions.Create<string?>("--log-dir", "Override the log directory."),
                CommandOptions.Create<string?>("--instance", "Set the Runtime instance identifier."),
                CommandOptions.Create<string?>("--pipe-prefix", "Override the IPC pipe prefix."),
                CommandOptions.Create<int?>("--max-runs", "Set the maximum concurrent Run count."));
    }

    public static Command CreateRun()
    {
        return new Command("run", "Run one task or enter interactive mode.")
            .AddOptions(
                CommandOptions.Create<string?>("--provider", "Select a Provider."),
                CommandOptions.Create<string?>("--model", "Select a model."),
                CommandOptions.Create<string?>("--agent", "Select an Agent."),
                CommandOptions.Create<string?>("--mode", "Select expert, meeting, or work mode."),
                CommandOptions.Create<string?>("--session", "Continue an existing Session."),
                CommandOptions.Create<string?>("--input", "Run once with the supplied input."),
                CommandOptions.Create<string?>("--input-file", "Read input from a file."),
                CommandOptions.Create<string?>("--output-file", "Write output to a file."),
                CommandOptions.Create<string?>("--output-format", "Select text, json, or jsonl output."),
                CommandOptions.Create<bool>("--no-stream", "Wait for the complete result."),
                CommandOptions.Create<int?>("--timeout", "Set the timeout in seconds."));
    }
}
