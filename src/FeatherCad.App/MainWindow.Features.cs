using Avalonia.VisualTree;
using Avalonia.Controls;
using FeatherCad.App.Session;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.App;

public partial class MainWindow
{
    sealed class EditContext
    {
        public required Feature Feature;
        public bool IsNew;
        /// <summary>Show the model before the feature and the result as a translucent preview.</summary>
        public bool OverlayPreview;
        public string? Hint;
        public Action? BeforeCommit;
        /// <summary>Extra cleanup when the edit is cancelled (e.g. remove the Hole Wizard position sketch).</summary>
        public Action? OnCancelled;
    }

    EditContext? _edit;

    // ------------------------------------------------------------------------------------------
    // generic edit / preview / commit

    void BeginEdit(Feature f, bool isNew, PmPage page, bool overlay, string? hint = null)
    {
        if (_sketch != null) ExitSketch(true);
        if (_edit != null) CancelEdit(); // starting another command cancels the one in progress
        _edit = new EditContext { Feature = f, IsNew = isNew, OverlayPreview = overlay, Hint = hint };
        if (string.IsNullOrEmpty(f.Id)) f.Id = Session.Doc.NewId();
        if (string.IsNullOrEmpty(f.Name)) f.Name = Session.Doc.NewName(f.Kind);
        page.OnOk = CommitEdit;
        page.OnCancel = CancelEdit;
        page.Changed += SchedulePreview;
        page.Message = hint;
        Selection.Clear();
        ShowPage(page);
        ShowConfirmCorner(CommitEdit, CancelEdit, "OK (Enter)", "Cancel (Esc)");
        FocusFirstField(page);
        SchedulePreview();
        Status($"{f.Name}: set the parameters, then click OK (Enter) or Esc to cancel.");
    }

    /// <summary>Focuses and selects the first text field of a page (type a value, Enter = OK).</summary>
    static void FocusFirstField(PmPage page)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var tb = page.View.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEnabled && !t.IsReadOnly);
            if (tb == null) return;
            tb.Focus();
            tb.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void RunPreview()
    {
        if (_edit == null) return;
        _edit.BeforeCommit?.Invoke();
        var doc = Session.Doc.Clone();
        var clone = _edit.Feature.Clone();
        if (_edit.IsNew)
        {
            var at = doc.ActiveCount;
            doc.Features.Insert(at, clone);
            if (doc.RollbackIndex != null) doc.RollbackIndex = at + 1;
        }
        else
        {
            var i = doc.IndexOf(clone.Id);
            if (i >= 0) doc.Features[i] = clone;
        }
        Session.RequestPreview(doc);
    }

    void CommitEdit()
    {
        if (_edit == null) return;
        // do not commit what the user did not type, or a feature the preview already showed to fail
        if (_page?.FirstInvalidField is { } bad)
        {
            _page.Message = $"\u2716 \"{bad}\" has an invalid value.";
            Status($"Fix \"{bad}\" first (or Esc to cancel).");
            return;
        }
        if (!_previewTimer.IsEnabled && !Session.Busy && Session.PreviewResult is { } pr
            && pr.Status.TryGetValue(_edit.Feature.Id, out var pst) && pst.Error != null)
        {
            if (_page != null) _page.Message = "\u2716 " + pst.Error;
            Status($"{_edit.Feature.Name} cannot be created: {pst.Error}");
            return;
        }
        _edit.BeforeCommit?.Invoke();
        var e = _edit;
        _edit = null;
        _previewTimer.Stop();
        var f = e.Feature.Clone();
        Session.Change(() =>
        {
            if (e.IsNew) Session.Doc.AddFeature(f);
            else
            {
                var i = Session.Doc.IndexOf(f.Id);
                if (i >= 0) Session.Doc.Features[i] = f;
            }
        });
        Session.EndPreview();
        ShowPage(null);
        HideConfirmCorner();
        Status($"{f.Name} {(e.IsNew ? "created" : "updated")}.");
        RefreshScene();
    }

    void CancelEdit()
    {
        var cancelled = _edit?.OnCancelled;
        _edit = null;
        _previewTimer.Stop();
        Session.EndPreview();
        ShowPage(null);
        HideConfirmCorner();
        cancelled?.Invoke();
        RefreshScene();
        Status("Cancelled.");
    }

    /// <summary>Opens the editor for an existing feature (double-click in the tree).</summary>
    public void EditFeature(string id)
    {
        var f = Session.Doc.Find(id);
        if (f == null) return;
        if (_sketch != null) ExitSketch(true);
        if (_page != null && _edit != null) CancelEdit();
        switch (f)
        {
            case SketchFeature sf:
                {
                    Frame frame;
                    var r = Session.Result;
                    if (r != null && r.State.Sketches.TryGetValue(sf.Id, out var sr)) frame = sr.Frame;
                    else
                    {
                        try { frame = Resolver.ResolvePlane(sf.Plane, r == null ? ModelState.Initial() : StateBefore(r, Session.Doc.IndexOf(sf.Id))); }
                        catch (Exception ex) { Status("Cannot edit sketch: " + ex.Message); return; }
                    }
                    var consumer = Session.Doc.ConsumersOf(sf.Id).FirstOrDefault();
                    EnterSketch((SketchFeature)sf.Clone(), frame, isNew: false, pointsOnly: consumer is HoleFeature);
                    break;
                }
            case ExtrudeFeature e: OpenExtrudePage((ExtrudeFeature)e.Clone(), false); break;
            case RevolveFeature rv: OpenRevolvePage((RevolveFeature)rv.Clone(), false); break;
            case FilletFeature fi: OpenBlendPage(fi.Clone(), false); break;
            case ChamferFeature ch: OpenBlendPage(ch.Clone(), false); break;
            case HoleFeature h: OpenHolePage((HoleFeature)h.Clone(), false); break;
            case ShellFeature s: OpenShellPage((ShellFeature)s.Clone(), false); break;
            case LinearPatternFeature lp: OpenLinearPatternPage((LinearPatternFeature)lp.Clone(), false); break;
            case CircularPatternFeature cp: OpenCircularPatternPage((CircularPatternFeature)cp.Clone(), false); break;
            case MirrorFeature m: OpenMirrorPage((MirrorFeature)m.Clone(), false); break;
            case PlaneFeature p: OpenPlanePage((PlaneFeature)p.Clone(), false); break;
            case CombineFeature c: OpenCombinePage((CombineFeature)c.Clone(), false); break;
        }
    }

    // ------------------------------------------------------------------------------------------
    // helpers to turn references back into selection items (for editing existing features)

    ModelState StateForEdit(Feature f, bool isNew)
    {
        var r = Session.Result;
        if (r == null) return ModelState.Initial();
        return isNew ? StateBefore(r, Session.Doc.ActiveCount) : StateBefore(r, Session.Doc.IndexOf(f.Id));
    }

    static SelItem EdgeItem(EdgeRef e, ModelState st)
    {
        try
        {
            var (b, te) = Resolver.ResolveEdge(e, st);
            return new SelItem { Kind = SelKind.Edge, BodyId = b.Id, EdgeIndex = te.Index, Edge = e.Clone(), Point = e.Hint, EdgeLinear = te.IsLinear, EdgeCircular = te.Closed };
        }
        catch { return new SelItem { Kind = SelKind.Edge, Edge = e.Clone(), Point = e.Hint, BodyId = e.Body }; }
    }

    static SelItem FaceItem(FaceRef f) => new()
    {
        Kind = SelKind.Face, Name = f.Name, BodyId = f.Body, Point = f.Hint, Normal = f.Normal,
        Planar = f.Normal.LengthSq > 0.5, FacePlane = f.Normal.LengthSq > 0.5 ? Plane.FromPointNormal(f.Hint, f.Normal) : null,
    };

    static SelItem? PlaneItem(PlaneRef? p, ModelState st)
    {
        if (p == null) return null;
        if (p.Std != null) return new SelItem { Kind = SelKind.Plane, Name = p.Std, Frame = Resolver.StdPlane(p.Std) };
        if (p.Feature != null) return new SelItem { Kind = SelKind.Plane, FeatureId = p.Feature, Name = p.Feature, Frame = st.Planes.GetValueOrDefault(p.Feature) };
        if (p.Face != null) return FaceItem(p.Face);
        return null;
    }

    static SelItem? DirItem(DirRef? d, ModelState st)
    {
        if (d == null || d.Axis != null) return null;
        if (d.Edge != null) return EdgeItem(d.Edge, st);
        if (d.Face != null) return FaceItem(d.Face);
        if (d.Plane != null) return PlaneItem(d.Plane, st);
        return null;
    }

    string? SketchFromSelection()
    {
        if (Selection.Items.FirstOrDefault(s => s.Kind == SelKind.Sketch) is { } s) return s.FeatureId;
        // the most recent unconsumed sketch (just exited) is used automatically, like SolidWorks
        var doc = Session.Doc;
        var active = doc.Features.Take(doc.ActiveCount).ToList();
        var last = active.LastOrDefault();
        if (last is SketchFeature sk && !doc.ConsumersOf(sk.Id).Any()) return sk.Id;
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Extrude

    void ExtrudeCommand(bool cut)
    {
        if (_sketch != null)
        {
            _pendingAfterSketch = cut ? "cut" : "extrude";
            ExitSketch(true);
            return;
        }
        var sk = SketchFromSelection();
        if (sk == null)
        {
            if (Selection.First is { } s && (s.Kind == SelKind.Plane || s.Kind == SelKind.Face && s.Planar))
            {
                StartSketchCommand(cut ? "cut" : "extrude");
                Status("Sketch the profile, then click Exit Sketch to continue the extrude.");
                return;
            }
        }
        OpenExtrudePage(new ExtrudeFeature { SketchId = sk ?? "", IsCut = cut, End = cut ? EndCondition.ThroughAll : EndCondition.Blind, Reverse = cut }, true);
    }

    void OpenExtrudePage(ExtrudeFeature f, bool isNew)
    {
        var page = new PmPage(f.IsCut ? "Cut-Extrude" : "Boss-Extrude", f.IsCut ? "cut" : "extrude");
        var from = page.Section("Sketch");
        var skBox = page.Selection(from, "Sketch", SelFilter.Sketch, "Profile sketch (select in the design tree or graphics area)", 1);
        if (!string.IsNullOrEmpty(f.SketchId)) skBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.SketchId });
        skBox.Changed += () => f.SketchId = skBox.Items.FirstOrDefault()?.FeatureId ?? "";
        var d1 = page.Section("Direction 1");
        page.Combo(d1, "End", new List<(EndCondition, string)>
        {
            (EndCondition.Blind, "Blind"), (EndCondition.ThroughAll, "Through All"), (EndCondition.ThroughAllBoth, "Through All - Both"), (EndCondition.MidPlane, "Mid Plane"),
        }, () => f.End, v => f.End = v);
        page.Check(d1, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Number(d1, "Depth", () => f.Depth, v => f.Depth = v, "dimension");
        var d2 = page.Section("Direction 2 (optional)");
        page.Number(d2, "Depth", () => f.Depth2 ?? "", v => f.Depth2 = string.IsNullOrWhiteSpace(v) ? null : v, "dimension", optional: true);
        page.Info(d2, "Blind extrusion in the opposite direction. Leave empty for none.");
        if (!f.IsCut)
        {
            var opt = page.Section("Options");
            page.Check(opt, "Merge result", () => f.Merge, v => f.Merge = v);
        }
        if (string.IsNullOrEmpty(f.SketchId)) page.Activate(skBox); else page.Activate(null);
        BeginEdit(f, isNew, page, overlay: false, hint: string.IsNullOrEmpty(f.SketchId) ? "Select a sketch to extrude." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Revolve

    void RevolveCommand(bool cut)
    {
        if (_sketch != null)
        {
            _pendingAfterSketch = cut ? "cutrevolve" : "revolve";
            ExitSketch(true);
            return;
        }
        var sk = SketchFromSelection();
        if (sk == null && Selection.First is { } s && (s.Kind == SelKind.Plane || s.Kind == SelKind.Face && s.Planar))
        {
            StartSketchCommand(cut ? "cutrevolve" : "revolve");
            Status("Sketch the profile and a centerline (axis), then click Exit Sketch.");
            return;
        }
        OpenRevolvePage(new RevolveFeature { SketchId = sk ?? "", IsCut = cut }, true);
    }

    void OpenRevolvePage(RevolveFeature f, bool isNew)
    {
        var page = new PmPage(f.IsCut ? "Cut-Revolve" : "Revolve", f.IsCut ? "cutrevolve" : "revolve");
        var s0 = page.Section("Sketch");
        var skBox = page.Selection(s0, "Sketch", SelFilter.Sketch, "Profile sketch", 1);
        if (!string.IsNullOrEmpty(f.SketchId)) skBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.SketchId });
        var axisSec = page.Section("Axis of Revolution");
        ComboBox? axisCombo = null;
        void FillAxes()
        {
            if (axisCombo != null) axisSec.Children.Remove(axisCombo.Parent as Control ?? axisCombo);
            var sk = Session.Doc.Find(f.SketchId) as SketchFeature;
            var lines = sk?.Sketch.Entities.OfType<SkLine>().OrderByDescending(l => l.Construction).ToList() ?? new();
            var opts = new List<(int, string)> { (0, "First centerline (auto)") };
            opts.AddRange(lines.Select(l => (l.Id, (l.Construction ? "Centerline" : "Line") + l.Id)));
            axisCombo = page.Combo(axisSec, "Axis", opts, () => f.AxisEntity, v => f.AxisEntity = v, "centerline");
        }
        FillAxes();
        skBox.Changed += () => { f.SketchId = skBox.Items.FirstOrDefault()?.FeatureId ?? ""; f.AxisEntity = 0; FillAxes(); };
        var d = page.Section("Direction 1");
        page.Number(d, "Angle", () => f.Angle, v => f.Angle = v, "revolve", "°");
        page.Check(d, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        if (!f.IsCut) page.Check(page.Section("Options"), "Merge result", () => f.Merge, v => f.Merge = v);
        if (!string.IsNullOrEmpty(f.SketchId)) page.Activate(null);
        BeginEdit(f, isNew, page, overlay: false);
    }

    // ------------------------------------------------------------------------------------------
    // Fillet / Chamfer

    void BlendCommand(bool fillet)
    {
        if (_sketch != null) ExitSketch(true);
        var edges = Selection.Items.Where(s => s.Kind == SelKind.Edge && s.Edge != null).Select(s => s.Edge!.Clone()).ToList();
        // a selected face contributes all its edges (like SolidWorks)
        foreach (var s in Selection.Items.Where(s => s.Kind == SelKind.Face))
        {
            var body = Session.Result?.State.FindBody(s.BodyId ?? "");
            if (body == null) continue;
            foreach (var e in body.Solid.Topology.Edges.Where(e => e.FaceA == s.Name || e.FaceB == s.Name))
                edges.Add(new EdgeRef { A = e.FaceA, B = e.FaceB, Body = body.Id, Hint = e.Midpoint });
        }
        if (fillet) OpenBlendPage(new FilletFeature { Edges = edges }, true);
        else OpenBlendPage(new ChamferFeature { Edges = edges }, true);
    }

    void OpenBlendPage(Feature f, bool isNew)
    {
        bool fillet = f is FilletFeature;
        var edges = fillet ? ((FilletFeature)f).Edges : ((ChamferFeature)f).Edges;
        var page = new PmPage(fillet ? "Fillet" : "Chamfer", fillet ? "fillet" : "chamfer");
        var s = page.Section("Items To " + (fillet ? "Fillet" : "Chamfer"));
        var st = StateForEdit(f, isNew);
        var box = page.Selection(s, "Edges", SelFilter.Edge | SelFilter.Face, "Edges (or faces) to " + (fillet ? "fillet" : "chamfer"));
        foreach (var e in edges) box.Items.Add(EdgeItem(e, st));
        box.Changed += () =>
        {
            // faces expand to their edges
            var list = new List<EdgeRef>();
            foreach (var it in box.Items)
            {
                if (it.Kind == SelKind.Edge && it.Edge != null) list.Add(it.Edge.Clone());
                else if (it.Kind == SelKind.Face && st.FindBody(it.BodyId ?? "") is { } b)
                    foreach (var e in b.Solid.Topology.Edges.Where(e => e.FaceA == it.Name || e.FaceB == it.Name))
                        list.Add(new EdgeRef { A = e.FaceA, B = e.FaceB, Body = b.Id, Hint = e.Midpoint });
            }
            edges.Clear(); edges.AddRange(list);
        };
        var p = page.Section(fillet ? "Fillet Parameters" : "Chamfer Parameters");
        if (fillet) page.Number(p, "Radius", () => ((FilletFeature)f).Radius, v => ((FilletFeature)f).Radius = v, "fillet");
        else page.Number(p, "Distance", () => ((ChamferFeature)f).Distance, v => ((ChamferFeature)f).Distance = v, "chamfer");
        if (fillet) page.Check(p, "Tangent propagation", () => ((FilletFeature)f).TangentPropagation, v => ((FilletFeature)f).TangentPropagation = v);
        else page.Check(p, "Tangent propagation", () => ((ChamferFeature)f).TangentPropagation, v => ((ChamferFeature)f).TangentPropagation = v);
        // SolidWorks "overflow": where the blend runs into neighbouring geometry it is trimmed by it (keep edge)
        if (fillet) page.Check(p, "Overflow: trim at neighbouring faces", () => ((FilletFeature)f).Overflow, v => ((FilletFeature)f).Overflow = v);
        else page.Check(p, "Overflow: trim at neighbouring faces", () => ((ChamferFeature)f).Overflow, v => ((ChamferFeature)f).Overflow = v);
        page.Info(p, "Supported: straight edges between planar faces, and circular edges (full or partial arcs) where a plane meets a cylinder.");
        BeginEdit(f, isNew, page, overlay: true, hint: edges.Count == 0 ? "Select edges in the graphics area." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Hole Wizard

    void HoleCommand()
    {
        if (_sketch != null) ExitSketch(true);
        var hole = new HoleFeature();
        var sel = Selection.First;
        if (sel != null && TryPlaneOf(sel, out var pr, out var frame))
        {
            _pendingHole = hole;
            StartSketch(pr!, frame, pointsOnly: true);
            Status("Hole Wizard: click on the face to place hole centers, dimension them, then click OK.");
            return;
        }
        var page = new PmPage("Hole Position", "hole");
        var s = page.Section("Positions");
        page.Info(s, "Select the planar face (or plane) on which to place the holes.");
        var box = page.Selection(s, "Face", SelFilter.PlanarFace | SelFilter.Plane, "Face or plane", 1);
        box.Changed += () =>
        {
            var it = box.Items.FirstOrDefault();
            if (TryPlaneOf(it, out var p2, out var f2))
            {
                ShowPage(null);
                _pendingHole = hole;
                StartSketch(p2!, f2, pointsOnly: true);
            }
        };
        page.OnCancel = () => ShowPage(null);
        page.OnOk = page.OnCancel;
        ShowPage(page);
    }

    void OpenHolePage(HoleFeature f, bool isNew)
    {
        var page = new PmPage("Hole Specification", "hole");
        var t = page.Section("Hole Type");
        Action syncSections = () => { };
        page.Combo(t, "Type", new List<(HoleType, string)> { (HoleType.Simple, "Simple (drilled)"), (HoleType.Counterbore, "Counterbore"), (HoleType.Countersink, "Countersink") },
            () => f.Type, v => { f.Type = v; syncSections(); });
        var std = page.Section("Size");
        var sizes = new List<(string, string)>
        {
            ("custom", "Custom"),
            ("M3", "M3 clearance (3.4)"), ("M4", "M4 clearance (4.5)"), ("M5", "M5 clearance (5.5)"), ("M6", "M6 clearance (6.6)"),
            ("M8", "M8 clearance (9)"), ("M10", "M10 clearance (11)"), ("M12", "M12 clearance (13.5)"),
        };
        TextBox? dia = null, cbd = null, cbdp = null, csd = null;
        // shows the standard size matching the current diameter (editing an M6 hole shows M6, not Custom)
        string CurrentStd() => sizes.Skip(1).FirstOrDefault(s => s.Item2.Contains($"({f.Diameter})")).Item1 ?? "custom";
        page.Combo(std, "Standard", sizes, CurrentStd, v =>
        {
            var spec = v switch
            {
                "M3" => (3.4, 6.5, 3.3, 6.6), "M4" => (4.5, 8.0, 4.4, 8.6), "M5" => (5.5, 10.0, 5.4, 10.4), "M6" => (6.6, 11.0, 6.5, 12.4),
                "M8" => (9.0, 15.0, 8.6, 16.4), "M10" => (11.0, 18.0, 10.8, 20.4), "M12" => (13.5, 20.0, 13.0, 24.4), _ => (0, 0, 0, 0),
            };
            if (spec.Item1 == 0) return;
            dia!.Text = spec.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cbd!.Text = spec.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cbdp!.Text = spec.Item3.ToString(System.Globalization.CultureInfo.InvariantCulture);
            csd!.Text = spec.Item4.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        dia = page.Number(std, "Diameter", () => f.Diameter, v => f.Diameter = v, "hole");
        var end = page.Section("End Condition");
        TextBox? depth = null;
        page.Check(end, "Through All", () => f.ThroughAll, v => { f.ThroughAll = v; if (depth != null) depth.IsEnabled = !v; });
        depth = page.Number(end, "Depth", () => f.Depth, v => f.Depth = v, "dimension");
        depth.IsEnabled = !f.ThroughAll;
        page.Check(end, "Drill point (118°)", () => f.DrillPoint, v => f.DrillPoint = v);
        var cb = page.Section("Counterbore");
        cbd = page.Number(cb, "C'bore dia.", () => f.CbDiameter, v => f.CbDiameter = v);
        cbdp = page.Number(cb, "C'bore depth", () => f.CbDepth, v => f.CbDepth = v);
        var cs = page.Section("Countersink");
        csd = page.Number(cs, "C'sink dia.", () => f.CsDiameter, v => f.CsDiameter = v);
        page.Number(cs, "C'sink angle", () => f.CsAngle, v => f.CsAngle = v, null, "°");
        // only the section of the chosen hole type is shown
        syncSections = () =>
        {
            if (cb.Parent is Control cbs) cbs.IsVisible = f.Type == HoleType.Counterbore;
            if (cs.Parent is Control css) css.IsVisible = f.Type == HoleType.Countersink;
        };
        syncSections();
        var pos = page.Section("Positions");
        page.ButtonRow(pos, "Edit hole positions…", () =>
        {
            if (isNew) { Status("Finish the hole first (OK), then use Edit Hole Positions from the design tree."); return; }
            CancelEdit();
            EditFeature(f.SketchId);
        }, "sketch");
        BeginEdit(f, isNew, page, overlay: false);
        if (isNew && _edit != null)
        {
            // the position sketch was committed just before this page opened: cancelling removes it
            var sketchId = f.SketchId;
            _edit.OnCancelled = () =>
            {
                if (Session.Doc.Find(sketchId) is SketchFeature && !Session.Doc.ConsumersOf(sketchId).Any()) Session.Undo();
            };
        }
    }

    // ------------------------------------------------------------------------------------------
    // Shell

    void ShellCommand()
    {
        var faces = Selection.Items.Where(s => s.Kind == SelKind.Face).Select(s => s.ToFaceRef()).ToList();
        OpenShellPage(new ShellFeature { Faces = faces }, true);
    }

    void OpenShellPage(ShellFeature f, bool isNew)
    {
        var page = new PmPage("Shell", "shell");
        var p = page.Section("Parameters");
        page.Number(p, "Thickness", () => f.Thickness, v => f.Thickness = v, "shell");
        var box = page.Selection(p, "Faces", SelFilter.Face, "Faces to remove");
        foreach (var fr in f.Faces) box.Items.Add(FaceItem(fr));
        box.Changed += () => { f.Faces = box.Items.Select(i => i.ToFaceRef()).ToList(); };
        BeginEdit(f, isNew, page, overlay: true, hint: f.Faces.Count == 0 ? "Select the faces to remove." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Patterns / mirror

    List<string> SeedsFromSelection()
    {
        var ids = new List<string>();
        foreach (var s in Selection.Items)
        {
            var fid = s.Kind == SelKind.Feature ? s.FeatureId : s.OwningFeatureId;
            if (fid != null && Session.Doc.Find(fid) is { } f && f is not SketchFeature && !ids.Contains(fid)) ids.Add(fid);
        }
        if (ids.Count == 0 && Tree.SelectedId is { } tid && Session.Doc.Find(tid) is { } tf && tf is not SketchFeature) ids.Add(tid);
        return ids;
    }

    PmSelectionBox SeedBox(PmPage page, List<string> seeds)
    {
        var s = page.Section("Features to Pattern");
        var box = page.Selection(s, "Features", SelFilter.Feature, "Select features in the tree or click their faces");
        foreach (var id in seeds) box.Items.Add(new SelItem { Kind = SelKind.Feature, FeatureId = id });
        box.Changed += () => { seeds.Clear(); seeds.AddRange(box.Items.Select(i => i.FeatureId!).Distinct()); };
        return box;
    }

    void AxisButtons(PmPage page, Avalonia.Controls.StackPanel s, Action<string> set)
    {
        var row = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        foreach (var a in new[] { "X", "Y", "Z" })
        {
            var b = new Button { Content = a + " axis", Padding = new Avalonia.Thickness(6, 2) };
            b.Click += (_, _) => { set(a); page.RaiseChanged(); };
            row.Children.Add(b);
        }
        s.Children.Add(row);
    }

    void LinearPatternCommand() => OpenLinearPatternPage(new LinearPatternFeature { Seeds = SeedsFromSelection() }, true);

    void OpenLinearPatternPage(LinearPatternFeature f, bool isNew)
    {
        var page = new PmPage("Linear Pattern", "lpattern");
        var st = StateForEdit(f, isNew);
        var d1 = page.Section("Direction 1");
        var dirBox = page.Selection(d1, "Direction", SelFilter.Edge | SelFilter.Face | SelFilter.Plane, "Pattern direction: a linear edge, face, plane, or axis button", 1);
        if (DirItem(f.Direction, st) is { } di) dirBox.Items.Add(di);
        var axisLbl = new TextBlock { FontSize = 11, Foreground = Avalonia.Media.Brushes.DimGray };
        void UpdAxis() => axisLbl.Text = f.Direction.Axis != null ? $"Using {f.Direction.Axis} axis" : "";
        dirBox.Changed += () => { if (dirBox.Items.FirstOrDefault()?.ToDirRef() is { } dr) { f.Direction = dr; UpdAxis(); } };
        AxisButtons(page, d1, a => { f.Direction = new DirRef { Axis = a }; dirBox.Items.Clear(); dirBox.Refresh(); UpdAxis(); });
        d1.Children.Add(axisLbl); UpdAxis();
        page.Check(d1, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Number(d1, "Spacing", () => f.Spacing, v => f.Spacing = v, "dimension");
        page.Number(d1, "Instances", () => f.Count, v => f.Count = v, "lpattern", "");
        var d2 = page.Section("Direction 2");
        var use2 = page.Check(d2, "Use direction 2", () => f.Direction2 != null, v => f.Direction2 = v ? (f.Direction2 ?? new DirRef { Axis = "Y" }) : null);
        AxisButtons(page, d2, a => { f.Direction2 = new DirRef { Axis = a }; use2.IsChecked = true; });
        page.Check(d2, "Reverse direction", () => f.Reverse2, v => f.Reverse2 = v);
        page.Number(d2, "Spacing", () => f.Spacing2, v => f.Spacing2 = v, "dimension");
        page.Number(d2, "Instances", () => f.Count2, v => f.Count2 = v, "lpattern", "");
        var seeds = SeedBox(page, f.Seeds);
        page.Activate(f.Seeds.Count == 0 ? seeds : dirBox);
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the feature(s) to pattern." : null);
    }

    void CircularPatternCommand() => OpenCircularPatternPage(new CircularPatternFeature { Seeds = SeedsFromSelection() }, true);

    void OpenCircularPatternPage(CircularPatternFeature f, bool isNew)
    {
        var page = new PmPage("Circular Pattern", "cpattern");
        var st = StateForEdit(f, isNew);
        var p = page.Section("Parameters");
        var axBox = page.Selection(p, "Axis", SelFilter.Edge | SelFilter.Face | SelFilter.Plane, "Axis: a cylindrical face, circular or linear edge, or axis button", 1);
        if (DirItem(f.Axis, st) is { } ai) axBox.Items.Add(ai);
        var axisLbl = new TextBlock { FontSize = 11, Foreground = Avalonia.Media.Brushes.DimGray };
        void UpdAxis() => axisLbl.Text = f.Axis.Axis != null ? $"Using {f.Axis.Axis} axis" : "";
        axBox.Changed += () => { if (axBox.Items.FirstOrDefault()?.ToDirRef() is { } dr) { f.Axis = dr; UpdAxis(); } };
        AxisButtons(page, p, a => { f.Axis = new DirRef { Axis = a }; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); });
        p.Children.Add(axisLbl); UpdAxis();
        page.Check(p, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Number(p, "Angle", () => f.Angle, v => f.Angle = v, "cpattern", "°");
        page.Number(p, "Instances", () => f.Count, v => f.Count = v, null, "");
        page.Check(p, "Equal spacing", () => f.EqualSpacing, v => f.EqualSpacing = v);
        var seeds = SeedBox(page, f.Seeds);
        page.Activate(f.Seeds.Count == 0 ? seeds : axBox);
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the feature(s) to pattern." : null);
    }

    void MirrorCommand()
    {
        var seeds = SeedsFromSelection();
        var plane = Selection.Items.FirstOrDefault(s => s.Kind == SelKind.Plane || s.Kind == SelKind.Face && s.Planar)?.ToPlaneRef() ?? PlaneRef.Right;
        OpenMirrorPage(new MirrorFeature { Seeds = seeds, Plane = plane }, true);
    }

    void OpenMirrorPage(MirrorFeature f, bool isNew)
    {
        var page = new PmPage("Mirror", "mirror");
        var st = StateForEdit(f, isNew);
        var p = page.Section("Mirror Face/Plane");
        var box = page.Selection(p, "Plane", SelFilter.Plane | SelFilter.PlanarFace, "Mirror plane or planar face", 1);
        if (PlaneItem(f.Plane, st) is { } pi) box.Items.Add(pi);
        box.Changed += () => { if (box.Items.FirstOrDefault()?.ToPlaneRef() is { } pr) f.Plane = pr; };
        var seeds = SeedBox(page, f.Seeds);
        page.Activate(f.Seeds.Count == 0 ? seeds : box);
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the feature(s) to mirror." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Reference plane

    void PlaneCommand()
    {
        var b = Selection.Items.FirstOrDefault(s => s.Kind == SelKind.Plane || s.Kind == SelKind.Face && s.Planar)?.ToPlaneRef() ?? PlaneRef.Front;
        OpenPlanePage(new PlaneFeature { Base = b }, true);
    }

    void OpenPlanePage(PlaneFeature f, bool isNew)
    {
        var page = new PmPage("Plane", "plane");
        var st = StateForEdit(f, isNew);
        var r = page.Section("First Reference");
        var box = page.Selection(r, "Reference", SelFilter.Plane | SelFilter.PlanarFace, "Plane or planar face", 1);
        if (PlaneItem(f.Base, st) is { } pi) box.Items.Add(pi);
        box.Changed += () => { if (box.Items.FirstOrDefault()?.ToPlaneRef() is { } pr) f.Base = pr; };
        page.Number(r, "Offset", () => f.Offset, v => f.Offset = v, "dimension");
        page.Check(r, "Flip offset", () => f.Flip, v => f.Flip = v);
        BeginEdit(f, isNew, page, overlay: false);
    }

    // ------------------------------------------------------------------------------------------
    // Combine bodies

    void CombineCommand() => OpenCombinePage(new CombineFeature(), true);

    void OpenCombinePage(CombineFeature f, bool isNew)
    {
        var page = new PmPage("Combine", "combine");
        var t = page.Section("Operation Type");
        page.Combo(t, "Operation", new List<(CombineOp, string)> { (CombineOp.Add, "Add"), (CombineOp.Subtract, "Subtract"), (CombineOp.Common, "Common") }, () => f.Op, v => f.Op = v);
        var m = page.Section("Bodies");
        var main = page.Selection(m, "Main body", SelFilter.Body, "Main body (click a face of it)", 1);
        if (!string.IsNullOrEmpty(f.MainBody)) main.Items.Add(new SelItem { Kind = SelKind.Body, BodyId = f.MainBody });
        main.Changed += () => f.MainBody = main.Items.FirstOrDefault()?.BodyId ?? "";
        var tools = page.Selection(m, "Bodies to combine", SelFilter.Body, "Other bodies");
        foreach (var b in f.ToolBodies) tools.Items.Add(new SelItem { Kind = SelKind.Body, BodyId = b });
        tools.Changed += () => f.ToolBodies = tools.Items.Select(i => i.BodyId!).ToList();
        page.Info(m, "Tip: extrude with 'Merge result' off to create separate bodies.");
        BeginEdit(f, isNew, page, overlay: false);
    }
}
