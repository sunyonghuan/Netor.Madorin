using System.Text.RegularExpressions;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 文件版 Agent manifest 的严格 schema 校验器。
/// </summary>
public sealed partial class AgentManifestValidator
{
    public AgentManifestValidationResult Validate(AgentManifest? manifest, string? directoryName)
    {
        if (manifest is null)
        {
            return AgentManifestValidationResult.Fail("manifest 为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return AgentManifestValidationResult.Fail("name 不能为空。");
        }

        if (!AgentNameRegex().IsMatch(manifest.Name))
        {
            return AgentManifestValidationResult.Fail("name 只能包含字母、数字、下划线、短横线，长度 1-64。");
        }

        if (!string.IsNullOrWhiteSpace(directoryName) &&
            !string.Equals(manifest.Name, directoryName, StringComparison.Ordinal))
        {
            return AgentManifestValidationResult.Fail("name 必须与目录名严格一致。");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            return AgentManifestValidationResult.Fail("display_name 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            return AgentManifestValidationResult.Fail("description 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            manifest.Kind = AgentManifestKinds.Agent;
        }

        if (!string.Equals(manifest.Kind, AgentManifestKinds.Agent, StringComparison.Ordinal) &&
            !string.Equals(manifest.Kind, AgentManifestKinds.BuiltinSystem, StringComparison.Ordinal))
        {
            return AgentManifestValidationResult.Fail("kind 只能是 agent 或 builtin/system。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Avatar))
        {
            if (Path.IsPathRooted(manifest.Avatar))
            {
                return AgentManifestValidationResult.Fail("avatar 必须是相对 manifest 所在目录的路径。");
            }

            var normalizedAvatar = manifest.Avatar.Replace('\\', '/');
            if (normalizedAvatar.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                return AgentManifestValidationResult.Fail("avatar 必须位于 manifest 所在目录内。");
            }

            var extension = Path.GetExtension(manifest.Avatar);
            if (!IsSupportedAvatarExtension(extension))
            {
                return AgentManifestValidationResult.Fail("avatar 仅支持 png/jpg/jpeg/webp。");
            }
        }

        return AgentManifestValidationResult.Success;
    }

    private static bool IsSupportedAvatarExtension(string extension) =>
        string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentNameRegex();
}
