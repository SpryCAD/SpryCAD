using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

static partial class Demos
{
    /// <summary>
    /// A rocker sketched as one profile: a 60 x 8 rectangle with R6 circles over its ends and its middle (the
    /// outlines cross), extruded 10.
    /// </summary>
    public static CadDocument OverlappingProfile()
    {
        var d = Doc(("A", "30"), ("B", "4"), ("R", "6"), ("T", "10"));
        var s = new Sk(d);
        s.Rect("-A", "-B", "A", "B");
        s.Circle(s.P("-A", "0"), "2*R");
        s.Circle(s.P("A", "0"), "2*R");
        s.Circle(Sketch.OriginId, "2*R");
        var ss = d.AddFeature(new SketchFeature { Name = "Rocker", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Rocker", SketchId = ss.Id, Depth = "T" });
        return d;
    }
}
