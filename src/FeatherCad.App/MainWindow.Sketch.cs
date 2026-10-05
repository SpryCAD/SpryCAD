using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.App.Session;
using FeatherCad.App.Sketcher;
using FeatherCad.App.UI;
using FeatherCad.Core;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.App;

public partial class MainWindow
{
    bool _sketchIsNew;
    string? _pendingAfterSketch;
    SketchTool? _pendingTool;
    HoleFeature? _pendingHole;

    void SetSketchTool(SketchTool t)
    {
        if (_sketch == null) { _pendingTool = t; StartSketchCommand(); return; }
        _sketch.SetTool(t);
        UpdateToolButtons();
        BuildSketchPage();
    }

    /// <summary>Sketch command: on the selected plane / planar face, or edit the selected sketch.</summary>
    void StartSketchCommand(string? pendingAfter = null)
    {
        if (_sketch != null) { ExitSketch(true); return; }
        if (_page != null && _edit != null) CancelEdit();
        var sel = Selection.First;
        if (sel?.Kind == SelKind.Sketch && pendingAfter == null) { EditFeature(sel.FeatureId!); return; }
        if (TryPlaneOf(sel, out var pr, out var frame))
        {
            _pendingAfterSketch = pendingAfter;
            StartSketch(pr!, frame);
            return;
        }
        // ask for a plane
        _pendingAfterSketch = pendingAfter;
        var page = new PmPage("Edit Sketch", "sketch");
        var s = page.Section("Message");
        page.Info(s, "Select a plane on which to create a sketch: one of the three default planes (in the graphics area or the design tree), a reference plane, or a planar face.");
        var box = page.Selection(s, "Plane", SelFilter.Plane | SelFilter.PlanarFace, "Plane or planar face", 1);
        box.Changed += () =>
        {
            var it = box.Items.FirstOrDefault();
            if (TryPlaneOf(it, out var p2, out var f2)) { ShowPage(null); StartSketch(p2!, f2); }
        };
        page.OnCancel = () => { _pendingAfterSketch = null; _pendingTool = null; ShowPage(null); };
        page.OnOk = page.OnCancel;
        _showPlanesTemp = true;
        ShowPage(page);
        Viewport.ShowPlanes = true;
        Viewport.Redraw();
        Status("Select a plane or planar face for the sketch.");
    }
    bool _showPlanesTemp;

    bool TryPlaneOf(SelItem? sel, out PlaneRef? pr, out Frame frame)
    {
        pr = null; frame = default;
        if (sel == null) return false;
        if (sel.Kind == SelKind.Plane)
        {
            pr = sel.ToPlaneRef();
            frame = sel.Frame ?? Resolver.StdPlane(sel.Name!);
            return true;
        }
        if (sel.Kind == SelKind.Face && sel.Planar && sel.FacePlane != null)
        {
            pr = sel.ToPlaneRef();
            frame = Frame.FromPlane(sel.FacePlane);
            return true;
        }
        return false;
    }

    void StartSketch(PlaneRef pr, Frame frame, bool pointsOnly = false)
    {
        var sf = new SketchFeature { Id = Session.Doc.NewId(), Plane = pr, Sketch = new Sketch() };
        EnterSketch(sf, frame, isNew: true, pointsOnly);
    }

    void EnterSketch(SketchFeature sf, Frame frame, bool isNew, bool pointsOnly = false)
    {
        Selection.Clear();
        _sketchIsNew = isNew;
        _sketch = new SketchEditor(Viewport, sf, frame, Session.Lookup) { PointsOnly = pointsOnly };
        _sketch.Overlay = Overlay;
        _sketch.Changed += OnSketchChanged;
        _sketch.SelectionChanged += BuildSketchPage;
        _sketch.Message += Status;
        _sketch.ToolChanged += () => { UpdateToolButtons(); };
        Viewport.Mode = _sketch;
        Viewport.Hover = null;
        ShowTab("Sketch");
        ShowConfirmCorner(() => ExitSketch(true), () => ExitSketch(false), "Exit Sketch", "Discard changes");
        BuildSketchPage();
        if (isNew) Viewport.NormalTo(frame);
        if (pointsOnly) _sketch.SetTool(SketchTool.Point);
        else if (_pendingTool != null) { _sketch.SetTool(_pendingTool.Value); _pendingTool = null; }
        else _sketch.SetTool(SketchTool.Select);
        UpdateToolButtons();
        RefreshScene();
        _sketch.UpdateOverlay(Viewport);
        OnSketchChanged();
    }

    void OnSketchChanged()
    {
        if (_sketch == null) return;
        StatusSketch.Text = _sketch.Status.Status switch
        {
            SketchStatus.FullyDefined => "Fully Defined",
            SketchStatus.OverDefined => "Over Defined",
            _ => "Under Defined",
        };
        StatusSketch.Foreground = _sketch.Status.Status switch
        {
            SketchStatus.FullyDefined => Brushes.Black,
            SketchStatus.OverDefined => Brushes.Red,
            _ => Brushes.Blue,
        };
        _sketch.UpdateOverlay(Viewport);
        if (_sketchStatusText != null) _sketchStatusText.Text = StatusSketch.Text;
    }

    TextBlock? _sketchStatusText;

    public void ExitSketch(bool save)
    {
        if (_sketch == null) return;
        var ed = _sketch;
        ed.DetachOverlay();
        _sketch = null;
        Viewport.Mode = _modelMode;
        Viewport.Cursor = null;
        HideConfirmCorner();
        ShowPage(null);
        StatusSketch.Text = "";
        _showPlanesTemp = false;
        var pending = _pendingAfterSketch; _pendingAfterSketch = null;
        var hole = _pendingHole; _pendingHole = null;
        var id = ed.Feature.Id;
        if (save && (ed.Modified || _sketchIsNew) && ed.Sk.Entities.Count > 0)
        {
            Session.Change(() =>
            {
                if (_sketchIsNew) Session.Doc.AddFeature(ed.Feature);
                else if (Session.Doc.Find(id) is SketchFeature existing) existing.Sketch = ed.Feature.Sketch;
            });
            Status($"{Session.Doc.Find(id)?.Name} saved.");
        }
        else if (save && _sketchIsNew) { Status("Empty sketch discarded."); pending = null; hole = null; }
        ShowTab(_activeTab);
        if (save && Session.Doc.Find(id) is SketchFeature sf)
        {
            Selection.Set(new SelItem { Kind = SelKind.Sketch, FeatureId = sf.Id, Frame = ed.Frame });
            if (hole != null)
            {
                hole.SketchId = sf.Id;
                OpenHolePage(hole, isNew: true);
            }
            else switch (pending)
            {
                case "extrude": OpenExtrudePage(new ExtrudeFeature { SketchId = sf.Id }, true); break;
                case "cut": OpenExtrudePage(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true }, true); break;
                case "revolve": OpenRevolvePage(new RevolveFeature { SketchId = sf.Id }, true); break;
                case "cutrevolve": OpenRevolvePage(new RevolveFeature { SketchId = sf.Id, IsCut = true }, true); break;
            }
        }
        RefreshScene();
    }

    // ------------------------------------------------------------------------------------------
    // sketch PropertyManager (relations / properties of the selection)

    void BuildSketchPage()
    {
        if (_sketch == null) return;
        var ed = _sketch;
        var page = new PmPage(ed.PointsOnly ? "Hole Positions" : "Sketch", "sketch") { OnOk = () => ExitSketch(true), OnCancel = () => ExitSketch(false) };
        if (ed.Selected.Count == 0)
        {
            var s = page.Section("Status");
            _sketchStatusText = new TextBlock { FontWeight = FontWeight.SemiBold };
            s.Children.Add(_sketchStatusText);
            page.Info(s, "Blue = under defined, black = fully defined, red = over defined.");
            if (ed.PointsOnly)
                page.Info(s, "Click on the face to place hole centers. Add dimensions (Smart Dimension) to locate them, then click OK.");
            var t = page.Section("Tools");
            page.Info(t, "L Line · R Rectangle · C Circle · A Arc · P Point · D Smart Dimension · T Trim · Esc Select. Drag points to move geometry. Double-click a dimension to change its value.");
            page.Number(t, "Polygon sides", () => ed.PolygonSides.ToString(), v => { if (int.TryParse(v, out var n)) ed.PolygonSides = Math.Clamp(n, 3, 64); }, "polygon", "");
            var m = page.Section("Model Edges");
            page.Info(m, "Select model edges or faces (in Select mode) and use Convert Entities to project them into the sketch.");
            page.ButtonRow(m, "Convert Entities", ConvertEntities, "convert");
        }
        else
        {
            var sel = page.Section("Selected Entities");
            foreach (var r in ed.Selected.Take(12)) sel.Children.Add(new TextBlock { Text = Describe(ed, r), FontSize = 12 });
            if (ed.Selected.Count > 12) sel.Children.Add(new TextBlock { Text = $"… {ed.Selected.Count - 12} more" });

            var rels = ed.ApplicableRelations();
            if (rels.Count > 0)
            {
                var add = page.Section("Add Relations");
                var wrap = new WrapPanel();
                foreach (var rel in rels)
                {
                    var b = new Button { Margin = new Thickness(2), Padding = new Thickness(6, 2), Content = $"{SketchEditor.Glyph(rel)}  {SketchEditor.RelationName(rel)}" };
                    var rr = rel;
                    b.Click += (_, _) =>
                    {
                        var err = ed.AddRelation(rr);
                        Status(err ?? $"{SketchEditor.RelationName(rr)} relation added.");
                        BuildSketchPage();
                    };
                    wrap.Children.Add(b);
                }
                add.Children.Add(wrap);
            }

            var existing = ed.RelationsOfSelection().ToList();
            var pts = ed.SelectedPoints.Concat(ed.SelectedEntities.SelectMany(e => e.PointIds)).Distinct().ToList();
            var fixedPts = pts.Where(p => ed.Sk.Point(p)?.Fixed == true && p != Sketch.OriginId).ToList();
            if (existing.Count > 0 || fixedPts.Count > 0)
            {
                var ex = page.Section("Existing Relations");
                foreach (var c in existing)
                {
                    var row = new DockPanel();
                    var del = new Button { Content = "✖", Padding = new Thickness(4, 0), Classes = { "tb" } };
                    ToolTip.SetTip(del, "Delete relation");
                    var cid = c.Id;
                    del.Click += (_, _) => { ed.Selected.Clear(); ed.Selected.Add(new SkRef(SkRefKind.Constraint, cid)); ed.DeleteSelection(); BuildSketchPage(); };
                    DockPanel.SetDock(del, Dock.Right);
                    row.Children.Add(del);
                    row.Children.Add(new TextBlock { Text = $"{SketchEditor.Glyph(c.Type)}  {SketchEditor.RelationName(c.Type)}", VerticalAlignment = VerticalAlignment.Center });
                    ex.Children.Add(row);
                }
                foreach (var p in fixedPts)
                {
                    var row = new DockPanel();
                    var del = new Button { Content = "✖", Padding = new Thickness(4, 0), Classes = { "tb" } };
                    var pid = p;
                    del.Click += (_, _) => { if (ed.Sk.Point(pid) is { } pp) { pp.Fixed = false; ed.Resolve(); Viewport.Redraw(); BuildSketchPage(); } };
                    DockPanel.SetDock(del, Dock.Right);
                    row.Children.Add(del);
                    row.Children.Add(new TextBlock { Text = "⚓  Fixed", VerticalAlignment = VerticalAlignment.Center });
                    ex.Children.Add(row);
                }
            }

            var opt = page.Section("Options");
            if (ed.SelectedEntities.Any())
            {
                var first = ed.SelectedEntities.First();
                page.Check(opt, "For construction", () => first.Construction, v => { ed.SetConstruction(v); });
            }
            var fixRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var fixB = new Button { Content = "⚓ Fix" }; fixB.Click += (_, _) => { ed.FixSelection(true); BuildSketchPage(); };
            var flB = new Button { Content = "Float" }; flB.Click += (_, _) => { ed.FixSelection(false); BuildSketchPage(); };
            fixRow.Children.Add(fixB); fixRow.Children.Add(flB);
            opt.Children.Add(fixRow);

            // parameters of a single point / dimension
            if (ed.Selected.Count == 1)
            {
                var r = ed.Selected[0];
                int? pid = r.Kind == SkRefKind.Point ? r.Id : r.Kind == SkRefKind.Entity && ed.Sk.Entity(r.Id) is SkPointEntity pe ? pe.P : null;
                if (pid is int id && id != Sketch.OriginId && ed.Sk.Point(id) is { } pt)
                {
                    var par = page.Section("Parameters");
                    page.Number(par, "X", () => Expr.Format(pt.X), v => { if (Expr.TryEval(v, Session.Lookup, out var x, out _) && Math.Abs(x - pt.X) > 1e-9) ed.SetPointPosition(id, new Vec2(x, pt.Y)); });
                    page.Number(par, "Y", () => Expr.Format(pt.Y), v => { if (Expr.TryEval(v, Session.Lookup, out var y, out _) && Math.Abs(y - pt.Y) > 1e-9) ed.SetPointPosition(id, new Vec2(pt.X, y)); });
                }
                if (r.Kind == SkRefKind.Constraint && ed.Sk.Constraint(r.Id) is { IsDimension: true } dim)
                {
                    var par = page.Section("Dimension");
                    page.Number(par, "Value", () => dim.Expr ?? Expr.Format(dim.Value), v => { }, null, dim.Type == CType.Angle ? "°" : "mm");
                    page.ButtonRow(par, "Edit value…", () => ed.OpenModifyBox(dim));
                    page.Check(par, "Driven (reference)", () => dim.Driven, v => { dim.Driven = v; ed.Resolve(); Viewport.Redraw(); });
                }
            }
            page.ButtonRow(opt, "Delete", () => { ed.DeleteSelection(); BuildSketchPage(); });
        }
        ShowPage(page);
        OnSketchChanged();
    }

    static string Describe(SketchEditor ed, SkRef r)
    {
        switch (r.Kind)
        {
            case SkRefKind.Point: return r.Id == Sketch.OriginId ? "Origin" : $"Point{r.Id}";
            case SkRefKind.Constraint:
                {
                    var c = ed.Sk.Constraint(r.Id);
                    return c == null ? "" : c.IsDimension ? $"{c.Name ?? "Dimension"} = {Expr.Format(c.Value)}" : SketchEditor.RelationName(c.Type);
                }
            default:
                return ed.Sk.Entity(r.Id) switch
                {
                    SkLine l => (l.Construction ? "Centerline" : "Line") + r.Id,
                    SkCircle => "Circle" + r.Id,
                    SkArc => "Arc" + r.Id,
                    SkPointEntity => "Point" + r.Id,
                    _ => "Entity" + r.Id,
                };
        }
    }

    void ConvertEntities()
    {
        if (_sketch == null) { Status("Convert Entities works inside a sketch."); return; }
        var edges = new List<TopoEdge>();
        foreach (var s in Selection.Items)
        {
            var body = Viewport.Bodies.FirstOrDefault(b => b.Id == s.BodyId);
            if (body == null) continue;
            var topo = body.Solid.Topology;
            if (s.Kind == SelKind.Edge && s.EdgeIndex >= 0 && s.EdgeIndex < topo.Edges.Count) edges.Add(topo.Edges[s.EdgeIndex]);
            else if (s.Kind == SelKind.Face) edges.AddRange(topo.Edges.Where(e => e.FaceA == s.Name || e.FaceB == s.Name));
        }
        if (edges.Count == 0) { Status("Select model edges or faces first (click them in the graphics area)."); return; }
        _sketch.ConvertEdges(edges.Distinct(), Viewport.Bodies);
        Selection.Clear();
    }

    void ToggleConstruction()
    {
        if (_sketch == null) return;
        var first = _sketch.SelectedEntities.FirstOrDefault();
        if (first == null) { Status("Select sketch entities to toggle construction geometry."); return; }
        _sketch.SetConstruction(!first.Construction);
        BuildSketchPage();
    }

    // ------------------------------------------------------------------------------------------
    // confirmation corner

    void ShowConfirmCorner(Action ok, Action cancel, string okTip, string cancelTip)
    {
        ConfirmBar.Children.Clear();
        var okB = new Button { Classes = { "tb" }, Content = Icons.Make("exitsketch", 26) };
        ToolTip.SetTip(okB, okTip);
        okB.Click += (_, _) => ok();
        var cb = new Button { Classes = { "tb" }, Content = Icons.Make("cancel", 22, new SolidColorBrush(Color.FromRgb(200, 40, 40))) };
        ToolTip.SetTip(cb, cancelTip);
        cb.Click += (_, _) => cancel();
        ConfirmBar.Children.Add(okB);
        ConfirmBar.Children.Add(cb);
        ConfirmCorner.IsVisible = true;
    }

    void HideConfirmCorner() => ConfirmCorner.IsVisible = false;
}
