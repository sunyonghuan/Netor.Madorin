namespace DesktopPet.Models.Live2D;

public sealed record Live2DModelInfo(
    string Name,
    string ModelJsonPath,
    string MocPath,
    IReadOnlyList<string> TexturePaths,
    int ParameterCount,
    int DrawableCount,
    int MouthParameterIndex);
