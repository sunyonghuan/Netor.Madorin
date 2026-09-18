using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 用 get_info 的 parameters 作为 JsonSchema 的 Native 工具，避免模型只看到 argsJson。
/// </summary>
internal sealed class NativePluginAIFunction : AIFunction
{
    private readonly IReadOnlyList<NativeToolParameter> _parameters;
    private readonly Func<string, CancellationToken, Task<string>> _invoke;

    public NativePluginAIFunction(
        string name,
        string description,
        JsonElement jsonSchema,
        IReadOnlyList<NativeToolParameter> parameters,
        Func<string, CancellationToken, Task<string>> invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(invoke);

        Name = name;
        Description = description ?? string.Empty;
        JsonSchema = jsonSchema;
        _parameters = parameters;
        _invoke = invoke;
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var json = NativeToolArgumentBinder.FromFunctionArguments(arguments, _parameters);
        return await _invoke(json, cancellationToken).ConfigureAwait(false);
    }
}
