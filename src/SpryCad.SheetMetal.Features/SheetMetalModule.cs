using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.SheetMetal.Brep;

namespace SpryCad.SheetMetal.Features;

/// <summary>
/// The sheet metal module's entry points for the app: registering its features with SpryCad.Core
/// (<see cref="FeatureRegistry"/>), the rules a sheet gets from the shop profile in use, and new sheet bodies.
/// </summary>
public static class SheetMetalModule
{
    /// <summary>Registers the sheet metal features. Call once at startup, before any part is read.</summary>
    public static void Register()
    {
        FeatureRegistry.Register<BaseFlangeFeature>("baseFlange", SheetRebuild.BaseFlange, (f, d) => SheetProfiles.KeyOf(d, f.Body));
        FeatureRegistry.Register<EdgeFlangeFeature>("edgeFlange", SheetRebuild.EdgeFlange, (f, d) => SheetProfiles.KeyOf(d, f.Body));
        FeatureRegistry.Register<HemFeature>("hem", SheetRebuild.Hem, (f, d) => SheetProfiles.KeyOf(d, f.Body));
        FeatureRegistry.Register<SheetCutFeature>("sheetCut", SheetRebuild.Cut, (f, d) => SheetProfiles.KeyOf(d, f.Body));
    }

    /// <summary>
    /// The rules for <paramref name="material"/> and <paramref name="gauge"/> from the library set's shop profile (one
    /// profile for every part: the same material and gauge, the same rules), with the part's overrides.
    /// </summary>
    public static SheetRules RulesFor(LibrarySet set, string material, string gauge, IReadOnlyDictionary<string, string>? overrides = null) =>
        SheetRules.Resolve(set.Profile, material, gauge, overrides);

    /// <summary>A new sheet metal body in the part (its id "Sheet1", "Sheet2"…), of a material and gauge.</summary>
    public static SheetMetalBody AddBody(CadDocument doc, string material, string gauge)
    {
        var bodies = doc.SheetBodies ??= new();
        int n = 1;
        while (bodies.Any(b => b.Id == $"Sheet{n}")) n++;
        var b = new SheetMetalBody { Id = $"Sheet{n}", Name = $"Sheet{n}", Material = material, Gauge = gauge };
        bodies.Add(b);
        return b;
    }

    /// <summary>The sheet metal body a feature belongs to (null: not a sheet metal feature).</summary>
    public static string? BodyOf(Feature f) => f switch
    {
        BaseFlangeFeature b => b.Body,
        EdgeFlangeFeature e => e.Body,
        HemFeature h => h.Body,
        SheetCutFeature c => c.Body,
        _ => null,
    };

    /// <summary>The flat pattern of a sheet body as the rebuild left it (null: no such body).</summary>
    public static FlatPattern2D? FlatOf(ModelState state, string body) =>
        state.Sheets.TryGetValue(body, out var s) ? FlatPattern2D.From(s.Graph) : null;

    /// <summary>Text with the features' ids in entity ids ("Fx3k….0.B") said by the features' names.</summary>
    public static string Named(CadDocument doc, string text) => SheetRebuild.Named(doc, text);

    /// <summary>
    /// Sets (or, with <paramref name="treatment"/> and <paramref name="gap"/> both null, clears) a corner's override of
    /// the profile's corner rule (sheet metal spec section 7). An edit of the part: the caller makes it undoable.
    /// </summary>
    public static void SetCorner(CadDocument doc, CornerId corner, CornerTreatment? treatment, string? gap)
    {
        var list = doc.SheetOverrides ??= new();
        var o = list.FirstOrDefault(x => x.Target == corner.Value);
        if (o == null) { o = new SheetMetalOverride { Target = corner.Value }; list.Add(o); }
        o.Values.Remove("sm.corners/treatment");
        o.Values.Remove("sm.corners/gap");
        if (treatment is { } tr) o.Values["sm.corners/treatment"] = tr.ToString();
        if (!string.IsNullOrWhiteSpace(gap)) o.Values["sm.corners/gap"] = gap.Trim();
        if (o.Values.Count == 0) list.Remove(o);
        list.Sort((a, b) => string.CompareOrdinal(a.Target, b.Target));
        if (list.Count == 0) doc.SheetOverrides = null;
    }

    /// <summary>The sheet metal edge a face name names ("BaseFlange1.P0.Edge(2)"): for edge flanges (null: not one).</summary>
    public static bool IsPanelEdge(string faceName) => FoldedSolid.ParseEdge(faceName) != null;
}
