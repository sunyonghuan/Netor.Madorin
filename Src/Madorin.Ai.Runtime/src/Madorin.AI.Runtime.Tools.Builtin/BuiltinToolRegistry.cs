using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed class BuiltinToolRegistry : IBuiltinToolRegistry
{
    public const string MemoryReadToolId = "builtin.memory.read";
    public const string MemoryAppendToolId = "builtin.memory.append";
    public const string FileListToolId = "builtin.fs.list";
    public const string FileStatusToolId = "builtin.fs.status";
    public const string FileReadToolId = "builtin.fs.read";
    public const string FileWriteToolId = "builtin.fs.write";
    public const string DirectoryCreateToolId = "builtin.fs.mkdir";
    public const string FileCopyToolId = "builtin.fs.copy";
    public const string FileMoveToolId = "builtin.fs.move";
    public const string FileDeleteToolId = "builtin.fs.delete";
    public const string FileSearchToolId = "builtin.fs.search";
    public const string FilePatchToolId = "builtin.fs.patch";
    public const string ContentReadToolId = "builtin.content.read";
    public const string ProcessRunToolId = "builtin.process.run";
    public const string PowerShellRunToolId = "builtin.powershell.run";
    public const string HttpRequestToolId = "builtin.http.request";

    private static readonly IReadOnlyList<ToolDescriptor> Tools = Array.AsReadOnly<ToolDescriptor>(
    [
        new(
            MemoryReadToolId,
            "builtin",
            "Read memory",
            "Reads global, project, or effective Runtime memory.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project","effective"]}},"required":["scope"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":["string","null"]},"hash":{"type":["string","null"]}},"required":["content","hash"],"additionalProperties":false}""",
            Capabilities: ["memory.read"]),
        new(
            MemoryAppendToolId,
            "builtin",
            "Append memory",
            "Appends one user-approved item to global or project Runtime memory.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"item":{"type":"string"}},"required":["scope","item"],"additionalProperties":false}""",
            """{"type":"object","properties":{"appended":{"type":"boolean"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["appended","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Write),
        new(
            FileListToolId,
            "builtin",
            "List files",
            "Lists files and directories within an authorized workspace path.",
            """{"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":false}""",
            """{"type":"object","properties":{"entries":{"type":"array"}},"required":["entries"]}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileStatusToolId,
            "builtin",
            "Inspect path",
            "Returns safe metadata for an authorized file or directory.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exists":{"type":"boolean"},"kind":{"type":["string","null"]},"byteLength":{"type":["integer","null"]},"lastWriteTime":{"type":["string","null"]}},"required":["exists","kind","byteLength","lastWriteTime"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileReadToolId,
            "builtin",
            "Read file",
            "Reads an authorized UTF-8 workspace file up to 1 MB.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":"string"},"encoding":{"type":"string"},"byteLength":{"type":"integer"}},"required":["content","encoding","byteLength"]}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileWriteToolId,
            "builtin",
            "Write file",
            "Atomically writes an authorized UTF-8 workspace file up to 1 MB.",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}""",
            """{"type":"object","properties":{"written":{"type":"boolean"},"byteLength":{"type":"integer"}},"required":["written","byteLength"]}""",
            RiskLevel: "medium",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            DirectoryCreateToolId,
            "builtin",
            "Create directory",
            "Creates a directory within an authorized workspace path.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"created":{"type":"boolean"}},"required":["created"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write),
        new(
            FileCopyToolId,
            "builtin",
            "Copy file",
            "Copies one authorized workspace file.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"],"additionalProperties":false}""",
            """{"type":"object","properties":{"copied":{"type":"boolean"}},"required":["copied"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            FileMoveToolId,
            "builtin",
            "Move file",
            "Moves one authorized workspace file when the Grant allows moves.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"],"additionalProperties":false}""",
            """{"type":"object","properties":{"moved":{"type":"boolean"}},"required":["moved"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write", "filesystem.destructive"],
            Risk: ToolRiskLevel.Destructive,
            IsIdempotent: false),
        new(
            FileDeleteToolId,
            "builtin",
            "Delete path",
            "Deletes one authorized file or an empty directory when the Grant allows deletion.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"deleted":{"type":"boolean"}},"required":["deleted"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write", "filesystem.destructive"],
            Risk: ToolRiskLevel.Destructive,
            IsIdempotent: false),
        new(
            FileSearchToolId,
            "builtin",
            "Search text",
            "Searches bounded UTF-8 files below an authorized directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string"},"maxResults":{"type":"integer","minimum":1,"maximum":100}},"required":["path","query"],"additionalProperties":false}""",
            """{"type":"object","properties":{"matches":{"type":"array"}},"required":["matches"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FilePatchToolId,
            "builtin",
            "Patch lines",
            "Replaces an inclusive line range only when the expected SHA-256 matches.",
            """{"type":"object","properties":{"path":{"type":"string"},"expectedHash":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"replacement":{"type":"string"}},"required":["path","expectedHash","startLine","endLine","replacement"],"additionalProperties":false}""",
            """{"type":"object","properties":{"patched":{"type":"boolean"},"hash":{"type":"string"}},"required":["patched","hash"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            ContentReadToolId,
            "builtin",
            "Read content page",
            "Reads a bounded page with encoding, media type, SHA-256, and summary text.",
            """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1,"maximum":65536}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":"string"},"encoding":{"type":"string"},"mediaType":{"type":"string"},"sha256":{"type":"string"},"offset":{"type":"integer"},"nextOffset":{"type":["integer","null"]},"summary":{"type":"string"}},"required":["content","encoding","mediaType","sha256","offset","nextOffset","summary"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            ProcessRunToolId,
            "builtin",
            "Run process",
            "Runs an allowlisted executable without shell expansion.",
            """{"type":"object","properties":{"fileName":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"workingDirectory":{"type":"string"},"environment":{"type":"object","additionalProperties":{"type":"string"}},"stdin":{"type":"string"}},"required":["fileName"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"},"stdoutTruncated":{"type":"boolean"},"stderrTruncated":{"type":"boolean"}},"required":["exitCode","stdout","stderr","stdoutTruncated","stderrTruncated"],"additionalProperties":false}""",
            Capabilities: ["process.execute"],
            Risk: ToolRiskLevel.Process,
            IsIdempotent: false),
        new(
            PowerShellRunToolId,
            "builtin",
            "Run PowerShell",
            "Runs an approved PowerShell script through standard input.",
            """{"type":"object","properties":{"script":{"type":"string"},"workingDirectory":{"type":"string"}},"required":["script"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"},"stdoutTruncated":{"type":"boolean"},"stderrTruncated":{"type":"boolean"}},"required":["exitCode","stdout","stderr","stdoutTruncated","stderrTruncated"],"additionalProperties":false}""",
            RequiresApproval: true,
            Capabilities: ["powershell.execute"],
            Risk: ToolRiskLevel.PowerShell,
            IsIdempotent: false),
        new(
            HttpRequestToolId,
            "builtin",
            "HTTP request",
            "Performs a bounded request allowed by the effective network policy.",
            """{"type":"object","properties":{"method":{"type":"string","enum":["GET","HEAD","POST","PUT","PATCH","DELETE"]},"url":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"body":{"type":"string"}},"required":["method","url"],"additionalProperties":false}""",
            """{"type":"object","properties":{"statusCode":{"type":"integer"},"headers":{"type":"object"},"body":{"type":"string"},"mediaType":{"type":["string","null"]}},"required":["statusCode","headers","body","mediaType"],"additionalProperties":false}""",
            Capabilities: ["network.access"],
            Risk: ToolRiskLevel.Network,
            IsIdempotent: false)
    ]);

    public string CatalogVersion => "1.0.0";

    public IReadOnlyList<ToolDescriptor> GetTools() => Tools;

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var requested = toolIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
    }
}
