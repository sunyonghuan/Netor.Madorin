using System.Reflection;

namespace Netor.Cortana.Plugin.Native.Debugger.Discovery;

/// <summary>
/// 工具元数据
/// </summary>
public record ToolMetadata(
    string ToolName,
    Type DeclaringType,
    MethodInfo MethodInfo,
    ParameterInfo[] Parameters
);
