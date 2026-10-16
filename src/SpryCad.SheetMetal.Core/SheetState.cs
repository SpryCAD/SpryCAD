namespace SpryCad.SheetMetal;

/// <summary>
/// A sheet metal body as a rebuild leaves it after a feature: its bend graph, the rules it was made with, and where its
/// flat coordinates are in the part (<see cref="World"/>: the fixed panel's place). Made again on every rebuild.
/// </summary>
public sealed record SheetState(string BodyId, BendGraph Graph, SheetRules Rules, Frame3 World)
{
    /// <summary>The bends' rules where a bend has its own overrides (by bend id); the others use <see cref="Rules"/>.</summary>
    public IReadOnlyDictionary<BendId, SheetRules> BendRules { get; init; } = new Dictionary<BendId, SheetRules>();

    public SheetRules RulesOf(BendId bend) => BendRules.TryGetValue(bend, out var r) ? r : Rules;
}
