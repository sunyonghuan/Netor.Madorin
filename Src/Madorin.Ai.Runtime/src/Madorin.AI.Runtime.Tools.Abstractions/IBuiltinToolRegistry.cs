namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IBuiltinToolRegistry
{
    public string CatalogVersion { get; }

    public IReadOnlyList<ToolDescriptor> GetTools();

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds);
}
