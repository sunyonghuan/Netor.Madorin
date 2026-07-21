namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IBuiltinToolRegistry
{
    public IReadOnlyList<ToolDescriptor> GetTools();
}
