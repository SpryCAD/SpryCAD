using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App;

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
        if (PartOnly("Sketching")) return;
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
        // a reference plane just made and not used yet: offered (OK sketches on it; clicking another plane takes that)
        if (UnusedLastPlane() is { } offered)
        {
            box.Items.Add(offered);
            box.Refresh();
            page.Info(s, $"{offered.Name} is not used yet: OK (Enter) sketches on it.");
            page.OnOk = () => { if (TryPlaneOf(box.Items.FirstOrDefault(), out var p3, out var f3)) { ShowPage(null); StartSketch(p3!, f3); } else page.OnCancel!(); };
        }
        ShowPage(page);
        _planePickPage = page; // the planes show while it is open (ShowPage restores them when it goes)
        Viewport.ShowPlanes = true;
        Viewport.Redraw();
        Status("Select a plane or planar face for the sketch.");
    }

    /// <summary>The last feature, when it is a reference plane nothing uses yet (made for the sketch that comes next), as a pick.</summary>
    SelItem? UnusedLastPlane()
    {
        var doc = Session.Doc;
        if (doc.Features.Take(doc.ActiveCount).LastOrDefault() is not PlaneFeature { Suppressed: false } pf || UsedByActive().Contains(pf.Id)) return null;
        if (Session.Result?.State.Planes.TryGetValue(pf.Id, out var fr) != true) return null;
        return new SelItem { Kind = SelKind.Plane, Name = pf.Name, FeatureId = pf.Id, Frame = fr };
    }

    // the "select a plane for the sketch" page, while open: it shows the planes, even when they are hidden
    PmPage? _planePickPage;

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
        _sketch = new SketchEditor(Viewport, sf, frame, Session.Lookup) { PointsOnly = pointsOnly, Units = Session.Doc.Units };
        _sketch.Overlay = Overlay;
        _sketch.Changed += OnSketchChanged;
        _sketch.SelectionChanged += BuildSketchPage;
        _sketch.Message += Status;
        // the panel shows tool settings (e.g. polygon sides) for the tool in use
        _sketch.ToolChanged += () => { UpdateToolButtons(); if (_sketch?.Selected.Count == 0) BuildSketchPage(); };
        Viewport.Mode = _sketch;
        Viewport.Hover = null;
        ShowTab("Sketch");
        ShowConfirmCorner(() => ExitSketch(true), DiscardSketch, "Exit Sketch", "Discard changes", okIcon: "exitsketch");
        BuildSketchPage();
        // a new sketch turns to its plane; an existing one also zooms to fit its geometry and its dimensions
        Viewport.NormalTo(frame, isNew ? null : SketchFit(sf.Sketch, frame));
        if (pointsOnly) _sketch.SetTool(SketchTool.Point);
        else if (_pendingTool != null) { _sketch.SetTool(_pendingTool.Value); _pendingTool = null; }
        else _sketch.SetTool(SketchTool.Select);
        UpdateToolButtons();
        RefreshScene();
        _sketch.UpdateOverlay(Viewport);
        OnSketchChanged();
    }

    /// <summary>
    /// What editing a sketch zooms to: its geometry and all its dimensions. The dimension rows are spaced in screen
    /// pixels, so where they end up depends on the zoom: fitted again until it settles (a few rounds).
    /// </summary>
    Box3? SketchFit(Sketch sk, Frame frame)
    {
        if (SketchBounds(sk, frame) is not { } box) return null;
        if (_sketch == null) return box;
        for (int i = 0; i < 5; i++)
        {
            var cam = Viewport.NormalToCamera(frame, box);
            if (_sketch.DimensionsExtent(cam) is not { } dims) break;
            var next = box; next.Add(dims);
            var grew = (next.Max - next.Min).Length - (box.Max - box.Min).Length;
            box = next;
            if (grew <= 1e-3 * (box.Max - box.Min).Length) break; // settled
        }
        return box;
    }

    /// <summary>
    /// The sketch's geometry in world space (lines, arcs and circles at their full extent, points; construction
    /// geometry too), or null when it has none. Never smaller than a fifth of the model, so a sketch of one point
    /// (a single hole position) does not zoom in absurdly far.
    /// </summary>
    Box3? SketchBounds(Sketch sk, Frame frame)
    {
        var b = Box3.Empty;
        foreach (var e in sk.Entities)
        {
            foreach (var (p, q) in SpryCad.App.Viewport.SketchDrawing.Segments2D(sk, e)) { b.Add(frame.ToWorld(p)); b.Add(frame.ToWorld(q)); }
            foreach (var id in e.PointIds) if (sk.Point(id) is { } pt) b.Add(frame.ToWorld(pt.P));
        }
        if (b.IsEmpty) return null;
        var model = Viewport.SceneBounds();
        var min = model.IsEmpty ? 0 : model.Diagonal * 0.2;
        if (b.Diagonal < min)
        {
            var c = b.Center; var h = new Vec3(min, min, min) * (0.5 / Math.Sqrt(3));
            b.Add(c - h); b.Add(c + h);
        }
        return b;
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
            SketchStatus.OverDefined => Palette.Danger,
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

    void AutoArrange(SketchEditor ed)
    {
        var n = ed.AutoArrangeDimensions();
        Status(n == 0 ? "All dimension labels are already arranged automatically."
            : $"{n} dimension label{(n == 1 ? "" : "s")} arranged automatically (Ctrl+Z to undo).");
    }

    void BuildSketchPage()
    {
        if (_sketch == null) return;
        // the Convert Entities page stays up: what was selected in the sketch is picked into it
        if (Converting) { TakeConvertPicks(); return; }
        // so do the Mirror and Offset pages
        if (InEntityPage) { TakeEntityPicks(); return; }
        var ed = _sketch;
        var page = new PmPage(ed.PointsOnly ? "Hole Positions" : "Sketch", "sketch") { OnOk = () => ExitSketch(true), OnCancel = DiscardSketch };
        if (ed.Selected.Count == 0)
        {
            var s = page.Section("Status");
            _sketchStatusText = new TextBlock { FontWeight = FontWeight.SemiBold };
            s.Children.Add(_sketchStatusText);
            page.Info(s, "Blue = under defined, black = fully defined, red = over defined.");
            if (ed.PointsOnly)
                page.Info(s, "Click on the face to place hole centers. Add dimensions (Smart Dimension) to locate them, then click OK.");
            // settings of the tool in use (a finished polygon is plain lines: its sides are set while drawing it)
            if (ed.Tool == SketchTool.Fillet)
            {
                var fs = page.Section("Sketch Fillet");
                page.Number(fs, "Radius", () => Units.Entry(ed.FilletRadius), v =>
                {
                    // only the value: the field applies on every change, also as the panel is built, so anything
                    // that rebuilds the panel from here would loop
                    if (UnitFormat.TryLength(v, Session.Lookup, out var r, out _) && r > 0) ed.FilletRadius = r;
                }, "sketchfillet");
                page.Info(fs, "Click corners where two lines meet (or the two lines). The lines stay as construction geometry, so dimensions to the sharp corner remain; delete the fillet's arc to restore the corner.");
            }
            if (ed.Tool == SketchTool.Polygon)
            {
                var poly = page.Section("Polygon");
                page.Number(poly, "Sides", () => ed.PolygonSides.ToString(), v => { if (int.TryParse(v, out var n)) ed.PolygonSides = Math.Clamp(n, 3, 64); }, "polygon", "");
                page.Info(poly, "Click the center, then a corner.");
            }
            var t = page.Section("Tools");
            page.Info(t, "L Line · R Rectangle · C Circle · A Arc · P Point · D Smart Dimension · T Trim · Esc Select. Drag points to move geometry. Double-click a dimension to change its value.");
            page.ButtonRow(t, "Auto Arrange Dimensions", () => AutoArrange(ed), "dimension");
            page.Info(t, "Dimension labels you place or drag stay where you put them; the others arrange themselves. Auto Arrange puts the placed ones back in the arrangement (select some dimensions first to arrange only those).");
            var m = page.Section("Model Edges");
            page.Info(m, "Convert Entities projects model edges and faces into the sketch: exact lines, arcs and circles.");
            page.ButtonRow(m, "Convert Entities", ConvertEntities, "convert");
        }
        else
        {
            var sel = page.Section("Selected Entities");
            foreach (var r in ed.Selected.Take(12)) sel.Children.Add(new TextBlock { Text = Describe(ed, r), FontSize = UI.TypeScale.Body });
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
            // converted geometry is held by its model edge (On Edge), not by anchors on its points
            var linked = ed.SelectedEntities.Where(e => e.Link != null).ToList();
            var onEdge = ed.Sk.Entities.Where(e => e.Link != null).SelectMany(e => e.PointIds).ToHashSet();
            var fixedPts = pts.Where(p => ed.Sk.Point(p)?.Fixed == true && p != Sketch.OriginId && !onEdge.Contains(p)).ToList();
            if (existing.Count > 0 || fixedPts.Count > 0 || linked.Count > 0)
            {
                var ex = page.Section("Existing Relations");
                if (linked.Count > 0)
                {
                    var row = new DockPanel();
                    var del = Dialogs.DeleteButton("Delete On Edge: the geometry stays where it is, no longer following the model");
                    del.Click += (_, _) => { ed.UnlinkSelection(); BuildSketchPage(); };
                    DockPanel.SetDock(del, Dock.Right);
                    row.Children.Add(del);
                    var dangling = linked.Count(e => e.Dangling);
                    var label = new TextBlock
                    {
                        Text = "⛓  On Edge" + (linked.Count > 1 ? $" ({linked.Count})" : "") + (dangling > 0 ? (dangling == linked.Count ? ", dangling" : $", {dangling} dangling") : ""),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    // dangling: its model edge no longer in the model
                    if (dangling > 0) label.Foreground = Palette.Warning;
                    row.Children.Add(label);
                    ex.Children.Add(row);
                }
                foreach (var c in existing)
                {
                    var row = new DockPanel();
                    var del = Dialogs.DeleteButton("Delete relation");
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
                    var del = Dialogs.DeleteButton("Unfix this point");
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
            // Fix / Float anchor and release points (of the selected points and geometry); a dimension has none
            if (ed.SelectedPoints.Any() || ed.SelectedEntities.Any())
            {
                var fixRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var fixB = new Button { Content = "⚓ Fix" }; fixB.Click += (_, _) => { ed.FixSelection(true); BuildSketchPage(); };
                var flB = new Button { Content = "Float" }; flB.Click += (_, _) => { ed.FixSelection(false); BuildSketchPage(); };
                ToolTip.SetTip(fixB, "Anchor the selected points (and the end points of selected geometry) where they are");
                ToolTip.SetTip(flB, "Release anchored points so the sketch can move them again");
                fixRow.Children.Add(fixB); fixRow.Children.Add(flB);
                opt.Children.Add(fixRow);
            }
            if (ed.Selected.Any(r => r.Kind == SkRefKind.Constraint && ed.Sk.Constraint(r.Id) is { IsDimension: true }))
                page.ButtonRow(opt, "Auto Arrange Dimensions", () => AutoArrange(ed), "dimension");

            // parameters of a single point / dimension
            if (ed.Selected.Count == 1)
            {
                var r = ed.Selected[0];
                long? pid = r.Kind == SkRefKind.Point ? r.Id : r.Kind == SkRefKind.Entity && ed.Sk.Entity(r.Id) is SkPointEntity pe ? pe.P : null;
                if (pid is long id && id != Sketch.OriginId && ed.Sk.Point(id) is { } pt)
                {
                    var par = page.Section("Parameters");
                    page.Number(par, "X", () => Units.Entry(pt.X), v => { if (UnitFormat.TryLength(v, Session.Lookup, out var x, out _) && Math.Abs(x - pt.X) > 1e-9) ed.SetPointPosition(id, new Vec2(x, pt.Y)); });
                    page.Number(par, "Y", () => Units.Entry(pt.Y), v => { if (UnitFormat.TryLength(v, Session.Lookup, out var y, out _) && Math.Abs(y - pt.Y) > 1e-9) ed.SetPointPosition(id, new Vec2(pt.X, y)); });
                }
                if (r.Kind == SkRefKind.Constraint && ed.Sk.Constraint(r.Id) is { IsDimension: true } dim)
                {
                    var par = page.Section("Dimension");
                    // applied on Enter / leaving the field (not per keystroke: "1" on the way to "12" would re-solve)
                    var vb = page.Number(par, "Value", () => dim.Expr ?? Units.Entry(dim.Value, dim.ValueKind), _ => { }, null, dim.Type == CType.Angle ? "°" : null);
                    void Apply() { if (ed.SetDimensionValue(dim, vb.Text)) { ed.UpdateOverlay(Viewport); OnSketchChanged(); } }
                    vb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Apply(); e.Handled = true; } };
                    vb.LostFocus += (_, _) => Apply();
                    page.ButtonRow(par, "Edit value…", () => ed.OpenModifyBox(dim));
                    page.Check(par, "Driven (reference)", () => dim.Driven, v => { dim.Driven = v; ed.Resolve(); Viewport.Redraw(); });
                    if (dim.Type == CType.Offset) page.ButtonRow(par, "Reverse direction", () => { ed.ReverseOffset(dim); BuildSketchPage(); }, "offset");
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
            case SkRefKind.Point: return DisplayNames.Of(ed.Sk, r.Id);
            case SkRefKind.Constraint:
                {
                    var c = ed.Sk.Constraint(r.Id);
                    return c == null ? "" : c.IsDimension ? $"{c.Name ?? "Dimension"} = {(c.Type == CType.Angle ? UnitFormat.Angle(c.Value) : ed.Units.Length(c.Value))}" : SketchEditor.RelationName(c.Type);
                }
            default:
                return DisplayNames.Of(ed.Sk, r.Id);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Convert Entities (PropertyManager, as SolidWorks'): model edges and faces picked into its box, optionally
    // followed along their tangent chains, projected into the sketch on OK

    PmPage? _convertPage;
    PmSelectionBox? _convertBox;
    TextBlock? _convertCount;
    bool _convertChain;

    /// <summary>The Convert Entities page is the one shown (picks in the graphics area go into its box).</summary>
    bool Converting => _convertPage != null && _page == _convertPage && _sketch != null;

    void ConvertEntities()
    {
        if (_sketch == null) { Status("Convert Entities works inside a sketch."); return; }
        if (Converting) { _convertPage!.Activate(_convertBox); return; }
        _sketch.SetTool(SketchTool.Select);
        var page = new PmPage("Convert Entities", "convert");
        var s = page.Section("Entities to Convert");
        var box = page.Selection(s, "Entities", SelFilter.Edge | SelFilter.Face,
            "Click model edges (lines, arcs, curves) and faces (all their edges). Click one again to remove it.");
        page.Check(s, "Select chain", () => _convertChain, v => { _convertChain = v; UpdateConvertCount(); });
        page.Info(s, "Select chain: each edge picked brings the edges tangent to it, end to end (a filleted outline in one click).");
        _convertCount = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = Palette.TextMuted, Margin = new Thickness(0, 4, 0, 0) };
        s.Children.Add(_convertCount);
        // model edges and faces selected already are what is converted
        foreach (var it in Selection.Items.Where(i => i.Kind is SelKind.Edge or SelKind.Face).ToList()) box.Add(it);
        Selection.Clear();
        box.Changed += () => { UpdateConvertCount(); UpdateEmphasis(); };
        page.OnOk = ConvertOk;
        page.OnCancel = EndConvert;
        _convertPage = page; _convertBox = box;
        ShowPage(page);
        ShowConfirmCorner(ConvertOk, EndConvert, "OK: convert (Enter)", "Cancel (Esc)");
        UpdateConvertCount();
        UpdateEmphasis();
    }

    void ConvertOk()
    {
        if (!Converting || _convertBox is not { } box) return;
        var (exact, mesh, count) = ConvertTargets(box.Items, _convertChain);
        if (count == 0) { Status("Pick model edges or faces to convert."); return; }
        _sketch!.ConvertEdges(exact, mesh);
        EndConvert();
    }

    /// <summary>Back to the sketch's page and its corner (Exit Sketch).</summary>
    void EndConvert()
    {
        if (!Converting) return;
        _convertPage = null; _convertBox = null; _convertCount = null;
        Viewport.Emphasis = Array.Empty<SelItem>();
        ShowConfirmCorner(() => ExitSketch(true), DiscardSketch, "Exit Sketch", "Discard changes", okIcon: "exitsketch");
        BuildSketchPage();
        Viewport.Redraw();
    }

    /// <summary>Model edges and faces selected in the sketch go into the page's box (picked again: taken out).</summary>
    void TakeConvertPicks()
    {
        var picks = Selection.Items.Where(i => i.Kind is SelKind.Edge or SelKind.Face).ToList();
        if (picks.Count == 0) return;
        foreach (var p in picks) _convertPage!.HandlePick(p);
        Selection.Clear();
        UpdateEmphasis();
    }

    void UpdateConvertCount()
    {
        if (_convertCount == null || _convertBox == null) return;
        var (_, _, n) = ConvertTargets(_convertBox.Items, _convertChain);
        _convertCount.Text = n == 0 ? "Nothing picked yet." : $"{n} edge{(n == 1 ? "" : "s")} to convert.";
    }

    /// <summary>
    /// What the picks stand for: faces, chains and edges of the bodies' exact model (converted as picked, so a face
    /// or chain is converted again as the model changes it), else display edges. With <paramref name="chain"/> an
    /// edge brings its tangent chain. Also how many edges that is.
    /// </summary>
    (List<SketchConvert.Request> exact, List<TopoEdge> mesh, int count) ConvertTargets(IEnumerable<SelItem> picks, bool chain)
    {
        var exact = new List<SketchConvert.Request>();
        var mesh = new List<TopoEdge>();
        var st = Session.DisplayResult?.State;
        foreach (var s in picks)
        {
            var body = Viewport.Bodies.FirstOrDefault(b => b.Id == s.BodyId);
            if (body == null) continue;
            var nb = body.Solid.Native;
            if (s.Kind == SelKind.Face)
            {
                if (nb != null && s.Name != null) exact.Add(new SketchConvert.Request(nb, body.Id, Face: s.Name));
                else mesh.AddRange(body.Solid.Topology.Edges.Where(e => e.FaceA == s.Name || e.FaceB == s.Name));
                continue;
            }
            if (s.Kind != SelKind.Edge) continue;
            Kernel.Native.NEdge? ne = null;
            if (nb != null && s.Edge != null && st != null)
                try { ne = Resolver.ResolveNativeEdge(s.Edge, st)?.edge; } catch { /* not found: the display edge */ }
            if (ne != null) exact.Add(new SketchConvert.Request(nb!, body.Id, Edge: ne, Chain: chain));
            else if (s.EdgeIndex >= 0 && s.EdgeIndex < body.Solid.Topology.Edges.Count) mesh.Add(body.Solid.Topology.Edges[s.EdgeIndex]);
        }
        mesh = mesh.Distinct().ToList();
        return (exact, mesh, exact.SelectMany(r => r.Edges()).Distinct().Count() + mesh.Count);
    }

    // Mirror Entities and Offset Entities: pages of their own (MainWindow.SketchEntityPages.cs)

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

    /// <summary>
    /// The OK / Cancel pair at the graphics area's top right. OK is the PropertyManager's green check, except in a
    /// sketch, where it is the Exit Sketch icon (<paramref name="okIcon"/>), as the command bar's button is.
    /// </summary>
    void ShowConfirmCorner(Action ok, Action cancel, string okTip, string cancelTip, string okIcon = "ok")
    {
        ConfirmBar.Children.Clear();
        var okB = new Button { Classes = { "tb" }, Content = okIcon == "ok" ? Icons.Make("ok", 24, PmPage.OkBrush) : Icons.Make(okIcon, 24) };
        ToolTip.SetTip(okB, okTip);
        okB.Click += (_, _) => ok();
        var cb = new Button { Classes = { "tb" }, Content = Icons.Make("cancel", 24, PmPage.CancelBrush) };
        ToolTip.SetTip(cb, cancelTip);
        cb.Click += (_, _) => cancel();
        ConfirmBar.Children.Add(okB);
        ConfirmBar.Children.Add(cb);
        ConfirmCorner.IsVisible = true;
    }

    void HideConfirmCorner() => ConfirmCorner.IsVisible = false;
}
