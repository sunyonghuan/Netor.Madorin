using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Files;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 三层工作模式的任务环境工具，负责维护 environment.yaml。
/// </summary>
public sealed class EnvironmentTools(WorkTaskFileService fileService, string taskId)
{
    public AIFunction CreateSetEnvironmentTool()
    {
        [Description("设置当前工作任务的环境信息，写入 environment.yaml。")]
        Task<string> SetEnvironmentAsync(
            [Description("环境键值 JSON 对象，例如 {\"output_dir\":\"novel/volume-1\",\"style\":\"冷峻克制\"}。")] string valuesJson,
            [Description("补充说明，可为空。")] string? notes,
            CancellationToken ct)
        {
            try
            {
                var values = ParseValues(valuesJson);
                var environment = new WorkTaskEnvironmentFile
                {
                    TaskId = taskId,
                    Values = values,
                    Notes = notes?.Trim() ?? string.Empty
                };

                fileService.SaveEnvironment(environment);
                return Task.FromResult($"环境已保存，共 {values.Count} 个键。");
            }
            catch (JsonException ex)
            {
                return Task.FromResult($"错误：环境 JSON 格式无效 - {ex.Message}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"错误：保存环境失败 - {ex.Message}");
            }
        }

        return AIFunctionFactory.Create(SetEnvironmentAsync, new AIFunctionFactoryOptions
        {
            Name = "set_environment",
            Description = "写入三层工作模式任务环境。用于保存输出目录、风格约束、外部账号、任务背景等可被后续专员步骤读取的信息。"
        });
    }

    private static Dictionary<string, string> ParseValues(string valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(valuesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("根节点必须是 JSON 对象。");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            values[property.Name.Trim()] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return values;
    }
}
