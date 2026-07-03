using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using System.Text;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 从文件系统提供当前项目设置的 <see cref="AIContextProvider"/>。
/// 项目设置文件位于工作目录下的 .cortana 文件夹中，优先使用 project-settings.md，并兼容旧版 memory.md。
/// </summary>
/// <remarks>
/// 提供项目设置工具，并保留旧版记忆工具名兼容。
/// <list type="bullet">
///   <item>sys_read_settings: 读取当前项目设置（带行号）</item>
///   <item>sys_write_settings: 追加写入当前项目设置</item>
///   <item>sys_edit_settings: 按行号修改指定行的内容</item>
///   <item>sys_delete_settings: 按行号删除指定行</item>
///   <item>sys_clear_settings: 清除当前项目设置</item>
/// </list>
/// </remarks>
public sealed class ProjectSettingsProvider(IAppPaths appPaths, ILogger<ProjectSettingsProvider> logger) : AIContextProvider, IDisposable
{
    private readonly List<AITool> _tools = [];
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private string SettingsDirectory => Path.Combine(
        appPaths.WorkspaceDirectory,
        ".cortana");

    private string PreferredSettingsFilePath => Path.Combine(SettingsDirectory, "project-settings.md");

    private string LegacySettingsFilePath => Path.Combine(SettingsDirectory, "memory.md");

    private string ActiveSettingsFilePath => File.Exists(PreferredSettingsFilePath)
        ? PreferredSettingsFilePath
        : File.Exists(LegacySettingsFilePath)
            ? LegacySettingsFilePath
            : PreferredSettingsFilePath;

    private const string SystemInstructionPrefix =
        "--project-settings--\n\nThe following content is the current project's AI settings. Use it to understand project-specific rules, commands, structure, and notes.\n\n";

    private const string SystemInstructionSuffix =
        "\n\n> The above project settings apply only to the current project.";

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
        {
            RegisterTools();
        }

        var settingsContent = await LoadSettingsAsync(cancellationToken);
        var instructions = BuildInstructions(settingsContent);

        return new AIContext
        {
            Instructions = instructions,
            Tools = _tools
        };
    }

    /// <summary>
    /// 构建 AI 指令内容。
    /// </summary>
    private static string BuildInstructions(string settingsContent)
    {
        if (string.IsNullOrWhiteSpace(settingsContent))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(SystemInstructionPrefix);
        builder.AppendLine(settingsContent);
        builder.Append(SystemInstructionSuffix);

        return builder.ToString();
    }

    /// <summary>
    /// 注册项目设置工具与旧版兼容工具。
    /// </summary>
    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_read_settings",
            description: "Read the current project's AI settings with line numbers. Each line is prefixed with its line number for sys_edit_settings and sys_delete_settings.",
            method: async (CancellationToken ct) => await LoadSettingsWithLineNumbersAsync(ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_settings",
            description: "Append content to the current project's AI settings. Parameters: content (Markdown-formatted settings content).",
            method: async (string content, CancellationToken ct) => await WriteSettingsAsync(content, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_edit_settings",
            description: "Modify a specified line in the current project's AI settings. Parameters: lineNumber (1-based), newContent.",
            method: async (int lineNumber, string newContent, CancellationToken ct) => await EditSettingsLineAsync(lineNumber, newContent, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_settings",
            description: "Delete a specified line in the current project's AI settings. Parameters: lineNumber (1-based).",
            method: async (int lineNumber, CancellationToken ct) => await DeleteSettingsLineAsync(lineNumber, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_clear_settings",
            description: "Clear all content in the current project's AI settings file.",
            method: async (CancellationToken ct) => await ClearSettingsAsync(ct)));

        RegisterLegacyTools();
    }

    /// <summary>
    /// 注册旧版 memory 工具名，避免旧会话和历史提示断裂。
    /// </summary>
    private void RegisterLegacyTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_read_memory",
            description: "Deprecated. Use sys_read_settings instead.",
            method: async (CancellationToken ct) => await LoadSettingsWithLineNumbersAsync(ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_memory",
            description: "Deprecated. Use sys_write_settings instead.",
            method: async (string content, CancellationToken ct) => await WriteSettingsAsync(content, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_edit_memory",
            description: "Deprecated. Use sys_edit_settings instead.",
            method: async (int lineNumber, string newContent, CancellationToken ct) => await EditSettingsLineAsync(lineNumber, newContent, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_memory",
            description: "Deprecated. Use sys_delete_settings instead.",
            method: async (int lineNumber, CancellationToken ct) => await DeleteSettingsLineAsync(lineNumber, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_clear_memory",
            description: "Deprecated. Use sys_clear_settings instead.",
            method: async (CancellationToken ct) => await ClearSettingsAsync(ct)));
    }

    /// <summary>
    /// 异步读取项目设置内容（带行号，供工具调用）。
    /// </summary>
    private async Task<string> LoadSettingsWithLineNumbersAsync(CancellationToken cancellationToken = default)
    {
        var content = await LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrEmpty(content))
            return "项目设置为空。";

        var lines = content.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.AppendLine($"{i + 1:D3}| {lines[i].TrimEnd('\r')}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 异步读取项目设置内容（原始格式，供指令构建使用）。
    /// </summary>
    private async Task<string> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = ActiveSettingsFilePath;
            if (!File.Exists(filePath))
            {
                return string.Empty;
            }

            await using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, useAsync: true);

            using var reader = new StreamReader(fileStream, Encoding.UTF8);

            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("加载项目设置被取消");
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载项目设置失败");
            return string.Empty;
        }
    }

    /// <summary>
    /// 异步写入项目设置内容（追加模式）。
    /// </summary>
    private async Task<string> WriteSettingsAsync(string content, CancellationToken cancellationToken = default)
    {
        var normalizedContent = content?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return "错误：内容为空";
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureSettingsDirectory();

            var filePath = ActiveSettingsFilePath;

            var fileMode = File.Exists(filePath) ? FileMode.Append : FileMode.Create;

            await using var fileStream = new FileStream(
                filePath, fileMode, FileAccess.Write,
                FileShare.Read, 4096, useAsync: true);

            await using var writer = new StreamWriter(fileStream, Encoding.UTF8);

            if (fileMode == FileMode.Append)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await writer.WriteAsync(normalizedContent).ConfigureAwait(false);

            logger.LogInformation("项目设置已写入：{FilePath}", filePath);
            return "✓ 项目设置已保存";
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("写入被取消");
            return "错误：操作被取消";
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogError("无权限访问项目设置文件");
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
    private async Task<string> EditSettingsLineAsync(int lineNumber, string newContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newContent))
            return "错误：新内容不能为空。";

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var content = await LoadSettingsAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
                return "错误：项目设置为空，没有可修改的内容。";

            var lines = new List<string>(content.Split('\n'));
            // 处理行尾 \r
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].TrimEnd('\r');

            if (lineNumber < 1 || lineNumber > lines.Count)
                return $"错误：行号 {lineNumber} 超出范围，有效范围 1~{lines.Count}。";

            var oldLine = lines[lineNumber - 1];
            lines[lineNumber - 1] = newContent.Trim();

            EnsureSettingsDirectory();

            var filePath = ActiveSettingsFilePath;
            await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8, cancellationToken);

            logger.LogInformation("项目设置第 {Line} 行已修改", lineNumber);
            return $"✓ 第 {lineNumber} 行已修改。\n  旧：{oldLine}\n  新：{lines[lineNumber - 1]}";
        }
        catch (OperationCanceledException)
        {
            return "错误：操作被取消";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改项目设置第 {Line} 行失败", lineNumber);
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
    private async Task<string> DeleteSettingsLineAsync(int lineNumber, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var content = await LoadSettingsAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
                return "错误：项目设置为空，没有可删除的内容。";

            var lines = new List<string>(content.Split('\n'));
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].TrimEnd('\r');

            if (lineNumber < 1 || lineNumber > lines.Count)
                return $"错误：行号 {lineNumber} 超出范围，有效范围 1~{lines.Count}。";

            var deletedLine = lines[lineNumber - 1];
            lines.RemoveAt(lineNumber - 1);

            EnsureSettingsDirectory();

            var filePath = ActiveSettingsFilePath;
            await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8, cancellationToken);

            logger.LogInformation("项目设置第 {Line} 行已删除", lineNumber);
            return $"✓ 第 {lineNumber} 行已删除。\n  已删除：{deletedLine}";
        }
        catch (OperationCanceledException)
        {
            return "错误：操作被取消";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除项目设置第 {Line} 行失败", lineNumber);
            return $"错误：删除失败 - {ex.Message}";
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 清除当前项目设置的全部内容。
    /// </summary>
    private async Task<string> ClearSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var filePath = ActiveSettingsFilePath;
            if (!File.Exists(filePath))
            {
                return "项目设置为空，无需清除。";
            }

            EnsureSettingsDirectory();
            await File.WriteAllTextAsync(filePath, string.Empty, Encoding.UTF8, cancellationToken);

            logger.LogInformation("项目设置已清除：{FilePath}", filePath);
            return "✓ 项目设置已清除";
        }
        catch (OperationCanceledException)
        {
            return "错误：操作被取消";
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogError("无权限访问项目设置文件");
            return "错误：权限不足";
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "文件被占用");
            return "错误：文件被占用，请稍后重试";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清除项目设置失败");
            return $"错误：清除失败 - {ex.Message}";
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 确保项目设置目录存在。
    /// </summary>
    private void EnsureSettingsDirectory()
    {
        var directory = SettingsDirectory;
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