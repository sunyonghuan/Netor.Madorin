using System.ComponentModel;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class MemoryCommands
{
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextReader input,
        TextWriter output,
        string? userHome = null)
    {
        var command = new Command("memory", "Manage global and project memory files.");
        command.Subcommands.Add(CreateInit(jsonOption, workspaceOption, output, userHome));
        command.Subcommands.Add(CreateShow(jsonOption, workspaceOption, output, userHome));
        command.Subcommands.Add(CreateAdd(jsonOption, workspaceOption, input, output, userHome));
        command.Subcommands.Add(CreateEdit(jsonOption, workspaceOption, input, output, userHome));
        command.Subcommands.Add(CreateClear(jsonOption, workspaceOption, input, output, userHome));
        return command;
    }

    private static Command CreateInit(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextWriter output,
        string? userHome)
    {
        var scopeArgument = CreateScopeArgument("global or project");
        var command = new Command("init", "Create a sample memory file.");
        command.Arguments.Add(scopeArgument);
        command.SetAction(async parseResult =>
        {
            if (!TryGetScope(parseResult.GetValue(scopeArgument), allowEffective: false, out var scope))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory init",
                    "error",
                    "Scope must be global or project.");
                return ExitCodes.InvalidArguments;
            }

            var service = CreateService(parseResult, workspaceOption, userHome);
            var path = GetPath(service, scope);
            if (File.Exists(path))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory init",
                    "exists",
                    $"Memory file already exists: {path}",
                    path);
                return ExitCodes.Success;
            }

            await service.InitAsync(scope);
            WriteMessage(
                parseResult,
                jsonOption,
                output,
                "memory init",
                "ok",
                $"Initialized memory: {path}",
                path);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateShow(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextWriter output,
        string? userHome)
    {
        var scopeArgument = CreateScopeArgument("global, project, or effective");
        var command = new Command("show", "Display memory content and paths.");
        command.Arguments.Add(scopeArgument);
        command.SetAction(async parseResult =>
        {
            if (!TryGetScope(parseResult.GetValue(scopeArgument), allowEffective: true, out var scope))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory show",
                    "error",
                    "Scope must be global, project, or effective.");
                return ExitCodes.InvalidArguments;
            }

            var service = CreateService(parseResult, workspaceOption, userHome);
            var content = await service.ReadAsync(scope);
            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "memory show");
                    writer.WriteString("scope", scope.ToString().ToLowerInvariant());
                    if (scope == MemoryScope.Effective)
                    {
                        writer.WriteString("globalPath", service.GlobalPath);
                        writer.WriteString("projectPath", service.ProjectPath);
                    }
                    else
                    {
                        writer.WriteString("path", GetPath(service, scope));
                    }

                    if (content is null)
                    {
                        writer.WriteNull("content");
                    }
                    else
                    {
                        writer.WriteString("content", content);
                    }

                    writer.WriteEndObject();
                });
            }
            else
            {
                if (scope == MemoryScope.Effective)
                {
                    output.WriteLine($"Global path: {service.GlobalPath}");
                    output.WriteLine($"Project path: {service.ProjectPath}");
                }
                else
                {
                    output.WriteLine($"Path: {GetPath(service, scope)}");
                }

                WriteContent(output, content);
            }
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateAdd(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextReader input,
        TextWriter output,
        string? userHome)
    {
        var scopeArgument = CreateScopeArgument("global or project");
        var command = new Command("add", "Append one memory item.");
        command.Arguments.Add(scopeArgument);
        command.SetAction(async parseResult =>
        {
            if (!TryGetScope(parseResult.GetValue(scopeArgument), allowEffective: false, out var scope))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory add",
                    "error",
                    "Scope must be global or project.");
                return ExitCodes.InvalidArguments;
            }

            if (!CliOutput.IsJson(parseResult, jsonOption))
            {
                output.Write("Memory item: ");
                output.Flush();
            }

            var item = await input.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(item))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory add",
                    "error",
                    "Memory item cannot be empty.");
                return ExitCodes.InvalidArguments;
            }

            var service = CreateService(parseResult, workspaceOption, userHome);
            await service.AppendAsync(scope, item);
            var path = GetPath(service, scope);
            WriteMessage(
                parseResult,
                jsonOption,
                output,
                "memory add",
                "ok",
                $"Updated memory: {path}",
                path);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateEdit(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextReader input,
        TextWriter output,
        string? userHome)
    {
        var scopeArgument = CreateScopeArgument("global or project");
        var command = new Command("edit", "Edit a memory file.");
        command.Arguments.Add(scopeArgument);
        command.SetAction(async parseResult =>
        {
            if (!TryGetScope(parseResult.GetValue(scopeArgument), allowEffective: false, out var scope))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory edit",
                    "error",
                    "Scope must be global or project.");
                return ExitCodes.InvalidArguments;
            }

            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory edit",
                    "error",
                    "Interactive memory editing is unavailable with --json.");
                return ExitCodes.InvalidArguments;
            }

            var service = CreateService(parseResult, workspaceOption, userHome);
            var path = GetPath(service, scope);
            try
            {
                var current = await service.ReadAsync(scope).ConfigureAwait(false) ?? "# Memory\n";
                var edited = await TryEditWithConfiguredEditorAsync(current).ConfigureAwait(false);
                if (edited is null)
                {
                    edited = await ReadMultilineEditAsync(input, output).ConfigureAwait(false);
                }

                if (edited is null)
                {
                    output.WriteLine("Memory edit was cancelled.");
                    return ExitCodes.UserInterrupted;
                }

                await service.ReplaceAsync(scope, edited).ConfigureAwait(false);
                output.WriteLine($"Updated memory: {path}");
                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
            {
                output.WriteLine($"Memory edit failed: {ex.Message}");
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateClear(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        TextReader input,
        TextWriter output,
        string? userHome)
    {
        var scopeArgument = CreateScopeArgument("global or project");
        var yesOption = CommandOptions.Create<bool>(
            "--yes",
            "Skip interactive confirmation.");
        var command = new Command("clear", "Clear memory content while preserving the file header.");
        command.Arguments.Add(scopeArgument);
        command.Options.Add(yesOption);
        command.SetAction(async parseResult =>
        {
            if (!TryGetScope(parseResult.GetValue(scopeArgument), allowEffective: false, out var scope))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory clear",
                    "error",
                    "Scope must be global or project.");
                return ExitCodes.InvalidArguments;
            }

            var service = CreateService(parseResult, workspaceOption, userHome);
            var path = GetPath(service, scope);
            var confirmed = parseResult.GetValue(yesOption);
            if (!confirmed && CliOutput.IsJson(parseResult, jsonOption))
            {
                WriteMessage(
                    parseResult,
                    jsonOption,
                    output,
                    "memory clear",
                    "error",
                    "--yes is required for non-interactive memory clear.",
                    path);
                return ExitCodes.InvalidArguments;
            }

            if (!confirmed)
            {
                output.Write($"Clear memory at '{path}'? Type yes to confirm: ");
                output.Flush();
                var confirmation = await input.ReadLineAsync();
                if (!string.Equals(confirmation?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
                {
                    WriteMessage(
                        parseResult,
                        jsonOption,
                        output,
                        "memory clear",
                        "cancelled",
                        "Memory was not cleared.",
                        path);
                    return ExitCodes.UserInterrupted;
                }
            }

            await service.ClearAsync(scope);
            WriteMessage(
                parseResult,
                jsonOption,
                output,
                "memory clear",
                "ok",
                $"Cleared memory: {path}",
                path);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Argument<string> CreateScopeArgument(string values)
    {
        return new Argument<string>("scope")
        {
            Description = $"Memory scope: {values}."
        };
    }

    private static MemoryFileService CreateService(
        ParseResult parseResult,
        Option<string?> workspaceOption,
        string? userHomeOverride)
    {
        var userHome = userHomeOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        var workspace = parseResult.GetValue(workspaceOption)
            ?? Environment.CurrentDirectory;
        return new MemoryFileService(
            Path.GetFullPath(userHome),
            Path.GetFullPath(workspace));
    }

    private static async Task<string?> TryEditWithConfiguredEditorAsync(string content)
    {
        var editor = GetConfiguredEditor();
        if (editor is null)
        {
            return null;
        }

        var tempPath = Path.Join(
            Path.GetTempPath(),
            $"madorin-memory-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Utf8).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo
            {
                FileName = editor,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(tempPath);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is Win32Exception
                or FileNotFoundException
                or DirectoryNotFoundException)
            {
                return null;
            }

            if (process is null)
            {
                return null;
            }

            using (process)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Configured editor exited with code {process.ExitCode}.");
                }
            }

            var bytes = await File.ReadAllBytesAsync(tempPath).ConfigureAwait(false);
            if (bytes.Length > MemoryFileService.MaximumFileSizeBytes)
            {
                throw new InvalidDataException(
                    "Edited memory exceeds the 32768-byte limit.");
            }

            try
            {
                return Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Edited memory is not valid UTF-8.", ex);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string? GetConfiguredEditor()
    {
        foreach (var variableName in new[] { "MADORIN_EDITOR", "VISUAL", "EDITOR" })
        {
            var editor = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(editor))
            {
                return editor.Trim();
            }
        }

        return null;
    }

    private static async Task<string?> ReadMultilineEditAsync(
        TextReader input,
        TextWriter output)
    {
        output.WriteLine("Enter replacement memory content. End with a single '.' line:");
        var lines = new List<string>();
        while (true)
        {
            var line = await input.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (string.Equals(line, ".", StringComparison.Ordinal))
            {
                return lines.Count == 0
                    ? string.Empty
                    : $"{string.Join('\n', lines)}\n";
            }

            lines.Add(line);
        }
    }

    private static string GetPath(MemoryFileService service, MemoryScope scope)
    {
        return scope switch
        {
            MemoryScope.Global => service.GlobalPath,
            MemoryScope.Project => service.ProjectPath!,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
    }

    private static bool TryGetScope(
        string? value,
        bool allowEffective,
        out MemoryScope scope)
    {
        if (string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Global;
            return true;
        }

        if (string.Equals(value, "project", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Project;
            return true;
        }

        if (allowEffective
            && string.Equals(value, "effective", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Effective;
            return true;
        }

        scope = default;
        return false;
    }

    private static void WriteContent(TextWriter output, string? content)
    {
        if (content is null)
        {
            output.WriteLine("(not found)");
            return;
        }

        if (content.Length == 0)
        {
            output.WriteLine("(empty)");
            return;
        }

        output.Write(content);
        if (!content.EndsWith('\n'))
        {
            output.WriteLine();
        }
    }

    private static void WriteMessage(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string status,
        string message,
        string? path = null)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine(message);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("status", status);
            writer.WriteString("message", message);
            if (path is not null)
            {
                writer.WriteString("path", path);
            }

            writer.WriteEndObject();
        });
    }
}
