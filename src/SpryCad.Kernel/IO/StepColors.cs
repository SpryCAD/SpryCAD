namespace SpryCad.Kernel;

/// <summary>
/// The colors a STEP file gives its shapes (AP214/AP242 presentation): a STYLED_ITEM (or OVER_RIDING_STYLED_ITEM)
/// naming a shape and its styles, a SURFACE_STYLE_USAGE whose SURFACE_SIDE_STYLE fills with a COLOUR_RGB (or a
/// DRAUGHTING_PRE_DEFINED_COLOUR), optionally see-through (SURFACE_STYLE_TRANSPARENT). As r, g, b, opacity (0-1).
/// </summary>
public static class StepColors
{
    static readonly Dictionary<string, double[]> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new[] { 1.0, 0, 0 }, ["green"] = new[] { 0, 1.0, 0 }, ["blue"] = new[] { 0, 0, 1.0 }, ["yellow"] = new[] { 1.0, 1, 0 },
        ["magenta"] = new[] { 1.0, 0, 1 }, ["cyan"] = new[] { 0, 1.0, 1 }, ["black"] = new[] { 0.0, 0, 0 }, ["white"] = new[] { 1.0, 1, 1 },
    };

    /// <summary>Each styled shape's color, by its instance id (a solid, shell or face).</summary>
    public static Dictionary<int, double[]> Of(StepFile f)
    {
        var map = new Dictionary<int, double[]>();
        foreach (var si in f.OfType("STYLED_ITEM").Concat(f.OfType("OVER_RIDING_STYLED_ITEM")).Distinct())
        {
            var args = si.Part("STYLED_ITEM") ?? si.Part("OVER_RIDING_STYLED_ITEM") ?? si.Args;
            if (args.Count < 3 || args[2] is not StepRef item || args[1] is not List<object?> styles) continue;
            if (Color(f, styles) is { } c) map[item.Id] = c;
        }
        return map;
    }

    /// <summary>A solid's color: its own, else its shell's, else the color most of its faces have (null: none).</summary>
    public static double[]? OfSolid(StepFile f, StepInstance brep, Dictionary<int, double[]> map)
    {
        if (map.Count == 0) return null;
        if (map.TryGetValue(brep.Id, out var c)) return c;
        if (brep.Args.Count < 2 || brep.Args[1] is not StepRef shellRef) return null;
        if (map.TryGetValue(shellRef.Id, out c)) return c;
        if (!f.Instances.TryGetValue(shellRef.Id, out var shell) || shell.Args.Count < 2 || shell.Args[1] is not List<object?> faces) return null;
        return faces.OfType<StepRef>().Select(r => map.GetValueOrDefault(r.Id)).OfType<double[]>()
            .GroupBy(x => string.Join(",", x)).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
    }

    /// <summary>The color most solids of the file have (null: none colored): what a part imported from it is shown in.</summary>
    public static double[]? Dominant(StepFile f)
    {
        var map = Of(f);
        return f.OfType("MANIFOLD_SOLID_BREP").Concat(f.OfType("BREP_WITH_VOIDS")).Distinct()
            .Select(b => OfSolid(f, b, map)).OfType<double[]>()
            .GroupBy(x => string.Join(",", x)).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
    }

    // PRESENTATION_STYLE_ASSIGNMENT((SURFACE_STYLE_USAGE(side, SURFACE_SIDE_STYLE(name, (SURFACE_STYLE_FILL_AREA(FILL_AREA_STYLE(name,
    // (FILL_AREA_STYLE_COLOUR(name, COLOUR_RGB(name, r, g, b))))), SURFACE_STYLE_TRANSPARENT(t)))))
    static double[]? Color(StepFile f, List<object?> styles)
    {
        double[]? rgb = null; double opacity = 1;
        void Visit(object? a, int depth)
        {
            if (depth > 12) return;
            if (a is List<object?> list) { foreach (var x in list) Visit(x, depth + 1); return; }
            if (a is not StepRef r || !f.Instances.TryGetValue(r.Id, out var i)) return;
            if (i.Is("COLOUR_RGB") && i.Part("COLOUR_RGB") is { Count: >= 4 } c && c[1] is double cr && c[2] is double cg && c[3] is double cb)
            {
                rgb ??= new[] { Math.Clamp(cr, 0, 1), Math.Clamp(cg, 0, 1), Math.Clamp(cb, 0, 1) };
                return;
            }
            if (i.Is("DRAUGHTING_PRE_DEFINED_COLOUR") && i.Args.Count > 0 && i.Args[0] is string name && Named.TryGetValue(name, out var nc)) { rgb ??= nc; return; }
            if (i.Is("SURFACE_STYLE_TRANSPARENT") && i.Args.Count > 0 && i.Args[0] is double t) { opacity = Math.Clamp(1 - t, 0, 1); return; }
            // curve styles (edges' colors) are not the surfaces'
            if (i.Is("CURVE_STYLE")) return;
            foreach (var p in i.Parts) foreach (var x in p.Args) Visit(x, depth + 1);
        }
        foreach (var s in styles) Visit(s, 0);
        return rgb == null ? null : new[] { rgb[0], rgb[1], rgb[2], opacity };
    }
}
