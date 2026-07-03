using Netor.Cortana.Plugin.Native.Debugger.Hosting;

namespace Netor.Cortana.Plugin.Native.Debugger.Repl;

/// <summary>
/// REPL 交互循环
/// </summary>
public class ReplLoop(DebugPluginHost host)
{
    public async Task RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        ConsoleFormatter.PrintPluginInfo(host);
        ConsoleFormatter.PrintToolList(host.ToolRegistry);

        while (true)
        {
            Console.Write("Debug> ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var command = CommandParser.Parse(input);
            switch (command.Type)
            {
                case CommandType.Exit:
                    Console.WriteLine("\n👋 调试器已退出。");
                    return;
                case CommandType.Help:
                    ConsoleFormatter.PrintToolList(host.ToolRegistry);
                    break;
                case CommandType.ToolHelp:
                    ConsoleFormatter.PrintToolParameters(host.ToolRegistry, command.ToolName!);
                    break;
                case CommandType.ToolInvoke:
                    await HandleToolInvokeAsync(command);
                    break;
            }
        }
    }

    private async Task HandleToolInvokeAsync(ParsedCommand command)
    {
        var toolName = command.ToolName!;
        if (!host.ToolRegistry.Tools.TryGetValue(toolName, out var tool))
        {
            ConsoleFormatter.PrintError($"未找到工具: {toolName}。输入 'help' 查看可用工具。");
            return;
        }

        try
        {
            object[]? boundArgs = null;

            // No args + tool has parameters → interactive mode
            if (string.IsNullOrWhiteSpace(command.Args) && tool.Parameters.Length > 0)
            {
                boundArgs = InteractiveParameterCollector.Collect(tool);
                if (boundArgs == null) return;
            }

            Console.WriteLine("\n⏳ 执行中...");

            string result;
            if (boundArgs != null)
                result = await host.InvokeToolAsync(toolName, boundArgs);
            else
                result = await host.InvokeToolAsync(toolName, command.Args);

            ConsoleFormatter.PrintResult(result);
        }
        catch (KeyNotFoundException ex)
        {
            ConsoleFormatter.PrintError(ex.Message);
        }
        catch (Exception ex)
        {
            ConsoleFormatter.PrintException(ex);
        }
    }
}
