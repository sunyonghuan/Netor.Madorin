using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record NextTurnSelection(
    int SelectionVersion,
    RuntimeMode Mode,
    DefaultSelection DefaultSelection,
    ModeOptions ModeOptions,
    string? SessionInstructions = null,
    string ToolCatalogVersion = "0");
