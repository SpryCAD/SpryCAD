using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.App;

/// <summary>
/// The Measure tool (as SolidWorks'): pick faces, edges and vertices. One item: its size (length, radius, area,
/// position). Two: the minimum distance between them (drawn), the distance between centers, the normal distance
/// of parallel faces or lines, and the angle. More: total length and area. Distances are found on the display
/// mesh and then refined on the exact curves and surfaces.
/// </summary>
public partial class MainWindow
{
    PmPage? _measurePage;
    PmSelectionBox? _measureBox;
    StackPanel? _measureResults;
    Border? _measureLabel;
    Vec3? _measureLabelAt;
    DocumentSession? _measureSession;
    string _measureText = "";

    void MeasureCommand()
    {
        if (_page == _measurePage && _measurePage != null) { _measurePage.Activate(_measureBox); return; }
        if (_sketch != null || _edit != null) { Status("Finish the current edit before measuring."); return; }
        var page = new PmPage("Measure", "measure");
        page.OnOk = CloseMeasure;
        page.OnCancel = CloseMeasure;
        var s = page.Section("Selections");
        var box = page.Selection(s, "Items", SelFilter.Face | SelFilter.Edge | SelFilter.Vertex | SelFilter.Center,
            "Click faces, edges, vertices or the centers of circles and arcs: one for its size, two for the distance and angle between them. Click an item again to remove it.");
        page.ButtonRow(s, "Clear Selections", () => box.Clear());
        var r = page.Section("Results");
        _measureResults = new StackPanel { Spacing = 2 };
        r.Children.Add(_measureResults);
        page.ButtonRow(r, "Copy Results", () => { if (_measureText.Length > 0) _ = Clipboard?.SetTextAsync(_measureText); });
        // what is selected already is measured
        foreach (var it in Selection.Items.Where(i => i.Kind is SelKind.Face or SelKind.Edge or SelKind.Vertex).ToList()) box.Add(it);
        Selection.Clear();
        _measurePage = page; _measureBox = box;
        box.Changed += UpdateMeasure;
        _measureSession = Session;
        Session.Rebuilt += UpdateMeasure;
        Viewport.CameraChanged += PlaceMeasureLabel;
        ShowPage(page);
        UpdateMeasure();
    }

    void CloseMeasure() { if (_page == _measurePage) ShowPage(null); }

    /// <summary>The Measure page went away (closed, or another page took its place): its drawing goes too.</summary>
    void EndMeasure()
    {
        if (_measureSession != null) _measureSession.Rebuilt -= UpdateMeasure;
        Viewport.CameraChanged -= PlaceMeasureLabel;
        if (_measureLabel != null) Overlay.Children.Remove(_measureLabel);
        Viewport.MeasureLines = Array.Empty<(Vec3, Vec3, ViewportControl.MeasureStyle)>();
        _measurePage = null; _measureBox = null; _measureResults = null; _measureLabel = null; _measureLabelAt = null;
        _measureSession = null; _measureText = "";
    }

    // ---- what a picked item is ----

    /// <summary>A measured item: its mesh shape (for distances) and exact geometry where known.</summary>
    sealed class MeasureItem
    {
        public required SelItem Item;
        public required MeasureShape Shape;
        public string Kind = "";            // Vertex, Line, Circle, Arc, Curve, Plane, Cylinder, Cone, Sphere, Torus, Surface
        public Vec3? Center;                // circle / sphere center, vertex position
        public Vec3? Axis;                  // cylinder / cone axis (through Center), circle axis
        public Vec3? Dir;                   // line direction
        public Plane? Plane;                // planar face
        public double? Radius;              // torus: its centerline radius
        public double? TubeRadius;          // torus minor radius
        public double Length, Area;
        public double? ConeAngle;           // half angle, degrees
        public Func<Vec3, Vec3>? Project;   // closest point on the exact curve / surface (curved items)
        public double Size;                 // its extent: how far a refinement may move a point
        public bool IsEdge => Item.Kind == SelKind.Edge;
        public bool IsFace => Item.Kind == SelKind.Face;
    }

    MeasureItem? MeasureItemOf(SelItem s, ModelState? st)
    {
        var shape = new MeasureShape();
        if (s.Kind == SelKind.Vertex)
        {
            shape.Points.Add(s.Point);
            return new MeasureItem { Item = s, Shape = shape, Kind = s.IsCenter ? "Center" : "Vertex", Center = s.Point, Radius = s.IsCenter ? s.CenterRadius : null };
        }
        var body = Viewport.Bodies.FirstOrDefault(b => b.Id == s.BodyId);
        if (body == null) return null;
        var m = body.Mesh;
        Vec3 P(int i) => new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]);
        if (s.Kind == SelKind.Face)
        {
            var it = new MeasureItem { Item = s, Shape = shape };
            SurfaceDef? def = null; Plane? plane = null;
            foreach (var fr in m.Faces.Where(f => f.Name == s.Name))
            {
                def ??= fr.Surface; plane ??= fr.FirstPlane;
                for (int i = fr.IndexStart; i < fr.IndexStart + fr.IndexCount; i += 3)
                {
                    var (a, b, c) = (P(m.Indices[i]), P(m.Indices[i + 1]), P(m.Indices[i + 2]));
                    shape.Triangles.Add((a, b, c));
                    it.Area += Vec3.Cross(b - a, c - a).Length / 2;
                }
            }
            if (def == null || shape.IsEmpty) return null;
            switch (def.Kind)
            {
                case SurfaceKind.Plane: it.Kind = "Plane"; it.Plane = plane; break;
                case SurfaceKind.Cylinder: it.Kind = "Cylinder"; it.Center = def.Origin; it.Axis = def.Axis; it.Radius = def.Radius; break;
                case SurfaceKind.Cone: it.Kind = "Cone"; it.Center = def.Origin; it.Axis = def.Axis; it.ConeAngle = def.Param2 * 180 / Math.PI; break;
                case SurfaceKind.Sphere: it.Kind = "Sphere"; it.Center = def.Origin; it.Radius = def.Radius; break;
                case SurfaceKind.Torus: it.Kind = "Torus"; it.Center = def.Origin; it.Axis = def.Axis; it.Radius = def.Radius; it.TubeRadius = def.Param2; break;
                default: it.Kind = "Surface"; break;
            }
            if (def.IsCurved) { var es = new ExactSurface(def); it.Project = es.Project; }
            it.Size = Extent(shape);
            return it;
        }
        if (s.Kind == SelKind.Edge)
        {
            var it = new MeasureItem { Item = s, Shape = shape };
            foreach (var er in m.Edges.Where(e => e.EdgeIndex == s.EdgeIndex))
                for (int i = er.VertexStart; i + 1 < er.VertexStart + er.VertexCount; i += 2)
                {
                    Vec3 E(int k) => new(m.EdgeLines[3 * k], m.EdgeLines[3 * k + 1], m.EdgeLines[3 * k + 2]);
                    shape.Segments.Add((E(i), E(i + 1)));
                }
            if (shape.IsEmpty) return null;
            NEdge? ne = null;
            if (s.Edge != null && st != null)
                try { ne = Resolver.ResolveNativeEdge(s.Edge, st)?.edge; } catch { /* not resolvable: mesh only */ }
            if (ne != null)
            {
                it.Length = ne.Length;
                switch (ne.Curve)
                {
                    case NLine l: it.Kind = "Line"; it.Dir = l.D; break;
                    case NCircle c: it.Kind = ne.Closed ? "Circle" : "Arc"; it.Center = c.C; it.Axis = c.Axis.Normalized; it.Radius = c.R; break;
                    default: it.Kind = "Curve"; break;
                }
                var curve = ne.Curve;
                if (it.Kind != "Line") it.Project = p => curve.Eval(curve.Param(p));
            }
            else
            {
                var te = s.EdgeIndex >= 0 && s.EdgeIndex < body.Solid.Topology.Edges.Count ? body.Solid.Topology.Edges[s.EdgeIndex] : null;
                it.Length = shape.Segments.Sum(x => Vec3.Distance(x.A, x.B));
                if (te is { IsLinear: true }) { it.Kind = "Line"; it.Dir = (te.End - te.Start).Normalized; }
                else it.Kind = "Curve";
            }
            it.Size = Extent(shape);
            return it;
        }
        return null;
    }

    static double Extent(MeasureShape s)
    {
        var b = Box3.Empty;
        foreach (var p in s.Points) b.Add(p);
        foreach (var (p, q) in s.Segments) { b.Add(p); b.Add(q); }
        foreach (var (p, q, r) in s.Triangles) { b.Add(p); b.Add(q); b.Add(r); }
        return b.IsEmpty ? 0 : b.Diagonal;
    }

    // ---- measuring ----

    void UpdateMeasure()
    {
        if (_measureBox == null || _measureResults == null) return;
        var st = Session.DisplayResult?.State;
        var items = _measureBox.Items.Select(i => MeasureItemOf(i, st)).ToList();
        var rows = new List<(string label, string value)>();
        var lines = new List<(Vec3, Vec3, ViewportControl.MeasureStyle)>();
        _measureLabelAt = null;
        string? labelText = null;
        var u = UnitLabel;
        string L(double v) => F(v) + " " + u;

        if (items.Count == 0) rows.Add(("", "Select faces, edges or vertices."));
        else if (items.Any(i => i == null)) rows.Add(("", "An item could not be measured (it is no longer in the model)."));
        else if (items.Count == 1)
        {
            Single(items[0]!, rows, L);
            // a circle, arc, cylinder or sphere: its radius drawn from the center to where it was picked
            if (RadiusLine(items[0]!) is { } rl)
            {
                lines.Add((rl.center, rl.onIt, ViewportControl.MeasureStyle.Radius));
                _measureLabelAt = (rl.center + rl.onIt) * 0.5;
                var it = items[0]!;
                labelText = "R " + L(it.Radius!.Value) + (it.Kind is "Circle" or "Cylinder" ? $"  (Ø {F(2 * it.Radius.Value)})" : "");
            }
            else if (items[0] is { Kind: "Torus", Radius: { } R, TubeRadius: { } r })
            {
                _measureLabelAt = items[0]!.Item.Point;
                labelText = $"Inner R {L(Math.Abs(R - r))}  (Ø {F(2 * Math.Abs(R - r))})\nOuter R {L(R + r)}  (Ø {F(2 * (R + r))})";
            }
        }
        else if (items.Count == 2)
        {
            var (a, b) = (items[0]!, items[1]!);
            var (d, pa, pb) = MinDistance(a, b);
            // two round items (circles, arcs, cylinders, spheres, centers): between centers is what is wanted, and
            // is drawn; the minimum distance is listed after it
            var cd = CenterDistance(a, b);
            var centersFirst = cd != null && IsRound(a) && IsRound(b);
            void Centers()
            {
                if (cd is not { } c) return;
                rows.Add(("Center distance", L(c.d)));
                if (c.d <= 1e-9) return;
                lines.Add((c.p, c.q, centersFirst ? ViewportControl.MeasureStyle.Distance : ViewportControl.MeasureStyle.Dashed));
                if (centersFirst)
                {
                    _measureLabelAt = (c.p + c.q) * 0.5; labelText = L(c.d);
                    var cv = c.q - c.p;
                    rows.Add(("dX / dY / dZ", $"{F(Math.Abs(cv.X))}, {F(Math.Abs(cv.Y))}, {F(Math.Abs(cv.Z))} {u}"));
                }
            }
            if (centersFirst) Centers();
            if (!double.IsNaN(d))
            {
                rows.Add((centersFirst ? "Minimum distance" : "Distance", L(d)));
                if (!centersFirst)
                {
                    var dv = pb - pa;
                    rows.Add(("dX / dY / dZ", $"{F(Math.Abs(dv.X))}, {F(Math.Abs(dv.Y))}, {F(Math.Abs(dv.Z))} {u}"));
                    if (d > 1e-9) { lines.Add((pa, pb, ViewportControl.MeasureStyle.Distance)); _measureLabelAt = (pa + pb) * 0.5; labelText = L(d); }
                }
            }
            if (NormalDistance(a, b) is { } nd) rows.Add(("Normal distance", L(nd)));
            if (!centersFirst) Centers();
            if (Angle(a, b) is { } ang) rows.Add(("Angle", F(ang) + "°"));
            if (a.IsEdge && b.IsEdge) rows.Add(("Total length", L(a.Length + b.Length)));
            if (a.IsFace && b.IsFace) rows.Add(("Total area", F(a.Area + b.Area) + " " + u + "²"));
        }
        else
        {
            var edges = items.Where(i => i!.IsEdge).ToList();
            var faces = items.Where(i => i!.IsFace).ToList();
            rows.Add(("Items", items.Count.ToString(CultureInfo.InvariantCulture)));
            if (edges.Count > 0) rows.Add(($"Total length ({edges.Count} edges)", L(edges.Sum(e => e!.Length))));
            if (faces.Count > 0) rows.Add(($"Total area ({faces.Count} faces)", F(faces.Sum(f => f!.Area)) + " " + u + "²"));
            rows.Add(("", "Select two items for the distance between them."));
        }

        _measureResults.Children.Clear();
        foreach (var (label, value) in rows)
        {
            if (label.Length == 0)
            {
                _measureResults.Children.Add(new TextBlock { Text = value, FontSize = 11, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
                continue;
            }
            var row = new DockPanel();
            var lb = new TextBlock { Text = label, Width = 110, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(lb, Dock.Left);
            row.Children.Add(lb);
            row.Children.Add(new SelectableTextBlock { Text = value, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            _measureResults.Children.Add(row);
        }
        _measureText = string.Join(Environment.NewLine, rows.Where(r => r.label.Length > 0).Select(r => $"{r.label}: {r.value}"));
        // vertices are not highlighted like faces and edges: marked here (a line of no length is its end marks)
        foreach (var v in items.Where(i => i?.Kind is "Vertex" or "Center")) lines.Add((v!.Center!.Value, v.Center.Value, ViewportControl.MeasureStyle.Point));
        Viewport.MeasureLines = lines;

        if (labelText != null)
        {
            if (_measureLabel == null)
            {
                _measureLabel = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(204, 26, 140)), BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3), Padding = new Avalonia.Thickness(4, 1),
                    Child = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold },
                    IsHitTestVisible = false,
                };
                Overlay.Children.Add(_measureLabel);
            }
            ((TextBlock)_measureLabel.Child!).Text = labelText;
            _measureLabel.IsVisible = true;
        }
        else if (_measureLabel != null) _measureLabel.IsVisible = false;
        PlaceMeasureLabel();
    }

    void PlaceMeasureLabel()
    {
        if (_measureLabel == null || _measureLabelAt is not { } at) return;
        var s = Viewport.Camera.Project(at);
        _measureLabel.Measure(Avalonia.Size.Infinity);
        var sz = _measureLabel.DesiredSize;
        Canvas.SetLeft(_measureLabel, s.X - sz.Width / 2);
        Canvas.SetTop(_measureLabel, s.Y - sz.Height - 6);
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void Single(MeasureItem it, List<(string, string)> rows, Func<double, string> L)
    {
        var u = UnitLabel;
        string Pt(Vec3 p) => $"{F(p.X)}, {F(p.Y)}, {F(p.Z)} {u}";
        string RD(double r) => $"R {L(r)}  (Ø {F(2 * r)})";
        switch (it.Kind)
        {
            case "Vertex": rows.Add(("X, Y, Z", Pt(it.Center!.Value))); return;
            case "Center":
                rows.Add(("Type", "Center of a circle or arc"));
                rows.Add(("X, Y, Z", Pt(it.Center!.Value)));
                rows.Add(("Diameter", L(2 * it.Radius!.Value)));
                return;
            case "Line": rows.Add(("Length", L(it.Length))); break;
            case "Circle":
            case "Arc":
                rows.Add(("Diameter", L(2 * it.Radius!.Value)));
                rows.Add(("Radius", L(it.Radius.Value)));
                rows.Add(("Center", Pt(it.Center!.Value)));
                if (it.Kind == "Arc") rows.Add(("Arc length", L(it.Length)));
                else rows.Add(("Circumference", L(it.Length)));
                break;
            case "Curve": rows.Add(("Length", L(it.Length))); break;
            case "Plane":
                rows.Add(("Area", F(it.Area) + " " + u + "²"));
                var n = it.Plane!.Normal;
                rows.Add(("Normal", $"{F(n.X)}, {F(n.Y)}, {F(n.Z)}"));
                break;
            case "Cylinder":
                rows.Add(("Diameter", L(2 * it.Radius!.Value)));
                rows.Add(("Radius", L(it.Radius.Value)));
                rows.Add(("Area", F(it.Area) + " " + u + "²"));
                break;
            case "Sphere":
                rows.Add(("Radius", L(it.Radius!.Value)));
                rows.Add(("Center", Pt(it.Center!.Value)));
                rows.Add(("Area", F(it.Area) + " " + u + "²"));
                break;
            case "Torus":
                {
                    // a round tube swept around an axis (a bend): across the tube, and the inside and outside of the bend
                    double R = it.Radius!.Value, r = it.TubeRadius!.Value;
                    rows.Add(("Inner sweep", RD(Math.Abs(R - r))));
                    rows.Add(("Outer sweep", RD(R + r)));
                    rows.Add(("Centerline", RD(R)));
                    rows.Add(("Tube", RD(r)));
                    rows.Add(("Center", Pt(it.Center!.Value)));
                    rows.Add(("Area", F(it.Area) + " " + u + "²"));
                    break;
                }
            case "Cone":
                rows.Add(("Half angle", F(it.ConeAngle!.Value) + "°"));
                rows.Add(("Area", F(it.Area) + " " + u + "²"));
                break;
            default: rows.Add(("Area", F(it.Area) + " " + u + "²")); break;
        }
        rows.Insert(0, ("Type", it.Kind));
    }

    /// <summary>
    /// The minimum distance between two items: on the display mesh, then refined on the exact curves/surfaces
    /// (alternately projecting each closest point onto the other item, while it shortens the distance and the
    /// points stay close to where the mesh put them).
    /// </summary>
    static (double d, Vec3 a, Vec3 b) MinDistance(MeasureItem a, MeasureItem b)
    {
        var (d, pa, pb) = ClosestPoints.Between(a.Shape, b.Shape);
        if (double.IsNaN(d) || d < 1e-12 || (a.Project == null && b.Project == null)) return (d, pa, pb);
        Vec3 Closest(MeasureItem it, Vec3 p)
        {
            if (it.Project != null) return it.Project(p);
            var one = new MeasureShape(); one.Points.Add(p);
            return ClosestPoints.Between(it.Shape, one).OnA;
        }
        Vec3 a0 = pa, b0 = pb;
        double limA = Math.Max(1e-6, a.Size * 0.02), limB = Math.Max(1e-6, b.Size * 0.02);
        for (int i = 0; i < 12; i++)
        {
            var na = Closest(a, pb);
            var nb = Closest(b, na);
            if (Vec3.Distance(na, a0) > limA || Vec3.Distance(nb, b0) > limB) break; // left the face / edge
            var nd = Vec3.Distance(na, nb);
            if (nd >= d - 1e-12) break;
            (d, pa, pb) = (nd, na, nb);
        }
        return (d, pa, pb);
    }

    /// <summary>Parallel planar faces or lines: the distance between their planes / lines.</summary>
    static double? NormalDistance(MeasureItem a, MeasureItem b)
    {
        const double tol = 1e-6;
        if (a.Plane != null && b.Plane != null && Math.Abs(Math.Abs(Vec3.Dot(a.Plane.Normal, b.Plane.Normal)) - 1) < tol)
            return Math.Abs(a.Plane.Distance(b.Item.Point));
        if (a.Dir is { } da && b.Dir is { } db && Math.Abs(Math.Abs(Vec3.Dot(da, db)) - 1) < tol)
        {
            var pa = a.Shape.Segments[0].A; var pb = b.Shape.Segments[0].A;
            var v = pb - pa;
            return (v - da * Vec3.Dot(v, da)).Length;
        }
        if (a.Plane != null && b.Dir is { } d1 && Math.Abs(Vec3.Dot(a.Plane.Normal, d1)) < tol) return Math.Abs(a.Plane.Distance(b.Shape.Segments[0].A));
        if (b.Plane != null && a.Dir is { } d2 && Math.Abs(Vec3.Dot(b.Plane.Normal, d2)) < tol) return Math.Abs(b.Plane.Distance(a.Shape.Segments[0].A));
        return null;
    }

    /// <summary>Items with a center or an axis, measured between centers: circles, arcs, cylinders, spheres, centers.</summary>
    static bool IsRound(MeasureItem i) => i.Kind is "Circle" or "Arc" or "Cylinder" or "Cone" or "Sphere" or "Center";

    /// <summary>
    /// A circle's, arc's, cylinder's or sphere's radius as a line: from the center (on the axis, for a cylinder)
    /// to the item, toward where it was picked.
    /// </summary>
    static (Vec3 center, Vec3 onIt)? RadiusLine(MeasureItem it)
    {
        if (it.Radius is not { } r || it.Center is not { } c || it.Kind is not ("Circle" or "Arc" or "Cylinder" or "Sphere")) return null;
        var p = it.Item.Point;
        if (it.Axis is { } ax0)
        {
            var ax = ax0.Normalized;
            if (it.Kind == "Cylinder") c += ax * Vec3.Dot(p - c, ax); // the center level with the pick
            var v = p - c; v -= ax * Vec3.Dot(v, ax);
            var dir = v.Length > 1e-12 ? v.Normalized : ax.AnyPerpendicular();
            return (c, c + dir * r);
        }
        var w = p - c;
        return (c, c + (w.Length > 1e-12 ? w.Normalized : new Vec3(1, 0, 0)) * r);
    }

    /// <summary>
    /// Between centers (circle, arc and sphere centers, vertices) and axes (cylinders, cones): when at least one
    /// item has a center or axis. Two axes: only parallel ones (the distance between them).
    /// </summary>
    static (double d, Vec3 p, Vec3 q)? CenterDistance(MeasureItem a, MeasureItem b)
    {
        if (!IsRound(a) && !IsRound(b)) return null;
        bool IsAxis(MeasureItem i) => i.Kind is "Cylinder" or "Cone";
        Vec3? PointOf(MeasureItem i) => IsAxis(i) ? null : i.Center;
        if (a.Center == null || b.Center == null) return null;
        Vec3 OnAxis(MeasureItem axis, Vec3 p) { var o = axis.Center!.Value; var d = axis.Axis!.Value.Normalized; return o + d * Vec3.Dot(p - o, d); }
        if (PointOf(a) is { } ca && PointOf(b) is { } cb) return (Vec3.Distance(ca, cb), ca, cb);
        if (PointOf(a) is { } pa && IsAxis(b)) { var q = OnAxis(b, pa); return (Vec3.Distance(pa, q), pa, q); }
        if (PointOf(b) is { } pb && IsAxis(a)) { var q = OnAxis(a, pb); return (Vec3.Distance(q, pb), q, pb); }
        // two axes: parallel ones, at the middle of the first item
        var da = a.Axis!.Value.Normalized; var db = b.Axis!.Value.Normalized;
        if (Math.Abs(Math.Abs(Vec3.Dot(da, db)) - 1) > 1e-6) return null;
        var mid = OnAxis(a, a.Item.Point);
        var other = OnAxis(b, mid);
        return (Vec3.Distance(mid, other), mid, other);
    }

    /// <summary>
    /// The angle between two items with a direction (planar faces by their normals; lines, and cylinder, cone and
    /// circle axes): 0 to 90°.
    /// </summary>
    static double? Angle(MeasureItem a, MeasureItem b)
    {
        // (direction, is it a normal of a plane?)
        (Vec3 v, bool normal)? Dir(MeasureItem i) =>
            i.Plane != null ? (i.Plane.Normal, true)
            : i.Dir is { } d ? (d, false)
            : i.Kind is "Cylinder" or "Cone" ? (i.Axis!.Value, false)
            : i.Kind is "Circle" or "Arc" ? (i.Axis!.Value, true)
            : null;
        if (Dir(a) is not { } x || Dir(b) is not { } y) return null;
        var c = Math.Clamp(Math.Abs(Vec3.Dot(x.v.Normalized, y.v.Normalized)), 0, 1);
        var deg = Math.Acos(c) * 180 / Math.PI;
        // a line against a plane: the angle to the plane, not to its normal
        return x.normal == y.normal ? deg : 90 - deg;
    }
}
