using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Base parts for the blend stress run (<see cref="BlendStress"/>): prisms of many outlines, drafted and not, and blocks with a boss, a pocket or a hole.</summary>
static partial class Demos
{
    public static IEnumerable<(string name, Func<CadDocument> build)> StressShapes => new (string, Func<CadDocument>)[]
    {
        ("block", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 20), (-30, 20) })),
        ("block drafted", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 20), (-30, 20) }, draft: "5")),
        ("corner cut", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 5), (10, 20), (-30, 20) })),
        ("corner cut drafted", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 5), (10, 20), (-30, 20) }, draft: "5")),
        ("trapezoid", () => Prism(new[] { (-30.0, -20.0), (30, -20), (15, 20), (-15, 20) })),
        ("triangle", () => Prism(new[] { (-30.0, -20.0), (30, -20), (0, 25) })),
        ("L", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 0), (0, 0), (0, 20), (-30, 20) })),
        ("L drafted", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, 0), (0, 0), (0, 20), (-30, 20) }, draft: "5")),
        ("thin wedge", () => Prism(new[] { (-30.0, -20.0), (30, -20), (30, -14), (-30, 20) })),
        ("hexagon", Hexagon),
        ("block with boss", () => BlockWith("boss")),
        ("block with round boss", () => BlockWith("round boss")),
        ("block with boss flush to a side", () => BlockWith("flush boss")),
        ("block with drafted boss", () => BlockWith("drafted boss")),
        ("block with pocket", () => BlockWith("pocket")),
        ("block with round pocket", () => BlockWith("round pocket")),
        ("block with hole", () => BlockWith("hole")),
    };

    /// <summary>A prism: the outline (counter-clockwise) on Front, extruded 30 (drafted in by the angle given).</summary>
    static CadDocument Prism((double x, double y)[] outline, string? draft = null)
    {
        var d = Doc(("H", "30"));
        var s = new Sk(d);
        s.Poly(outline.Select(p => s.P(Num(p.x), Num(p.y))).ToArray());
        var sk = d.AddFeature(new SketchFeature { Name = "Outline", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Prism", SketchId = sk.Id, Depth = "H", Draft = draft });
        return d;
    }

    static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static CadDocument Hexagon()
    {
        var d = Doc(("H", "30"));
        var s = new Sk(d);
        s.Polygon(Sketch.OriginId, 6, "50");
        var sk = d.AddFeature(new SketchFeature { Name = "Outline", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Prism", SketchId = sk.Id, Depth = "H" });
        return d;
    }

    /// <summary>A block 60 × 40 × 15 with something on or in its top: a boss, a pocket or a hole.</summary>
    static CadDocument BlockWith(string what)
    {
        var d = Doc(("T", "15"));
        var b = new Sk(d);
        b.Rect("-30", "-20", "30", "20");
        var bs = d.AddFeature(new SketchFeature { Name = "Base", Plane = PlaneRef.Front, Sketch = b.S });
        d.AddFeature(new ExtrudeFeature { Name = "Base", SketchId = bs.Id, Depth = "T" });
        var top = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Front, Offset = "T" });
        var s = new Sk(d);
        switch (what)
        {
            case "round boss": case "round pocket": case "hole": s.Circle(s.P("5", "0"), what == "hole" ? "16" : "20"); break;
            case "flush boss": s.Rect("0", "-20", "30", "5"); break;
            default: s.Rect("-15", "-10", "15", "10"); break;
        }
        var sk = d.AddFeature(new SketchFeature { Name = "Top", Plane = new PlaneRef { Feature = top.Id }, Sketch = s.S });
        if (what.Contains("pocket")) d.AddFeature(new ExtrudeFeature { Name = "Pocket", SketchId = sk.Id, IsCut = true, Depth = "8", Reverse = true });
        else if (what == "hole") d.AddFeature(new ExtrudeFeature { Name = "Hole", SketchId = sk.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        else d.AddFeature(new ExtrudeFeature { Name = "Boss", SketchId = sk.Id, Depth = "12", Draft = what.StartsWith("drafted") ? "6" : null });
        return d;
    }
}
