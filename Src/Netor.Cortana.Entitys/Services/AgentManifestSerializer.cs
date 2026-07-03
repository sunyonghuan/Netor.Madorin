using System.Globalization;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// manifest.yaml 的轻量读写器，覆盖当前 Agent schema 子集。
/// </summary>
public sealed class AgentManifestSerializer
{
    public AgentManifest Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var manifest = new AgentManifest();
        string? listKey = null;

        foreach (var rawLine in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal) && listKey is not null)
            {
                AddListValue(manifest, listKey, Unquote(line[4..].Trim()));
                continue;
            }

            listKey = null;
            var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length == 0 && IsListKey(key))
            {
                listKey = key;
                continue;
            }

            SetScalar(manifest, key, Unquote(value));
        }

        return manifest;
    }

    public string Serialize(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return string.Join(Environment.NewLine, BuildLines(manifest)) + Environment.NewLine;
    }

    private static IEnumerable<string> BuildLines(AgentManifest manifest)
    {
        yield return $"name: {EscapeScalar(manifest.Name)}";
        yield return $"display_name: {EscapeScalar(manifest.DisplayName)}";
        yield return $"description: {EscapeScalar(manifest.Description)}";
        yield return $"kind: {EscapeScalar(string.IsNullOrWhiteSpace(manifest.Kind) ? AgentManifestKinds.Agent : manifest.Kind)}";

        if (!string.IsNullOrWhiteSpace(manifest.Avatar))
        {
            yield return $"avatar: {EscapeScalar(manifest.Avatar)}";
        }

        yield return $"enabled: {ToYamlBool(manifest.Enabled)}";
        yield return $"sort_order: {manifest.SortOrder.ToString(CultureInfo.InvariantCulture)}";
        yield return $"allow_workflow_memory: {ToYamlBool(manifest.AllowWorkflowMemory)}";

        yield return "bound_plugins:";
        foreach (var pluginId in manifest.BoundPlugins.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            yield return $"  - {EscapeScalar(pluginId)}";
        }

        yield return "bound_mcp:";
        foreach (var mcpId in manifest.BoundMcp.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            yield return $"  - {EscapeScalar(mcpId)}";
        }
    }

    private static string StripComment(string line)
    {
        var quote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quote = !quote;
            }
            else if (!quote && line[i] == '#')
            {
                return line[..i].TrimEnd();
            }
        }

        return line.TrimEnd();
    }

    private static bool IsListKey(string key) =>
        string.Equals(key, "bound_plugins", StringComparison.Ordinal) ||
        string.Equals(key, "bound_mcp", StringComparison.Ordinal);

    private static void AddListValue(AgentManifest manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(key, "bound_plugins", StringComparison.Ordinal))
        {
            manifest.BoundPlugins.Add(value);
        }
        else if (string.Equals(key, "bound_mcp", StringComparison.Ordinal))
        {
            manifest.BoundMcp.Add(value);
        }
    }

    private static void SetScalar(AgentManifest manifest, string key, string value)
    {
        switch (key)
        {
            case "name":
                manifest.Name = value;
                break;
            case "display_name":
                manifest.DisplayName = value;
                break;
            case "description":
                manifest.Description = value;
                break;
            case "kind":
                manifest.Kind = string.IsNullOrWhiteSpace(value) ? AgentManifestKinds.Agent : value;
                break;
            case "avatar":
                manifest.Avatar = value;
                break;
            case "enabled":
                manifest.Enabled = ParseBool(value, defaultValue: true);
                break;
            case "sort_order":
                manifest.SortOrder = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortOrder) ? sortOrder : 0;
                break;
            case "allow_workflow_memory":
                manifest.AllowWorkflowMemory = ParseBool(value, defaultValue: true);
                break;
        }
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return value switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => defaultValue,
        };
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static string EscapeScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var requiresQuotes = value.Any(static c => char.IsWhiteSpace(c)) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal);
        return requiresQuotes
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string ToYamlBool(bool value) => value ? "true" : "false";
}
