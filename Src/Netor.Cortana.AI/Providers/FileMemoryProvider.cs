using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using System.Text;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 从文件系统提供记忆规则的 <see cref="AIContextProvider"/>。
/// 记忆文件位于工作目录下的 .cortana 文件夹中，格式：{项目名}-memory.md。
/// </summary>
/// <remarks>
/// 提供四个工具：
/// <list type="bullet">
///   <item>sys_read_memory: 读取当前项目的记忆内容（带行号）</item>
///   <item>sys_write_memory: 追加写入新的记忆内容</item>
///   <item>sys_edit_memory: 按行号修改指定行的内容</item>
///   <item>sys_delete_memory: 按行号删除指定行</item>
/// </list>
/// </remarks>
public sealed class FileMemoryProvider(IAppPaths appPaths, ILogger<FileMemoryProvider> logger) : AIContextProvider, IDisposable
{
    private readonly List<AITool> _tools = [];
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private string FileName => Path.Combine(
        appPaths.WorkspaceDirectory,
        ".cortana",
        "memory.md");

    private const string SystemInstructionPrefix =
        "### Please strictly follow the following rules when processing content\n\n";

    private const string SystemInstructionSuffix =
        "\n\n> Every processing task must carry the above memory content to ensure the output results comply with specification requirements";

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
        {
            RegisterTools();
        }

        var memoryContent = await LoadMemoryAsync(cancellationToken);
        var instructions = BuildInstructions(memoryContent);

        return new AIContext
        {
            Instructions = instructions,
            Tools = _tools
        };
    }

    /// <summary>
    /// 构建 AI 指令内容。
    /// </summary>
    private static string BuildInstructions(string memoryContent)
    {
        if (string.IsNullOrWhiteSpace(memoryContent))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(SystemInstructionPrefix);
        builder.AppendLine(memoryContent);
        builder.Append(SystemInstructionSuffix);

        return builder.ToString();
    }

    /// <summary>
    /// 注册 sys_read_memory / sys_write_memory 工具。
    /// </summary>
    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_read_memory",
            description: "Read the current project's memory rules (with line numbers). Call this tool before any development work. Each line in the returned content is prefixed with its line number, used for positioning by sys_edit_memory and sys_delete_memory.",
            method: async (CancellationToken ct) => await LoadMemoryWithLineNumbersAsync(ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_memory",
            description: "Append and write to the current project's memory rules. Parameters: content (Markdown-formatted rule content)",
            method: async (string content, CancellationToken ct) => await WriteMemoryAsync(content, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_edit_memory",
            description: "Modify a specified line in memory by line number. Parameters: lineNumber (1-based line number, obtain by calling sys_read_memory first), newContent (the new content for that line).",
            method: async (int lineNumber, string newContent, CancellationToken ct) => await EditMemoryLineAsync(lineNumber, newContent, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_memory",
            description: "Delete a specified line in memory by line number. Parameters: lineNumber (1-based line number, obtain by calling sys_read_memory first).",
            method: async (int lineNumber, CancellationToken ct) => await DeleteMemoryLineAsync(lineNumber, ct)));
    }

    /// <summary>
    /// 异步读取记忆内容（带行号，供工具调用）。
    /// </summary>
    private async Task<string> LoadMemoryWithLineNumbersAsync(CancellationToken cancellationToken = default)
    {
        var content = await LoadMemoryAsync(cancellationToken);
        if (string.IsNullOrEmpty(content))
            return "记忆为空。";

        var lines = content.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.AppendLine($"{i + 1:D3}| {lines[i].TrimEnd('\r')}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 异步读取记忆内容（原始格式，供指令构建使用）。
    /// </summary>
    private async Task<string> LoadMemoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FileName))
            {
                return string.Empty;
            }

            await using var fileStream = new FileStream(
                FileName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, useAsync: true);

            using var reader = new StreamReader(fileStream, Encoding.UTF8);

            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("加载记忆被取消");
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载记忆失败");
            return string.Empty;
        }
    }

    /// <summary>
    /// 异步写入记忆内容（追加模式）。
    /// </summary>
    private async Task<string> WriteMemoryAsync(string content, CancellationToken cancellationToken = default)
    {
        var normalizedContent = content?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return "错误：内容为空";
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureMemoryDirectory();

            var fileMode = File.Exists(FileName) ? FileMode.Append : FileMode.Create;

            await using var fileStream = new FileStream(
                FileName, fileMode, FileAccess.Write,
                FileShare.Read, 4096, useAsync: true);

            await using var writer = new StreamWriter(fileStream, Encoding.UTF8);

            if (fileMode == FileMode.Append)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await writer.WriteAsync(normalizedContent).ConfigureAwait(false);

            logger.LogInformation("记忆已写入：{FilePath}", FileName);
            return "✓ 记忆已保存";
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("写入被取消");
            return "错误：操作被取消";
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogError("无权限访问记忆文件");
            return "错误：权限不足";
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "文件被占用");
            return "错误：文件被占用，请稍后重试";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "写入失败");
            return "错误：保存失败";
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 按行号修改指定行的内容。
    /// </summary>
    private async Task<string> EditMemoryLineAsync(int lineNumber, string newContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newContent))
            return "错误：新内容不能为空。";

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var content = await LoadMemoryAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
                return "错误：记忆为空，没有可修改的内容。";

            var lines = new List<string>(content.Split('\n'));
            // 处理行尾 \r
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].TrimEnd('\r');

            if (lineNumber < 1 || lineNumber > lines.Count)
                return $"错误：行号 {lineNumber} 超出范围，有效范围 1~{lines.Count}。";

            var oldLine = lines[lineNumber - 1];
            lines[lineNumber - 1] = newContent.Trim();

            EnsureMemoryDirectory();
            await File.WriteAllTextAsync(FileName, string.Join("\n", lines), Encoding.UTF8, cancellationToken);

            logger.LogInformation("记忆第 {Line} 行已修改", lineNumber);
            return $"✓ 第 {lineNumber} 行已修改。\n  旧：{oldLine}\n  新：{lines[lineNumber - 1]}";
        }
        catch (OperationCanceledException)
        {
            return "错误：操作被取消";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改记忆第 {Line} 行失败", lineNumber);
            return $"错误：修改失败 - {ex.Message}";
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 按行号删除指定行。
    /// </summary>
    private async Task<string> DeleteMemoryLineAsync(int lineNumber, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var content = await LoadMemoryAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
                return "错误：记忆为空，没有可删除的内容。";

            var lines = new List<string>(content.Split('\n'));
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].TrimEnd('\r');

            if (lineNumber < 1 || lineNumber > lines.Count)
                return $"错误：行号 {lineNumber} 超出范围，有效范围 1~{lines.Count}。";

            var deletedLine = lines[lineNumber - 1];
            lines.RemoveAt(lineNumber - 1);

            EnsureMemoryDirectory();
            await File.WriteAllTextAsync(FileName, string.Join("\n", lines), Encoding.UTF8, cancellationToken);

            logger.LogInformation("记忆第 {Line} 行已删除", lineNumber);
            return $"✓ 第 {lineNumber} 行已删除。\n  已删除：{deletedLine}";
        }
        catch (OperationCanceledException)
        {
            return "错误：操作被取消";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除记忆第 {Line} 行失败", lineNumber);
            return $"错误：删除失败 - {ex.Message}";
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 确保记忆目录存在。
    /// </summary>
    private void EnsureMemoryDirectory()
    {
        var directory = Path.GetDirectoryName(FileName);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        _fileLock.Dispose();
    }
}