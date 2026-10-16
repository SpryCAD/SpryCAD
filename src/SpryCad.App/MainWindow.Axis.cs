using Avalonia.Controls;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;

namespace SpryCad.App;

/// <summary>
/// The reference axis, as SolidWorks': one or two picks, its type following from them (along a line, edge or round
/// face; where two planes meet; through two points; square to a plane through a point).
/// </summary>
public partial class MainWindow
{
    void AxisCommand()
    {
        var f = new AxisFeature();
        foreach (var s in Selection.Items.Take(2))
            if (AxisRefOf(s) is { } r) f.Refs.Add(r);
        OpenAxisPage(f, true);
    }

    /// <summary>A pick as an axis reference: a plane or flat face, a line (straight edge, sketch line, round face or circular edge's axis, another axis) or a point.</summary>
    AxisReference? AxisRefOf(SelItem s) => s.Kind switch
    {
        SelKind.Plane or SelKind.Sketch => new AxisReference { Plane = s.ToPlaneRef() },
        SelKind.Face when s.Planar => new AxisReference { Plane = s.ToPlaneRef() },
        SelKind.Axis => new AxisReference { Line = s.ToDirRef() },
        SelKind.Edge when s.EdgeLinear => new AxisReference { Line = s.ToDirRef() },
        SelKind.SketchSegment when Session.Doc.Find(s.FeatureId!) is SketchFeature sf && sf.Sketch.Entity(s.SketchItem) is SkLine => new AxisReference { Line = s.ToDirRef() },
        SelKind.Vertex => new AxisReference { Point = s.ToPointRef() },
        _ => null,
    };

    SelItem? AxisRefItem(AxisReference r, ModelState st) =>
        r.Plane != null ? PlaneItem(r.Plane, st) : r.Line != null ? DirItem(r.Line, st) ?? (r.Line.Axis is { } a ? WorldAxisItem(a) : null) : r.Point != null ? PointItem(r.Point, st) : null;

    void OpenAxisPage(AxisFeature f, bool isNew)
    {
        var page = new PmPage("Axis", "axis");
        var st = StateForEdit(f, isNew);
        var type = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = TypeScale.Body, TextWrapping = TextWrapping.Wrap };
        var sec = page.Section("Selections");
        // (a round face or circular edge picked as its axis)
        var box = page.Selection(sec, "References",
            SelFilter.Plane | SelFilter.PlanarFace | SelFilter.Sketch | SelFilter.Edge | SelFilter.Axis | SelFilter.SketchSegment | SelFilter.Vertex | SelFilter.Center,
            "Line, edge or cylindrical face; two planes; two points; or a point and a plane.", 2,
            s => AxisRefOf(s) != null);
        foreach (var r in f.Refs) if (AxisRefItem(r, st) is { } it) box.Items.Add(it);
        sec.Children.Add(type);
        void Sync()
        {
            var t = AxisSolver.TypeOf(f.Refs);
            (type.Text, type.Foreground) = t != null ? ("✔ " + t, Palette.Success) : (f.Refs.Count == 0 ? "" : "Incomplete references", Palette.TextSecondary);
            type.IsVisible = f.Refs.Count > 0;
        }
        box.Changed += () =>
        {
            // the references from the picks, as they are now
            f.Refs = box.Items.Select(AxisRefOf).Where(r => r != null).Select(r => r!).ToList();
            Sync();
        };
        Sync();
        BeginEdit(f, isNew, page, overlay: false);
    }
}
