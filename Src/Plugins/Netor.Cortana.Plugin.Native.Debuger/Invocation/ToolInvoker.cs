using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Plugin.Native.Debugger.Discovery;

namespace Netor.Cortana.Plugin.Native.Debugger.Invocation;

/// <summary>
/// 工具调用器 - 负责实例化类、绑定参数、执行方法、返回结果
/// </summary>
public class ToolInvoker
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ToolRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;

    public ToolInvoker(IServiceProvider serviceProvider, ToolRegistry registry)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    /// <summary>
    /// 从字符串参数调用工具（自动检测格式：位置参数/命名参数/JSON）
    /// </summary>
    public async Task<string> InvokeAsync(string toolName, string? argsOrJson = null)
    {
        var toolMeta = GetTool(toolName);
        var args = ParameterBinder.Bind(toolMeta, argsOrJson);
        return await InvokeCore(toolMeta, args);
    }

    /// <summary>
    /// 从预绑定参数调用工具（供交互模式使用）
    /// </summary>
    public async Task<string> InvokeAsync(string toolName, object[] boundArgs)
    {
        var toolMeta = GetTool(toolName);
        return await InvokeCore(toolMeta, boundArgs);
    }

    private ToolMetadata GetTool(string toolName)
    {
        if (!_registry.Tools.TryGetValue(toolName, out var toolMeta))
            throw new KeyNotFoundException(
                $"未找到工具: {toolName}。可用工具: {string.Join(", ", _registry.Tools.Keys)}");
        return toolMeta;
    }

    private async Task<string> InvokeCore(ToolMetadata toolMeta, object[] args)
    {
        var instance = _serviceProvider.GetRequiredService(toolMeta.DeclaringType);
        var result = toolMeta.MethodInfo.Invoke(instance, args);

        if (result is Task task)
        {
            await task;
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var resultProperty = taskType.GetProperty("Result");
                result = resultProperty?.GetValue(task);
            }
            else
            {
                return JsonSerializer.Serialize(new { status = "completed" }, _jsonOptions);
            }
        }

        return JsonSerializer.Serialize(result, _jsonOptions);
    }
}