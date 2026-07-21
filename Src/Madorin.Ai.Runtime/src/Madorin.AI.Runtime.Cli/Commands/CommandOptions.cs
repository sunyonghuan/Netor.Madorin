using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class CommandOptions
{
    public static Option<T> Create<T>(
        string name,
        string description,
        bool recursive = false)
    {
        return new Option<T>(name)
        {
            Description = description,
            Recursive = recursive
        };
    }

    public static Command AddOptions(this Command command, params Option[] options)
    {
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        return command;
    }
}
