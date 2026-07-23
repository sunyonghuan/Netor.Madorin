using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Maintains atomic immutable snapshots of built-in and host tool catalogs.</summary>
public sealed partial class ToolCatalogStore : IToolCatalogStore
{
    private readonly object _sync = new();
    private readonly IToolSchemaValidator _schemaValidator;
    private readonly ToolDescriptor[] _builtins;
    private Dictionary<string, ToolDescriptor> _hostTools = new(StringComparer.Ordinal);
    private string _hostCatalogVersion = string.Empty;
    private string _hostContentHash;
    private ToolCatalogSnapshot _current;

    public ToolCatalogStore(
        IBuiltinToolRegistry builtinRegistry,
        IToolSchemaValidator schemaValidator)
    {
        ArgumentNullException.ThrowIfNull(builtinRegistry);
        _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
        _builtins = ValidateBuiltins(builtinRegistry.GetTools());
        _hostContentHash = ComputeCatalogHash([]);
        _current = CreateSnapshot();
    }

    public ToolCatalogSnapshot CaptureSnapshot() => Volatile.Read(ref _current);

    public ToolCatalogSnapshot ReplaceHostCatalog(ToolCatalogReplaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateVersion(request.CatalogVersion, nameof(request.CatalogVersion));
        var replacement = ValidateHostTools(request.Tools);
        var replacementHash = ComputeCatalogHash(replacement.Values);

        lock (_sync)
        {
            if (string.Equals(_hostCatalogVersion, request.CatalogVersion, StringComparison.Ordinal))
            {
                if (!string.Equals(_hostContentHash, replacementHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Catalog version '{request.CatalogVersion}' is already bound to different content.");
                }

                return _current;
            }

            _hostTools = replacement;
            _hostCatalogVersion = request.CatalogVersion;
            _hostContentHash = replacementHash;
            _current = CreateSnapshot();
            return _current;
        }
    }

    public ToolCatalogSnapshot PatchHostCatalog(ToolCatalogPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateVersion(request.BaseVersion, nameof(request.BaseVersion));
        ValidateVersion(request.NewVersion, nameof(request.NewVersion));
        if (string.Equals(request.BaseVersion, request.NewVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("Patch versions must advance.", nameof(request));
        }

        var upserts = ValidateHostTools(request.Upserts);
        var removals = request.Removals
            ?? throw new ArgumentException("Patch removals are required.", nameof(request));
        if (removals.Length != removals.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Patch removals contain duplicate tool ids.", nameof(request));
        }

        if (removals.Any(upserts.ContainsKey))
        {
            throw new ArgumentException("A patch cannot upsert and remove the same tool id.", nameof(request));
        }

        lock (_sync)
        {
            if (!string.Equals(_hostCatalogVersion, request.BaseVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Catalog patch expected base '{request.BaseVersion}', but current host version is '{_hostCatalogVersion}'.");
            }

            var updated = new Dictionary<string, ToolDescriptor>(_hostTools, StringComparer.Ordinal);
            foreach (var toolId in removals)
            {
                if (!updated.Remove(toolId))
                {
                    throw new InvalidOperationException(
                        $"Catalog patch cannot remove unknown tool '{toolId}'.");
                }
            }

            foreach (var (toolId, descriptor) in upserts)
            {
                updated[toolId] = descriptor;
            }

            _hostTools = updated;
            _hostCatalogVersion = request.NewVersion;
            _hostContentHash = ComputeCatalogHash(updated.Values);
            _current = CreateSnapshot();
            return _current;
        }
    }

    private ToolCatalogSnapshot CreateSnapshot()
    {
        var merged = _builtins.Concat(_hostTools.Values).ToArray();
        return new ToolCatalogSnapshot(
            _hostCatalogVersion,
            $"sha256:{ComputeCatalogHash(merged)}",
            merged);
    }

    private ToolDescriptor[] ValidateBuiltins(IReadOnlyList<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var copies = new List<ToolDescriptor>(descriptors.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!descriptor.ToolId.StartsWith("builtin.", StringComparison.Ordinal)
                || !ids.Add(descriptor.ToolId))
            {
                throw new InvalidOperationException(
                    $"Built-in tool id '{descriptor.ToolId}' is invalid or duplicated.");
            }

            ValidateDescriptor(descriptor);
            copies.Add(CloneDescriptor(descriptor));
        }

        return copies.ToArray();
    }

    private Dictionary<string, ToolDescriptor> ValidateHostTools(ToolCatalogItem[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var tools = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            var match = HostToolIdRegex().Match(item.ToolId);
            if (!match.Success)
            {
                throw new ArgumentException(
                    $"Host tool id '{item.ToolId}' must use host.* or mcp.<server>.*.",
                    nameof(items));
            }

            var expectedTarget = item.ToolId.StartsWith("host.", StringComparison.Ordinal)
                ? ToolExecutionTarget.Host
                : ToolExecutionTarget.Mcp;
            if (item.ExecutionTarget != expectedTarget)
            {
                throw new ArgumentException(
                    $"Tool '{item.ToolId}' has execution target '{item.ExecutionTarget}', expected '{expectedTarget}'.",
                    nameof(items));
            }

            var descriptor = new ToolDescriptor(
                item.ToolId,
                match.Groups["namespace"].Value,
                item.DisplayName,
                item.Description,
                item.InputSchema.GetRawText(),
                item.OutputSchema.GetRawText(),
                item.Risk.ToString(),
                item.RequiresApproval,
                item.TimeoutSeconds,
                item.Tags,
                item.Capabilities,
                item.Risk,
                item.ExecutionTarget,
                item.IsIdempotent);
            ValidateDescriptor(descriptor);
            if (!tools.TryAdd(descriptor.ToolId, CloneDescriptor(descriptor)))
            {
                throw new ArgumentException(
                    $"Host catalog contains duplicate tool id '{descriptor.ToolId}'.",
                    nameof(items));
            }
        }

        return tools;
    }

    private void ValidateDescriptor(ToolDescriptor descriptor)
    {
        if (descriptor.TimeoutSeconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "Tool timeouts must be between 1 and 3600 seconds.");
        }

        var schemaResult = _schemaValidator.ValidateDescriptor(descriptor);
        if (!schemaResult.IsValid)
        {
            throw new ArgumentException(
                $"Tool '{descriptor.ToolId}' has an invalid schema: {schemaResult.Error}",
                nameof(descriptor));
        }
    }

    private static void ValidateVersion(string version, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version, parameterName);
        if (version.Length > 128)
        {
            throw new ArgumentException("Catalog versions cannot exceed 128 characters.", parameterName);
        }
    }

    private static string ComputeCatalogHash(IEnumerable<ToolDescriptor> descriptors)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var descriptor in descriptors.OrderBy(static item => item.ToolId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("toolId", descriptor.ToolId);
                writer.WriteString("namespace", descriptor.Namespace);
                writer.WriteString("displayName", descriptor.DisplayName);
                writer.WriteString("description", descriptor.Description);
                writer.WritePropertyName("inputSchema");
                WriteCanonicalJson(writer, descriptor.InputSchemaJson);
                writer.WritePropertyName("outputSchema");
                WriteCanonicalJson(writer, descriptor.OutputSchemaJson);
                writer.WriteString("risk", descriptor.Risk.ToString());
                writer.WriteBoolean("requiresApproval", descriptor.RequiresApproval);
                writer.WriteNumber("timeoutSeconds", descriptor.TimeoutSeconds);
                WriteSortedStrings(writer, "tags", descriptor.Tags);
                WriteSortedStrings(writer, "capabilities", descriptor.Capabilities);
                writer.WriteString("executionTarget", descriptor.ExecutionTarget.ToString());
                writer.WriteBoolean("isIdempotent", descriptor.IsIdempotent);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, string json)
    {
        using var document = JsonDocument.Parse(json);
        WriteCanonicalElement(writer, document.RootElement);
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteSortedStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string>? values)
    {
        writer.WriteStartArray(propertyName);
        var orderedValues = values is null
            ? Enumerable.Empty<string>()
            : values.Order(StringComparer.Ordinal);
        foreach (var value in orderedValues)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    [GeneratedRegex(
        "^(?<namespace>host)\\.[A-Za-z0-9][A-Za-z0-9_.-]*$|^(?<namespace>mcp\\.[A-Za-z0-9][A-Za-z0-9_-]*)\\.[A-Za-z0-9][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostToolIdRegex();

    private static ToolDescriptor CloneDescriptor(ToolDescriptor descriptor) =>
        descriptor with
        {
            Tags = descriptor.Tags is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Tags.ToArray()),
            Capabilities = descriptor.Capabilities is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Capabilities.ToArray())
        };
}
