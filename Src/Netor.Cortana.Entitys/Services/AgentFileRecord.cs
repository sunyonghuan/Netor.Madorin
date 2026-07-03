namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// Agent 文件目录扫描后的内存记录。
/// </summary>
public sealed record AgentFileRecord(
    AgentManifest Manifest,
    string DirectoryPath,
    string ManifestPath,
    string PromptPath,
    string Prompt);
