namespace Netor.Cortana.Plugin.Native.Debugger.Repl;

public enum CommandType
{
    Exit,
    Help,
    ToolHelp,
    ToolInvoke
}

public record ParsedCommand(CommandType Type, string? ToolName = null, string? Args = null);

public static class CommandParser
{
    public static ParsedCommand Parse(string input)
    {
        if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase))
            return new ParsedCommand(CommandType.Exit);

        if (string.Equals(input, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "h", StringComparison.OrdinalIgnoreCase))
            return new ParsedCommand(CommandType.Help);

        var spaceIdx = input.IndexOf(' ');
        var toolName = spaceIdx > 0 ? input[..spaceIdx] : input;
        var args = spaceIdx > 0 ? input[(spaceIdx + 1)..].Trim() : null;

        if (string.Equals(args, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args, "h", StringComparison.OrdinalIgnoreCase))
            return new ParsedCommand(CommandType.ToolHelp, toolName);

        return new ParsedCommand(CommandType.ToolInvoke, toolName, args);
    }
}
