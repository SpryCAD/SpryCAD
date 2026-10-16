using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// The reference plane, as SolidWorks': up to three references, each with the relations that suit what it is (a
/// face offers offset, parallel, perpendicular, coincident, angle and mid plane; a line coincident, perpendicular at
/// its end and parallel; a round face tangent; a point coincident). Picking chooses the usual relation, so a face
/// alone is an offset plane at once; the page says when the plane is fully defined.
/// </summary>
public partial class MainWindow
{
    static readonly string[] RefTitles = ["First Reference", "Second Reference", "Third Reference"];

    // what the active features use (sketches, reference planes and axes), worked out once per rebuild
    (object? result, int active, HashSet<string> used)? _usedMemo;

    HashSet<string> UsedByActive()
    {
        var doc = Session.Doc;
        if (_usedMemo is { } m && ReferenceEquals(m.result, Session.Result) && m.active == doc.ActiveCount) return m.used;
        var used = doc.UsedByActive();
        _usedMemo = (Session.Result, doc.ActiveCount, used);
        return used;
    }

    /// <summary>The origin planes, reference planes and axes a feature refers to (a sketch: the plane it is on).</summary>
    HashSet<string> RefersTo(Feature? f)
    {
        var set = new HashSet<string>();
        if (f == null) return set;
        if (f is SketchFeature s) { if (s.Plane.Std is { } std) set.Add(std); if (s.Plane.Feature is { } pf) set.Add(pf); return set; }
        // (a plane made from references: its Base, Front unless set, is not used, so not looked at)
        if (f is PlaneFeature { Mode: PlaneMode.References } byRefs) { var c = (PlaneFeature)byRefs.Clone(); c.Base = new PlaneRef(); f = c; }
        var json = CadDocument.JsonOf(f);
        foreach (var std in new[] { "Front", "Top", "Right" }) if (json.Contains($"\"{std}\"")) set.Add(std);
        foreach (var g in Session.Doc.Features)
            if (g is PlaneFeature or AxisFeature && g.Id != f.Id && CadDocument.Refers(json, g.Id)) set.Add(g.Id);
        return set;
    }

    /// <summary>
    /// The size a reference plane is drawn: around what is sketched on it (a little past it), so a plane with a small
    /// sketch is small; with nothing on it, a square of <paramref name="half"/> about its origin.
    /// </summary>
    (Vec2 lo, Vec2 hi) PlaneFit(string planeId, Frame fr, ModelState state, double half)
    {
        double lx = double.MaxValue, ly = double.MaxValue, hx = double.MinValue, hy = double.MinValue;
        foreach (var sf in Session.Doc.Features.Take(Session.Doc.ActiveCount).OfType<SketchFeature>())
        {
            if (sf.Plane.Feature != planeId || !state.Sketches.TryGetValue(sf.Id, out var sr)) continue;
            foreach (var (a, c) in SpryCad.App.Viewport.SketchDrawing.Segments(sr))
                foreach (var p in new[] { fr.ToLocal(a), fr.ToLocal(c) })
                { lx = Math.Min(lx, p.X); ly = Math.Min(ly, p.Y); hx = Math.Max(hx, p.X); hy = Math.Max(hy, p.Y); }
        }
        if (lx > hx) return (new Vec2(-half, -half), new Vec2(half, half));
        var more = Math.Max(Math.Max(hx - lx, hy - ly) * 0.15, half * 0.02);
        return (new Vec2(lx - more, ly - more), new Vec2(hx + more, hy + more));
    }

    void PlaneCommand()
    {
        var f = new PlaneFeature { Mode = PlaneMode.References }.Sized(Units);
        // what is selected becomes the references
        foreach (var s in Selection.Items.Take(3))
            if (PlaneRefOf(s) is { } r) { f.Refs.Add(r); PlaneSolver.Suggest(f.Refs, r); }
        OpenPlanePage(f, true);
    }

    /// <summary>A plane reference's distance to start with: round in the part's units.</summary>
    string DefaultOffset => FeatureDefaults.Length(Units, 10, 0.5);

    /// <summary>A pick as a plane reference: what it is decides what it offers.</summary>
    PlaneReference? PlaneRefOf(SelItem s)
    {
        var r = new PlaneReference { Hint = s.At ?? s.Point, Value = DefaultOffset };
        switch (s.Kind)
        {
            case SelKind.Plane or SelKind.Sketch:
            case SelKind.Face when s.Planar:
                r.Plane = s.ToPlaneRef();
                break;
            case SelKind.Face:
                {
                    var fr = s.ToFaceRef();
                    fr.Normal = default; // round: found by name, never as a flat face
                    r.Round = fr; r.Line = new DirRef { Face = fr.Clone() };
                    break;
                }
            case SelKind.Axis: r.Line = s.ToDirRef(); break;
            case SelKind.Edge:
                r.Curve = s.ToCurveRef();
                if (s.EdgeLinear) r.Line = s.ToDirRef();
                break;
            case SelKind.SketchSegment:
                r.Curve = s.ToCurveRef();
                if (Session.Doc.Find(s.FeatureId!) is SketchFeature sf && sf.Sketch.Entity(s.SketchItem) is SkLine) r.Line = s.ToDirRef();
                break;
            case SelKind.Vertex: r.Point = s.ToPointRef(); break;
            default: return null;
        }
        return PlaneSolver.KindOf(r) == PlaneSolver.RefKind.None ? null : r;
    }

    /// <summary>A reference shown in its box again (editing a plane).</summary>
    SelItem? PlaneRefItem(PlaneReference r, ModelState st)
    {
        if (r.Plane != null) return PlaneItem(r.Plane, st);
        if (r.Round != null) return FaceItem(r.Round);
        if (r.Curve?.Edge is { } e) return EdgeItem(e, st);
        if (r.Curve?.Sketch is { } cs) return new SelItem { Kind = SelKind.SketchSegment, FeatureId = cs, SketchItem = r.Curve.SketchEntity };
        if (r.Line != null) return DirItem(r.Line, st) ?? (r.Line.Axis is { } a ? WorldAxisItem(a) : null);
        if (r.Point != null) return PointItem(r.Point, st);
        return null;
    }

    static SelItem PointItem(PointRef p, ModelState st)
    {
        Vec3 at = default;
        try { at = Resolver.ResolvePoint(p, st); } catch (FeatureException) { }
        return p.Sketch != null
            ? new SelItem { Kind = SelKind.Vertex, FeatureId = p.Sketch, SketchItem = p.SketchPoint, Point = at }
            : p.Center is { } pc ? new SelItem { Kind = SelKind.Vertex, Edge = pc, BodyId = pc.Body, Point = at, IsCenter = true }
            : new SelItem { Kind = SelKind.Vertex, Edge = p.Vertex, BodyId = p.Vertex?.Body, Point = at };
    }

    /// <summary>A plane made before references (offset, through a point, at an angle, normal to a curve) as references.</summary>
    static void ToReferences(PlaneFeature f)
    {
        if (f.Mode == PlaneMode.References) return;
        var refs = new List<PlaneReference>();
        switch (f.Mode)
        {
            case PlaneMode.Offset:
                refs.Add(new() { Plane = f.Base, Relation = PlaneRelation.Offset, Value = f.Offset });
                break;
            case PlaneMode.ThroughPoint:
                refs.Add(new() { Plane = f.Base, Relation = PlaneRelation.Parallel });
                if (f.Point != null) refs.Add(new() { Point = f.Point });
                break;
            case PlaneMode.Angle:
                refs.Add(new() { Plane = f.Base, Relation = PlaneRelation.Angle, Value = f.Angle });
                if (f.Axis != null) refs.Add(new() { Line = f.Axis, Relation = PlaneRelation.Coincident });
                break;
            case PlaneMode.NormalToCurve:
                if (f.Curve != null) refs.Add(new() { Curve = f.Curve, Relation = PlaneRelation.Perpendicular, Hint = f.Curve.Edge?.Hint ?? default });
                if (f.Point != null) refs.Add(new() { Point = f.Point });
                break;
        }
        f.Mode = PlaneMode.References;
        f.Refs = refs;
    }

    static string RelationIcon(PlaneRelation r) => r switch
    {
        PlaneRelation.Coincident => "mate-coincident",
        PlaneRelation.Parallel => "mate-parallel",
        PlaneRelation.Perpendicular => "mate-perpendicular",
        PlaneRelation.Offset => "mate-distance",
        PlaneRelation.Angle => "mate-angle",
        PlaneRelation.MidPlane => "plane-mid",
        _ => "mate-tangent",
    };

    static string RelationName(PlaneRelation r) => r switch
    {
        PlaneRelation.Offset => "Offset distance",
        PlaneRelation.MidPlane => "Mid plane",
        _ => r.ToString(),
    };

    void OpenPlanePage(PlaneFeature f, bool isNew)
    {
        ToReferences(f);
        var page = new PmPage("Plane", "plane");
        var st = StateForEdit(f, isNew);
        var slots = new PlaneReference?[3];
        for (int i = 0; i < Math.Min(3, f.Refs.Count); i++) slots[i] = f.Refs[i];
        void Store() => f.Refs = slots.Where(s => s != null).Select(s => s!).ToList();

        // fully defined, or not
        var status = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = TypeScale.Body, TextWrapping = TextWrapping.Wrap };
        var statusBox = new Border { Classes = { "pmsection" }, Child = status };
        page.Content(statusBox);

        var boxes = new PmSelectionBox[3];
        var relLists = new StackPanel[3];
        var distRows = new Control[3];
        var angleRows = new Control[3];
        var distBoxes = new TextBox[3];
        var angleBoxes = new TextBox[3];
        var flips = new CheckBox[3];
        Action sync = () => { };

        for (int k = 0; k < 3; k++)
        {
            var i = k;
            var sec = page.Section(RefTitles[i]);
            var box = page.Selection(sec, RefTitles[i],
                SelFilter.Plane | SelFilter.Face | SelFilter.PlanarFace | SelFilter.Sketch | SelFilter.Edge | SelFilter.SketchSegment | SelFilter.Vertex | SelFilter.Center,
                i == 0 ? "Face, plane, edge, sketch line, point, or cylindrical face (its axis, or tangent to it)." : "", 1);
            if (slots[i] is { } r0 && PlaneRefItem(r0, st) is { } it) box.Items.Add(it);
            box.Changed += () =>
            {
                var item = box.Items.FirstOrDefault();
                slots[i] = item == null ? null : PlaneRefOf(item);
                if (item != null && slots[i] == null) { box.Items.Clear(); box.Refresh(); }
                if (slots[i] is { } added)
                {
                    var list = slots.Where(s => s != null).Select(s => s!).ToList();
                    PlaneSolver.Suggest(list, added);
                    // on to the next empty box, as SolidWorks
                    if (Enumerable.Range(0, 3).Select(j => (i + j) % 3).FirstOrDefault(j => slots[j] == null, -1) is var next and >= 0) page.Activate(boxes[next]);
                }
                Store(); sync();
            };
            boxes[i] = box;

            relLists[i] = new StackPanel { Spacing = 1 };
            sec.Children.Add(relLists[i]);
            distBoxes[i] = page.Number(sec, "Distance", () => slots[i]?.Value ?? DefaultOffset, v => { if (slots[i] is { } s) s.Value = v; }, "dimension");
            distRows[i] = PmPage.RowOf(distBoxes[i]);
            angleBoxes[i] = page.Number(sec, "Angle", () => slots[i]?.Value ?? "45", v => { if (slots[i] is { } s) s.Value = v; }, "mate-angle", "°");
            angleRows[i] = PmPage.RowOf(angleBoxes[i]);
            flips[i] = page.Check(sec, "Flip", () => slots[i]?.Flip ?? false, v => { if (slots[i] is { } s) s.Flip = v; });
        }

        var opts = page.Section("Options");
        page.Check(opts, "Flip normal", () => f.Flip, v => f.Flip = v);
        // its size: dragged by the handles on it, or around what is sketched on it
        page.ButtonRow(opts, "Size to Fit", () => { f.Size = null; page.RaiseChanged(); RefreshScene(); }, "fit");
        page.Info(opts, "Drag the handles to resize the plane. Size to Fit sizes it around its sketch geometry.");

        // the relations each reference offers, its value and its side
        sync = () =>
        {
            var refs = slots.Where(s => s != null).Select(s => s!).ToList();
            for (int i = 0; i < 3; i++)
            {
                var r = slots[i];
                relLists[i].Children.Clear();
                if (r != null)
                    foreach (var rel in PlaneSolver.Offered(r))
                    {
                        var b = new ToggleButton
                        {
                            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Make(RelationIcon(rel), 18), new TextBlock { Text = RelationName(rel), VerticalAlignment = VerticalAlignment.Center } } },
                            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(6, 3), MinHeight = 0, Classes = { "matetype" }, IsChecked = r.Relation == rel,
                        };
                        b.Click += (_, _) =>
                        {
                            var was = r.Relation;
                            r.Relation = rel;
                            // a distance and an angle are different numbers
                            if (rel == PlaneRelation.Angle && was != PlaneRelation.Angle) r.Value = "45";
                            if (rel == PlaneRelation.Offset && was == PlaneRelation.Angle) r.Value = DefaultOffset;
                            sync(); page.RaiseChanged();
                        };
                        relLists[i].Children.Add(b);
                    }
                var rl = r?.Relation;
                distRows[i].IsVisible = rl == PlaneRelation.Offset;
                angleRows[i].IsVisible = rl == PlaneRelation.Angle;
                if (r != null && rl == PlaneRelation.Offset && distBoxes[i].Text != r.Value) distBoxes[i].Text = r.Value;
                if (r != null && rl == PlaneRelation.Angle && angleBoxes[i].Text != r.Value) angleBoxes[i].Text = r.Value;
                flips[i].IsVisible = rl is PlaneRelation.Offset or PlaneRelation.Angle or PlaneRelation.Tangent;
                flips[i].Content = rl == PlaneRelation.Tangent ? "Other side" : rl == PlaneRelation.Angle ? "Flip angle" : "Flip offset";
                flips[i].IsChecked = r?.Flip ?? false;
            }
            // (with nothing picked the page's message says what to pick)
            statusBox.IsVisible = refs.Count > 0;
            (status.Text, status.Foreground) = refs.Count == 0 ? ("", Palette.TextMuted)
                : PlaneSolver.Defined(refs) switch
                {
                    PlaneSolver.Definition.Full => ("✔ Fully Defined", Palette.Success),
                    PlaneSolver.Definition.Under => ("Under Defined", Palette.TextSecondary),
                    _ => ("Over Defined", Palette.Danger),
                };
        };
        sync();
        // the first empty box takes the next pick
        if (slots.Select((s, j) => (s, j)).FirstOrDefault(x => x.s == null) is { s: null } empty) page.Activate(boxes[empty.j]);
        BeginEdit(f, isNew, page, overlay: false);
    }
}
