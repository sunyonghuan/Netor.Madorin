using System.Text.Json;

using Netor.Cortana.Entitys.Proxy;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// 将 Cortana 数据库模型包装成 Ollama 本地量化模型形状。
/// </summary>
internal static class OllamaModelShapeFactory
{
    public const int ExposedContextLength = 262_144;

    public static OllamaModelInfo CreateTagModel(AiProxyModelDescriptor model, DateTimeOffset modifiedAt)
    {
        var exposedName = ProxyModelNameMapper.ToExposedModelName(model.Name);
        return new OllamaModelInfo(
            exposedName,
            exposedName,
            modifiedAt,
            EstimateModelSize(ExposedContextLength),
            ComputeDigest(model.ModelId),
            CreateDetails());
    }

    public static OllamaShowResponse CreateShowResponse(string exposedModelName)
    {
        return new OllamaShowResponse(
            License: string.Empty,
            Modelfile: $"FROM {exposedModelName}",
            Parameters: string.Empty,
            Template: "{{ .Prompt }}",
            Details: CreateDetails(),
            ModelInfo: CreateModelInfo(),
            Capabilities: ["completion", "tools", "vision"],
            ModifiedAt: OllamaHttpResponseWriter.FormatUtcNow());
    }

    public static OllamaModelDetails CreateDetails()
    {
        return new OllamaModelDetails(
            ParentModel: string.Empty,
            Format: "gguf",
            Family: "cortana",
            Families: ["cortana"],
            ParameterSize: EstimateParameterSize(ExposedContextLength),
            QuantizationLevel: "Q4_K_M");
    }

    public static Dictionary<string, JsonElement> CreateModelInfo()
    {
        return new Dictionary<string, JsonElement>
        {
            ["general.architecture"] = CreateJsonElement("\"cortana\""),
            ["general.file_type"] = CreateJsonElement("15"),
            ["general.parameter_count"] = CreateJsonElement("0"),
            ["cortana.context_length"] = CreateJsonElement(ExposedContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["context_length"] = CreateJsonElement(ExposedContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
    }

    private static JsonElement CreateJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static long EstimateModelSize(int contextLength)
    {
        var ctxK = Math.Max(contextLength / 1024, 4);
        var estimatedParams = ctxK switch
        {
            >= 256 => 110.0,
            >= 128 => 70.0,
            >= 64 => 32.0,
            >= 32 => 14.0,
            >= 16 => 7.0,
            _ => 3.0
        };
        return (long)(estimatedParams * 0.6 * 1024 * 1024 * 1024);
    }

    private static string EstimateParameterSize(int contextLength)
    {
        var ctxK = Math.Max(contextLength / 1024, 4);
        return ctxK switch
        {
            >= 256 => "110B",
            >= 128 => "70B",
            >= 64 => "32B",
            >= 32 => "14B",
            >= 16 => "7B",
            _ => "3B"
        };
    }

    private static string ComputeDigest(string modelId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(modelId));
        return Convert.ToHexStringLower(bytes);
    }
}
