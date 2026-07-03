using System.Globalization;

namespace Netor.Cortana.AI.WorkMode.Files;

/// <summary>
/// 三层工作模式的任务环境文件，对应工作区中的 environment.yaml。
/// </summary>
public sealed class WorkTaskEnvironmentFile
{
    public int Version { get; set; } = 1;

    public string TaskId { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public string Notes { get; set; } = string.Empty;
}

internal static class WorkTaskEnvironmentFileYaml
{
    public static string Serialize(WorkTaskEnvironmentFile environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var lines = new List<string>
        {
            $"version: {environment.Version.ToString(CultureInfo.InvariantCulture)}",
            $"task_id: {YamlScalar.Escape(environment.TaskId)}",
            $"updated_at: {YamlScalar.Escape(environment.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))}",
            "values:"
        };

        foreach (var pair in environment.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            lines.Add($"  {YamlScalar.EscapeKey(pair.Key)}: {YamlScalar.Escape(pair.Value)}");
        }

        lines.Add($"notes: {YamlScalar.Escape(environment.Notes)}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static WorkTaskEnvironmentFile Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var environment = new WorkTaskEnvironmentFile();
        var inValues = false;

        foreach (var rawLine in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal) && inValues)
            {
                if (YamlScalar.TrySplit(line[2..], out var valueKey, out var value))
                {
                    environment.Values[YamlScalar.Unescape(valueKey)] = YamlScalar.Unescape(value);
                }
                continue;
            }

            inValues = false;
            if (!YamlScalar.TrySplit(line, out var key, out var scalar))
            {
                continue;
            }

            switch (key)
            {
                case "version":
                    environment.Version = int.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 1;
                    break;
                case "task_id":
                    environment.TaskId = YamlScalar.Unescape(scalar);
                    break;
                case "updated_at":
                    environment.UpdatedAt = DateTimeOffset.TryParse(
                        YamlScalar.Unescape(scalar),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var timestamp)
                        ? timestamp
                        : DateTimeOffset.UtcNow;
                    break;
                case "values":
                    inValues = true;
                    break;
                case "notes":
                    environment.Notes = YamlScalar.Unescape(scalar);
                    break;
            }
        }

        return environment;
    }
}
