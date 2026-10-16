using Avalonia.VisualTree;
using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App;

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
        /// <summary>Other changes the feature makes to the part (a base flange's sheet metal body): in the preview and the commit.</summary>
        public Action<CadDocument>? ApplyToDoc;
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
        Status($"{f.Name}: set the parameters, then click OK (Enter) or press Esc to cancel.");
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
        _edit.ApplyToDoc?.Invoke(doc);
        // a preview that rebuilds slow features says so up front (from how long they took last time)
        if (_page != null && !_edit.IsNew)
        {
            var (secs, slowest, slowSecs) = Session.Estimate(doc, doc.IndexOf(clone.Id));
            var note = secs >= 2 ? $"⏱ Each preview rebuilds all subsequent features, about {secs:0} s{(slowest != null && slowSecs >= 1 ? $"; slowest: {slowest}, {slowSecs:0.0} s" : "")}." : null;
            if (note != null && (_page.Message == null || _page.Message == _edit.Hint || _page.Message.StartsWith("⏱"))) _page.Message = note;
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
            Status($"Correct \"{bad}\" before clicking OK, or press Esc to cancel.");
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
            e.ApplyToDoc?.Invoke(Session.Doc);
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
                        catch (Exception ex) { App.Log(ex, "Edit sketch"); Status("Could not edit sketch: " + ex.Message); return; }
                    }
                    var consumer = Session.Doc.ConsumersOf(sf.Id).FirstOrDefault();
                    EnterSketch((SketchFeature)sf.Clone(), frame, isNew: false, pointsOnly: consumer is HoleFeature);
                    // converted geometry where its model edges are now (the model may have changed since it was saved)
                    if (r != null && _sketch != null && _sketch.Sk.Entities.Any(x => x.Link != null))
                    {
                        SketchConvert.Update(_sketch.Sk, frame, StateBefore(r, Session.Doc.IndexOf(sf.Id)), new List<string>());
                        _sketch.Resolve();
                    }
                    break;
                }
            case ExtrudeFeature e: OpenExtrudePage((ExtrudeFeature)e.Clone(), false); break;
            case RevolveFeature rv: OpenRevolvePage((RevolveFeature)rv.Clone(), false); break;
            case SweepFeature sw: OpenSweepPage((SweepFeature)sw.Clone(), false); break;
            case LoftFeature lf: OpenLoftPage((LoftFeature)lf.Clone(), false); break;
            case HelicalSweepFeature hs: OpenHelicalPage((HelicalSweepFeature)hs.Clone(), false); break;
            case ThreadFeature th: OpenThreadPage((ThreadFeature)th.Clone(), false); break;
            case FilletFeature fi: OpenBlendPage(fi.Clone(), false); break;
            case ChamferFeature ch: OpenBlendPage(ch.Clone(), false); break;
            case HoleFeature h: OpenHolePage((HoleFeature)h.Clone(), false); break;
            case ShellFeature s: OpenShellPage((ShellFeature)s.Clone(), false); break;
            case DraftFeature dr: OpenDraftPage((DraftFeature)dr.Clone(), false); break;
            case LinearPatternFeature lp: OpenLinearPatternPage((LinearPatternFeature)lp.Clone(), false); break;
            case CircularPatternFeature cp: OpenCircularPatternPage((CircularPatternFeature)cp.Clone(), false); break;
            case MirrorFeature m: OpenMirrorPage((MirrorFeature)m.Clone(), false); break;
            case PlaneFeature p: OpenPlanePage((PlaneFeature)p.Clone(), false); break;
            case AxisFeature ax: OpenAxisPage((AxisFeature)ax.Clone(), false); break;
            case CombineFeature c: OpenCombinePage((CombineFeature)c.Clone(), false); break;
            case SheetMetal.Features.BaseFlangeFeature bf: OpenBaseFlangePage((SheetMetal.Features.BaseFlangeFeature)bf.Clone(), false); break;
            case SheetMetal.Features.EdgeFlangeFeature ef: OpenEdgeFlangePage((SheetMetal.Features.EdgeFlangeFeature)ef.Clone(), false); break;
            case SheetMetal.Features.HemFeature hm: OpenHemPage((SheetMetal.Features.HemFeature)hm.Clone(), false); break;
            case SheetMetal.Features.SheetCutFeature sc: OpenSheetCutPage((SheetMetal.Features.SheetCutFeature)sc.Clone(), false); break;
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
        if (d.Feature is { } af)
            return st.Axes.TryGetValue(af, out var ax)
                ? new SelItem { Kind = SelKind.Axis, FeatureId = af, Point = (ax.A + ax.B) / 2, Normal = (ax.B - ax.A).Normalized, AxisHalfLength = Vec3.Distance(ax.A, ax.B) / 2 }
                : new SelItem { Kind = SelKind.Axis, FeatureId = af };
        // a round face or circular edge: shown as its axis, as it was picked
        if (d.Face is { } rf && st.Bodies.OrderBy(b => b.Id == rf.Body ? 0 : 1).Select(b => ViewportControl.FaceAxis(b, rf.Name)).FirstOrDefault(a => a != null) is { } fa) return fa;
        if (d.Edge is { } re && EdgeItem(re, st) is { EdgeCircular: true, BodyId: { } eb } && st.FindBody(eb) is { } body && ViewportControl.EdgeAxis(body, re) is { } ea) return ea;
        if (d.Edge != null) return EdgeItem(d.Edge, st);
        if (d.Face != null) return FaceItem(d.Face);
        if (d.Plane != null) return PlaneItem(d.Plane, st);
        if (d.Curve is { Sketch: { } cs }) return new SelItem { Kind = SelKind.SketchSegment, FeatureId = cs, SketchItem = d.Curve.SketchEntity };
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
                Status("Sketch the profile, then click Exit Sketch to continue.");
                return;
            }
        }
        OpenExtrudePage(new ExtrudeFeature { SketchId = sk ?? "", IsCut = cut, End = cut ? EndCondition.ThroughAll : EndCondition.Blind, Reverse = cut }.Sized(Units), true);
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
            (EndCondition.UpToSurface, "Up To Surface"), (EndCondition.UpToBody, "Up To Body"),
        }, () => f.End, v => f.End = v);
        page.Check(d1, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        // Up To Surface / Up To Body: where it ends (a plane, a planar or cylindrical face; for a body, any face of it)
        var upTo = page.Selection(d1, "Up to", SelFilter.Face | SelFilter.Plane,
            "Plane, or planar or cylindrical face, to end at; for Up To Body, any face of the body", 1);
        if (PlaneItem(f.UpTo, StateForEdit(f, isNew)) is { } ui) upTo.Items.Add(ui);
        upTo.Changed += () => f.UpTo = upTo.Items.FirstOrDefault() is { } it ? (it.Kind == SelKind.Face ? new PlaneRef { Face = it.ToFaceRef() } : it.ToPlaneRef()) : null;
        page.Number(d1, "Depth", () => f.Depth, v => f.Depth = v, "dimension");
        // the side walls tapered from the sketch plane (narrowing along the extrusion; outward: widening)
        page.Number(d1, "Draft angle", () => f.Draft ?? "", v => f.Draft = string.IsNullOrWhiteSpace(v) ? null : v, "draft", "°", optional: true);
        page.Check(d1, "Draft outward", () => f.DraftOutward, v => f.DraftOutward = v);
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
            Status("Sketch the profile and a centerline, then click Exit Sketch.");
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
        // one box, as the other features: a line of the profile's sketch, or an axis from the model (a cylindrical or
        // conical face, a straight or circular edge); the X / Y / Z buttons; empty: the sketch's first centerline
        var st = StateForEdit(f, isNew);
        var axBox = page.Selection(axisSec, "Axis", SelFilter.SketchSegment | SelFilter.Edge | SelFilter.Face | SelFilter.Axis,
            "Centerline or line in the profile sketch, linear edge, axis of a cylindrical or conical face or circular edge (right-click > Select Axis), or an axis button", 1);
        if (f.Axis != null) { if (DirItem(f.Axis, st) is { } ai) axBox.Items.Add(ai); }
        else if (f.AxisEntity != 0) axBox.Items.Add(new SelItem { Kind = SelKind.SketchSegment, FeatureId = f.SketchId, SketchItem = f.AxisEntity });
        var axisLbl = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = UI.Palette.TextMuted, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Action? axes = null;
        void UpdAxis()
        {
            axisLbl.Text = f.Axis?.Axis != null ? $"Using the {f.Axis.Axis} axis" : f.Axis == null && f.AxisEntity == 0 ? "Using the sketch's first centerline" : "";
            axes?.Invoke();
        }
        axBox.Changed += () =>
        {
            var it = axBox.Items.FirstOrDefault();
            if (it == null) { f.Axis = null; f.AxisEntity = 0; }
            else if (it.Kind == SelKind.SketchSegment)
            {
                // a line of the profile's sketch only (another sketch's line is not tied to this one)
                if (it.FeatureId == f.SketchId && Session.Doc.Find(f.SketchId) is SketchFeature sf && sf.Sketch.Entity(it.SketchItem) is SkLine)
                { f.AxisEntity = it.SketchItem; f.Axis = null; }
                else { axBox.Items.Clear(); axBox.Refresh(); Status("Select a line in the profile sketch, or a model edge or face."); }
            }
            else if (it.ToDirRef() is { } dr) { f.Axis = dr; f.AxisEntity = 0; }
            UpdAxis();
        };
        axes = AxisButtons(page, axisSec, () => f.Axis?.Axis, ax => { f.Axis = new DirRef { Axis = ax }; f.AxisEntity = 0; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); });
        axisSec.Children.Add(axisLbl); UpdAxis();
        skBox.Changed += () =>
        {
            f.SketchId = skBox.Items.FirstOrDefault()?.FeatureId ?? "";
            if (f.AxisEntity != 0) { f.AxisEntity = 0; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); }
        };
        var d = page.Section("Direction 1");
        page.Number(d, "Angle", () => f.Angle, v => f.Angle = v, "revolve", "°");
        page.Check(d, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        if (!f.IsCut) page.Check(page.Section("Options"), "Merge result", () => f.Merge, v => f.Merge = v);
        // the sketch picked: the axis box takes the next pick (a line, edge or face)
        if (!string.IsNullOrEmpty(f.SketchId)) page.Activate(axBox);
        BeginEdit(f, isNew, page, overlay: false);
    }

    // ------------------------------------------------------------------------------------------
    // Sweep

    void SweepCommand(bool cut)
    {
        if (_sketch != null) ExitSketch(true);
        // selected sketches: the first is the profile, the second the path (the sketch just finished: the profile)
        var sketches = Selection.Items.Where(s => s.Kind == SelKind.Sketch).Select(s => s.FeatureId).Distinct().ToList();
        if (sketches.Count == 0 && SketchFromSelection() is { } last) sketches.Add(last);
        OpenSweepPage(new SweepFeature { ProfileSketchId = sketches.ElementAtOrDefault(0) ?? "", PathSketchId = sketches.ElementAtOrDefault(1) ?? "", IsCut = cut }, true);
    }

    void OpenSweepPage(SweepFeature f, bool isNew)
    {
        var page = new PmPage(f.IsCut ? "Cut-Sweep" : "Sweep", f.IsCut ? "cutsweep" : "sweep");
        var sec = page.Section("Profile and Path");
        var profBox = page.Selection(sec, "Profile", SelFilter.Sketch, "Closed profile sketch, perpendicular to the path at its start", 1);
        if (!string.IsNullOrEmpty(f.ProfileSketchId)) profBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.ProfileSketchId });
        profBox.Changed += () => f.ProfileSketchId = profBox.Items.FirstOrDefault()?.FeatureId ?? "";
        var pathBox = page.Selection(sec, "Path", SelFilter.Sketch, "Path sketch of tangent lines and arcs, or a circle, starting on the profile plane", 1);
        if (!string.IsNullOrEmpty(f.PathSketchId)) pathBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.PathSketchId });
        pathBox.Changed += () => f.PathSketchId = pathBox.Items.FirstOrDefault()?.FeatureId ?? "";
        page.Info(sec, "The profile keeps its orientation to the path. Round sharp corners of the path with sketch fillets.");
        if (!f.IsCut) page.Check(page.Section("Options"), "Merge result", () => f.Merge, v => f.Merge = v);
        page.Activate(string.IsNullOrEmpty(f.ProfileSketchId) ? profBox : string.IsNullOrEmpty(f.PathSketchId) ? pathBox : null);
        BeginEdit(f, isNew, page, overlay: false,
            hint: string.IsNullOrEmpty(f.ProfileSketchId) ? "Select the profile sketch, then the path sketch." : string.IsNullOrEmpty(f.PathSketchId) ? "Select the path sketch." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Loft

    void LoftCommand(bool cut)
    {
        if (_sketch != null) ExitSketch(true);
        // selected sketches, in the order picked: the profiles (the sketch just finished: the first)
        var sketches = Selection.Items.Where(s => s.Kind == SelKind.Sketch).Select(s => s.FeatureId).Distinct().ToList();
        if (sketches.Count == 0 && SketchFromSelection() is { } last) sketches.Add(last);
        OpenLoftPage(new LoftFeature { SectionSketchIds = sketches, IsCut = cut }, true);
    }

    void OpenLoftPage(LoftFeature f, bool isNew)
    {
        var page = new PmPage(f.IsCut ? "Cut-Loft" : "Loft", f.IsCut ? "cutloft" : "loft");
        var sec = page.Section("Profiles");
        var profBox = page.Selection(sec, "Profiles", SelFilter.Sketch | SelFilter.PlanarFace,
            "Closed profile sketches or planar faces, in order; the first or last can be a point");
        for (int i = 0; i < f.SectionSketchIds.Count; i++)
            profBox.Items.Add(f.SectionSketchIds[i] == "" && f.SectionFaces.ElementAtOrDefault(i) is { } sf ? FaceItem(sf) : new SelItem { Kind = SelKind.Sketch, FeatureId = f.SectionSketchIds[i] });
        profBox.Refresh();
        profBox.Changed += () =>
        {
            f.SectionSketchIds = profBox.Items.Select(i => i.Kind == SelKind.Face ? "" : i.FeatureId ?? "").ToList();
            f.SectionFaces = profBox.Items.Any(i => i.Kind == SelKind.Face) ? profBox.Items.Select(i => i.Kind == SelKind.Face ? i.ToFaceRef() : null).ToList() : new();
        };
        page.Info(sec, "The loft passes through the profiles in the order selected. Vertices are matched between profiles for minimum twist.");
        var gsec = page.Section("Guide Curves");
        var guideBox = page.Selection(gsec, "Guides", SelFilter.Sketch, "Optional guide sketches of tangent lines and arcs crossing every profile, one guide per chain");
        foreach (var id in f.GuideSketchIds) guideBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = id });
        guideBox.Refresh();
        guideBox.Changed += () => f.GuideSketchIds = guideBox.Items.Select(i => i.FeatureId).ToList();
        var csec = page.Section("Centerline");
        var clBox = page.Selection(csec, "Centerline", SelFilter.Sketch, "Optional sketch of tangent lines and arcs crossing every profile plane for the loft to follow", 1);
        if (!string.IsNullOrEmpty(f.CenterlineSketchId)) clBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.CenterlineSketchId });
        clBox.Refresh();
        clBox.Changed += () => f.CenterlineSketchId = clBox.Items.FirstOrDefault()?.FeatureId;
        var opts = page.Section("Start/End Constraints");
        var tangency = new List<(LoftTangency, string)> { (LoftTangency.None, "None"), (LoftTangency.Normal, "Normal to profile") };
        page.Combo(opts, "Start", tangency, () => f.StartTangency, v => f.StartTangency = v);
        page.Number(opts, "Start tangent length", () => f.StartLength, v => f.StartLength = v, unit: "×");
        page.Combo(opts, "End", tangency, () => f.EndTangency, v => f.EndTangency = v);
        page.Number(opts, "End tangent length", () => f.EndLength, v => f.EndLength = v, unit: "×");
        var osec = page.Section("Options");
        page.Check(osec, "Close loft", () => f.Closed, v => f.Closed = v);
        if (!f.IsCut) page.Check(osec, "Merge result", () => f.Merge, v => f.Merge = v);
        page.Activate(f.SectionSketchIds.Count < 2 ? profBox : null);
        BeginEdit(f, isNew, page, overlay: false,
            hint: f.SectionSketchIds.Count < 2 ? "Select at least two profile sketches in order, then any guide curves." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Helical sweep

    void HelicalCommand(bool cut)
    {
        if (_sketch != null) ExitSketch(true);
        OpenHelicalPage(new HelicalSweepFeature { SketchId = SketchFromSelection() ?? "", IsCut = cut }.Sized(Units), true);
    }

    void OpenHelicalPage(HelicalSweepFeature f, bool isNew)
    {
        var page = new PmPage(f.IsCut ? "Cut-Helical" : "Helical", f.IsCut ? "cuthelical" : "helical");
        var s0 = page.Section("Profile");
        var skBox = page.Selection(s0, "Sketch", SelFilter.Sketch, "Sketch with a closed profile and a centerline for the helix axis", 1);
        if (!string.IsNullOrEmpty(f.SketchId)) skBox.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.SketchId });
        var axisSec = page.Section("Axis");
        // one box: a line of the profile's sketch, or an axis from the model (a cylindrical face, a straight or
        // circular edge); the X / Y / Z buttons; empty: the sketch's first centerline
        var st = StateForEdit(f, isNew);
        var axBox = page.Selection(axisSec, "Axis", SelFilter.SketchSegment | SelFilter.Edge | SelFilter.Face | SelFilter.Axis,
            "Centerline in the profile sketch, linear edge, axis of a cylindrical face or circular edge (right-click > Select Axis), or an axis button", 1);
        if (f.Axis != null) { if (DirItem(f.Axis, st) is { } ai) axBox.Items.Add(ai); }
        else if (f.AxisEntity != 0) axBox.Items.Add(new SelItem { Kind = SelKind.SketchSegment, FeatureId = f.SketchId, SketchItem = f.AxisEntity });
        var axisLbl = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = UI.Palette.TextMuted, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Action? axes = null;
        void UpdAxis()
        {
            axisLbl.Text = f.Axis?.Axis != null ? $"Using the {f.Axis.Axis} axis" : f.Axis == null && f.AxisEntity == 0 ? "Using the sketch's first centerline" : "";
            axes?.Invoke();
        }
        axBox.Changed += () =>
        {
            var it = axBox.Items.FirstOrDefault();
            if (it == null) { f.Axis = null; f.AxisEntity = 0; }
            else if (it.Kind == SelKind.SketchSegment)
            {
                // a line of the profile's sketch only (another sketch's line is not tied to this one)
                if (it.FeatureId == f.SketchId && Session.Doc.Find(f.SketchId) is SketchFeature sf && sf.Sketch.Entity(it.SketchItem) is SkLine)
                { f.AxisEntity = it.SketchItem; f.Axis = null; }
                else { axBox.Items.Clear(); axBox.Refresh(); Status("Select a line in the profile sketch, or a model edge or face."); }
            }
            else if (it.ToDirRef() is { } dr) { f.Axis = dr; f.AxisEntity = 0; }
            UpdAxis();
        };
        axes = AxisButtons(page, axisSec, () => f.Axis?.Axis, ax => { f.Axis = new DirRef { Axis = ax }; f.AxisEntity = 0; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); });
        axisSec.Children.Add(axisLbl); UpdAxis();
        skBox.Changed += () =>
        {
            f.SketchId = skBox.Items.FirstOrDefault()?.FeatureId ?? "";
            if (f.AxisEntity != 0) { f.AxisEntity = 0; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); }
        };
        var h = page.Section("Helix");
        page.Number(h, "Pitch", () => f.Pitch, v => f.Pitch = v, "dimension");
        page.Number(h, "Revolutions", () => f.Revolutions, v => f.Revolutions = v, "helical", "");
        page.Check(h, "Left hand", () => f.LeftHand, v => f.LeftHand = v);
        page.Check(h, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Info(h, "The profile must not touch the axis. Its length along the axis must be less than the pitch.");
        var ro = page.Section("Run-out");
        page.Check(ro, "Run out at the end", () => f.RunOut, v => f.RunOut = v);
        page.Number(ro, "Length", () => f.RunOutLength, v => f.RunOutLength = v, "dimension", optional: true);
        page.Info(ro, "The end continues straight along the helix tangent. If the length is blank, cuts run until clear of the model and bosses run one pitch.");
        if (!f.IsCut) page.Check(page.Section("Options"), "Merge result", () => f.Merge, v => f.Merge = v);
        if (!string.IsNullOrEmpty(f.SketchId)) page.Activate(null);
        BeginEdit(f, isNew, page, overlay: false, hint: string.IsNullOrEmpty(f.SketchId) ? "Select a sketch with a closed profile and a centerline." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Thread

    void ThreadCommand()
    {
        var face = Selection.Items.FirstOrDefault(s => s.Kind == SelKind.Face);
        var f = new ThreadFeature().Sized(Units);
        if (face != null) f.Face = face.ToFaceRef();
        OpenThreadPage(f, true);
    }

    void OpenThreadPage(ThreadFeature f, bool isNew)
    {
        var page = new PmPage("Thread", "thread");
        var s0 = page.Section("Face");
        var box = page.Selection(s0, "Face", SelFilter.Face, "Cylindrical face of a shaft or hole; the thread starts at the end nearest the click", 1);
        if (!string.IsNullOrEmpty(f.Face.Name)) box.Items.Add(FaceItem(f.Face));
        box.Changed += () => f.Face = box.Items.FirstOrDefault()?.ToFaceRef() ?? new FaceRef();
        var spec = page.Section("Specification");
        // the sizes the libraries offer (a company's preferred ones), as "table|size"; ISO metric coarse is a thread's
        // table when it names none (threads made before libraries)
        const string coarse = "ISO261-coarse";
        var sizes = new List<(string, string)> { ("Auto", "Auto (ISO metric, fits the face)") };
        foreach (var tbl in Core.Library.LibraryLink.Current.Offered(Core.Library.TableKinds.Thread))
        {
            var pitch = tbl.Column("pitch");
            if (pitch < 0) continue;
            sizes.AddRange(tbl.Rows.Select(r => (tbl.Key + "|" + r.Key, r.Key.StartsWith('M') && !r.Key.Contains('x') ? $"{r.Key} × {Core.Expr.Format(r.Values[pitch])}" : r.Key)));
        }
        sizes.Add(("Custom", "Custom pitch"));
        string SizeValue() => f.Size is "Auto" or "Custom" ? f.Size : Core.Library.ThreadSizes.TableOf(f) + "|" + f.Size;
        // a size the libraries no longer offer stays this thread's
        if (!sizes.Any(s => s.Item1 == SizeValue())) sizes.Insert(1, (SizeValue(), $"{f.Size} (not offered by the libraries)"));
        page.Combo(spec, "Size", sizes, SizeValue, v =>
        {
            var i = v.IndexOf('|');
            f.Size = i < 0 ? v : v[(i + 1)..];
            f.Standard = i < 0 || v[..i] == coarse ? null : v[..i];
        }, "thread");
        page.Number(spec, "Pitch (custom)", () => f.Pitch, v => f.Pitch = v, "dimension");
        page.Combo(spec, "End", new List<(ThreadEnd, string)>
        {
            (ThreadEnd.Blind, "Blind"), (ThreadEnd.UpToNext, "Up To Next"), (ThreadEnd.ThroughAll, "Through All"),
        }, () => f.End, v => f.End = v);
        page.Number(spec, "Length (blind)", () => f.Length, v => f.Length = v, "dimension", optional: true);
        page.Info(spec, "Use Up To Next on bolts to stop short of the head or a shoulder, and Through All for nuts and through holes.");
        var opt = page.Section("Options");
        page.Number(opt, "Clearance", () => f.Clearance, v => f.Clearance = v, "dimension");
        page.Info(opt, "Flank clearance for 3D-printed threads, typically 0.1–0.3 mm. The crest is truncated where it would become too narrow.");
        page.Check(opt, "Lead-in chamfer", () => f.Chamfer, v => f.Chamfer = v);
        page.Check(opt, "Cosmetic thread", () => f.Cosmetic, v => f.Cosmetic = v);
        page.Info(opt, "A cosmetic thread is shown as a helix on the face and is always modeled on export. View > Threads overrides the display of all threads.");
        page.Combo(opt, "Thread end", new List<(ThreadRunout, string)>
        {
            (ThreadRunout.None, "Plain"), (ThreadRunout.Relief, "Relief groove"), (ThreadRunout.Tangent, "Tangent run-out (shafts)"),
        }, () => f.Runout, v => f.Runout = v);
        page.Info(opt, "Relief groove: an undercut at the thread end for tool clearance at a shoulder. Tangent run-out: the thread groove exits the shaft along the helix tangent.");
        page.Check(opt, "Left hand", () => f.LeftHand, v => f.LeftHand = v);
        page.Check(opt, "Start at the other end", () => f.Reverse, v => f.Reverse = v);
        if (!string.IsNullOrEmpty(f.Face.Name)) page.Activate(null);
        BeginEdit(f, isNew, page, overlay: true, hint: string.IsNullOrEmpty(f.Face.Name) ? "Select a cylindrical face near the end where the thread starts." : null);
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
        if (fillet) OpenBlendPage(new FilletFeature { Edges = edges }.Sized(Units), true);
        else OpenBlendPage(new ChamferFeature { Edges = edges }.Sized(Units), true);
    }

    void OpenBlendPage(Feature f, bool isNew)
    {
        bool fillet = f is FilletFeature;
        var edges = fillet ? ((FilletFeature)f).Edges : ((ChamferFeature)f).Edges;
        var page = new PmPage(fillet ? "Fillet" : "Chamfer", fillet ? "fillet" : "chamfer");
        var s = page.Section("Items To " + (fillet ? "Fillet" : "Chamfer"));
        var st = StateForEdit(f, isNew);
        var box = page.Selection(s, "Edges", SelFilter.Edge | SelFilter.Face, "Edges or faces to " + (fillet ? "fillet" : "chamfer"));
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
        if (f is FilletFeature fs)
        {
            // besides the edges picked, edges found: every one of a kind on the body, or between features (a casting's
            // blends in one feature)
            var sc = page.Section("Scope");
            page.Check(sc, "Whole body", () => fs.WholeBody, v => fs.WholeBody = v);
            var fbox = page.Selection(sc, "Between features", SelFilter.Feature, "Select features in the design tree or graphics area");
            foreach (var id in fs.Features) fbox.Items.Add(new SelItem { Kind = SelKind.Feature, FeatureId = id });
            fbox.Changed += () => { fs.Features.Clear(); fs.Features.AddRange(fbox.Items.Select(i => i.FeatureId!).Distinct()); };
            page.Combo(sc, "Edges", new List<(BlendScopeKind, string)>
            {
                (BlendScopeKind.Concave, "Concave"), (BlendScopeKind.Convex, "Convex"), (BlendScopeKind.All, "Concave and convex"),
            }, () => fs.ScopeKind, v => fs.ScopeKind = v);
            page.Info(sc, "Whole body fillets every edge of the selected type; Between features fillets the edges where the features meet the body or each other, including pattern and mirror copies. Edges that cannot be filleted are skipped with a warning.");
        }
        var p = page.Section(fillet ? "Fillet Parameters" : "Chamfer Parameters");
        if (fillet) page.Number(p, "Radius", () => ((FilletFeature)f).Radius, v => ((FilletFeature)f).Radius = v, "fillet");
        else
        {
            var ch = (ChamferFeature)f;
            TextBox? d2 = null, angle = null;
            CheckBox? flip = null;
            static Control RowOf(Control field) => (Control)field.Parent!.Parent!; // number field -> its label row
            void ShowFields()
            {
                RowOf(d2!).IsVisible = ch.Type == ChamferType.DistanceDistance;
                RowOf(angle!).IsVisible = ch.Type == ChamferType.AngleDistance;
                flip!.IsVisible = ch.Type != ChamferType.Distance;
            }
            page.Combo(p, "Type", new List<(ChamferType, string)>
            {
                (ChamferType.Distance, "Distance"), (ChamferType.AngleDistance, "Angle-Distance"), (ChamferType.DistanceDistance, "Distance-Distance"),
            }, () => ch.Type, v => { ch.Type = v; ShowFields(); });
            page.Number(p, "Distance", () => ch.Distance, v => ch.Distance = v, "chamfer");
            d2 = page.Number(p, "Distance 2", () => ch.Distance2, v => ch.Distance2 = v, "chamfer");
            angle = page.Number(p, "Angle", () => ch.Angle, v => ch.Angle = v, "dimension", "°");
            flip = page.Check(p, "Flip direction (measure on the other face)", () => ch.Flip, v => ch.Flip = v);
            ShowFields();
        }
        if (fillet) page.Check(p, "Tangent propagation", () => ((FilletFeature)f).TangentPropagation, v => ((FilletFeature)f).TangentPropagation = v);
        else page.Check(p, "Tangent propagation", () => ((ChamferFeature)f).TangentPropagation, v => ((ChamferFeature)f).TangentPropagation = v);
        // SolidWorks "overflow": where the blend runs into neighboring geometry it is trimmed by it (keep edge)
        if (fillet)
        {
            page.Check(p, "Overflow: trim at neighboring faces", () => ((FilletFeature)f).Overflow, v => ((FilletFeature)f).Overflow = v);
            // (where it wraps round another fillet into an edge not picked: faded out to nothing at that fillet's
            // tangent point, over this length back from it; empty: the whole wrap, 0: a square end)
            var fade = page.Number(p, "Fade length (empty: whole wrap, 0: none)", () => ((FilletFeature)f).FadeLength, v => ((FilletFeature)f).FadeLength = v, "fillet", optional: true);
            ToolTip.SetTip(fade, "Where the fillet wraps round another fillet into an edge not picked, it fades out to nothing at that fillet's tangent point, over this length back from it. Empty: the whole wrap. 0: a square end.");
            // (where convex and concave edges meet at a corner: which is filleted first, the other rolling round it)
            var ff = (FilletFeature)f;
            page.Combo(p, "Mixed corners", new List<(MixedCorners, string)>
            {
                (MixedCorners.Auto, "Auto"), (MixedCorners.ConvexFirst, "Convex first (blended foot)"), (MixedCorners.ConcaveFirst, "Concave first (continuous foot)"),
            }, () => ff.MixedCorners, v => ff.MixedCorners = v);
        }
        else
        {
            page.Check(p, "Overflow: trim at neighboring faces", () => ((ChamferFeature)f).Overflow, v => ((ChamferFeature)f).Overflow = v);
            page.Check(p, "Flat corners where three chamfers meet", () => ((ChamferFeature)f).FlatCorners, v => ((ChamferFeature)f).FlatCorners = v);
        }
        var scoped = f is FilletFeature { WholeBody: true } or FilletFeature { Features.Count: > 0 };
        BeginEdit(f, isNew, page, overlay: true, hint: edges.Count == 0 && !scoped ? "Select edges in the graphics area, or set a scope." : null);
    }

    // ------------------------------------------------------------------------------------------
    // Hole Wizard

    void HoleCommand()
    {
        if (PartOnly("Hole Wizard")) return;
        if (_sketch != null) ExitSketch(true);
        var hole = new HoleFeature().Sized(Units);
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
        page.Info(s, "Select the planar face or plane on which to place the holes.");
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
        TextBox? dia = null, cbd = null, cbdp = null, csd = null, csa = null;
        // sizes from the libraries (section 34): the diameters set from the standard, size and fit picked
        Core.Library.LibrarySnapshot? Resolve(string item) => Core.Library.LibraryLink.Resolve(Session.Doc, item);
        void Fill()
        {
            if (f.Standard == null) return;
            Core.Library.HoleSizes.Apply(f, Resolve);
            // a size the counterbore or countersink table lacks keeps the values it had: said, so they are typed
            if (Core.Library.HoleSizes.Missing(f, Resolve) is { } missing) Status(missing);
            dia!.Text = f.Diameter; cbd!.Text = f.CbDiameter; cbdp!.Text = f.CbDepth; csd!.Text = f.CsDiameter; csa!.Text = f.CsAngle;
        }
        page.Combo(t, "Type", new List<(HoleType, string)> { (HoleType.Simple, "Simple"), (HoleType.Counterbore, "Counterbore"), (HoleType.Countersink, "Countersink") },
            () => f.Type, v => { f.Type = v; syncSections(); Fill(); });
        var std = page.Section("Size");
        var sizes = new List<(string, string)> { ("custom", "Custom") };
        foreach (var tbl in Core.Library.LibraryLink.Current.Offered(Core.Library.TableKinds.Clearance))
            sizes.AddRange(tbl.Rows.Select(r => (tbl.Key + "|" + r.Key, $"{r.Key} clearance ({tbl.Standard})")));
        // a hole made before libraries, with an ISO 273 normal diameter, shows its size (editing an M6 hole shows M6, not Custom)
        if (f.Standard == null && UnitFormat.TryLength(f.Diameter, Session.Lookup, out var d0, out _)
            && Core.Library.LibraryLink.Current.Table("ISO273") is { } iso && iso.Rows.FirstOrDefault(r => Math.Abs(r.Values[iso.Column("normal")] - d0) < 1e-9) is { } guess)
        { f.Standard = "ISO273"; f.Size = guess.Key; }
        string StdValue() => f.Standard != null && f.Size != null ? f.Standard + "|" + f.Size : "custom";
        if (!sizes.Any(s => s.Item1 == StdValue())) sizes.Insert(1, (StdValue(), $"{f.Size} clearance (not offered by the libraries)"));
        page.Combo(std, "Standard", sizes, StdValue, v =>
        {
            var i = v.IndexOf('|');
            if (i < 0) { f.Standard = null; f.Size = null; f.Fit = null; return; }
            f.Standard = v[..i]; f.Size = v[(i + 1)..];
            Fill();
        });
        page.Combo(std, "Fit", new List<(string, string)> { ("close", "Close"), ("normal", "Normal"), ("loose", "Loose") },
            () => f.Fit ?? "normal", v => { f.Fit = v == "normal" ? null : v; Fill(); });
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
        csa = page.Number(cs, "C'sink angle", () => f.CsAngle, v => f.CsAngle = v, null, "°");
        // only the section of the chosen hole type is shown
        syncSections = () =>
        {
            if (cb.Parent is Control cbs) cbs.IsVisible = f.Type == HoleType.Counterbore;
            if (cs.Parent is Control css) css.IsVisible = f.Type == HoleType.Countersink;
        };
        syncSections();
        var pos = page.Section("Positions");
        page.ButtonRow(pos, "Edit Hole Positions…", () =>
        {
            if (isNew) { Status("Click OK to create the hole, then use Edit Hole Positions in the design tree."); return; }
            CancelEdit();
            EditFeature(f.SketchId);
        }, "sketch");
        BeginEdit(f, isNew, page, overlay: false);
        // diameters typed over the standard's: a custom hole (a library update must not size it again)
        if (_edit != null) _edit.BeforeCommit = () =>
        {
            if (f.Standard == null) return;
            var sized = (HoleFeature)f.Clone();
            Core.Library.HoleSizes.Apply(sized, Resolve);
            bool same = sized.Diameter == f.Diameter && (f.Type != HoleType.Counterbore || (sized.CbDiameter == f.CbDiameter && sized.CbDepth == f.CbDepth))
                && (f.Type != HoleType.Countersink || (sized.CsDiameter == f.CsDiameter && sized.CsAngle == f.CsAngle));
            if (!same) { f.Standard = null; f.Size = null; f.Fit = null; }
        };
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
        OpenShellPage(new ShellFeature { Faces = faces }.Sized(Units), true);
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
    // Draft

    void DraftCommand()
    {
        var faces = Selection.Items.Where(s => s.Kind == SelKind.Face).Select(s => s.ToFaceRef()).ToList();
        OpenDraftPage(new DraftFeature { Faces = faces }, true);
    }

    void OpenDraftPage(DraftFeature f, bool isNew)
    {
        var page = new PmPage("Draft", "draft");
        var st = StateForEdit(f, isNew);
        var a = page.Section("Draft Angle");
        page.Number(a, "Angle", () => f.Angle, v => f.Angle = v, "draft", "°");
        var n = page.Section("Neutral Plane");
        var neutral = page.Selection(n, "Neutral plane", SelFilter.Plane | SelFilter.PlanarFace,
            "Plane or planar face about which the faces are drafted", 1);
        if (PlaneItem(f.Neutral, st) is { } pi) neutral.Items.Add(pi);
        neutral.Changed += () => { if (neutral.Items.FirstOrDefault()?.ToPlaneRef() is { } pr) f.Neutral = pr; };
        page.Check(n, "Reverse pull direction", () => f.Reverse, v => f.Reverse = v);
        page.Info(n, "The part narrows along the pull direction. The pull direction points into the part from a neutral face, or along a neutral plane's normal.");
        var fs = page.Section("Faces to Draft");
        var faces = page.Selection(fs, "Faces", SelFilter.Face, "Planar faces, or cylindrical faces parallel to the pull direction");
        foreach (var fr in f.Faces) faces.Items.Add(FaceItem(fr));
        faces.Changed += () => { f.Faces = faces.Items.Select(i => i.ToFaceRef()).ToList(); };
        // a new draft: the first click picks the neutral plane, the next ones the faces (as SolidWorks)
        neutral.Changed += () => { if (page.ActiveBox == neutral && neutral.Items.Count == 1) page.Activate(faces); };
        page.Activate(isNew && f.Faces.Count == 0 ? neutral : faces);
        BeginEdit(f, isNew, page, overlay: true, hint: f.Faces.Count == 0 ? "Select the neutral plane or face, then the faces to draft." : null);
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
        var box = page.Selection(s, "Features", SelFilter.Feature, "Select features in the design tree or graphics area");
        foreach (var id in seeds) box.Items.Add(new SelItem { Kind = SelKind.Feature, FeatureId = id });
        box.Changed += () => { seeds.Clear(); seeds.AddRange(box.Items.Select(i => i.FeatureId!).Distinct()); };
        return box;
    }

    /// <summary>
    /// X / Y / Z axis buttons; the axis in use (<paramref name="current"/>) shows pressed, also when the page is
    /// opened again to edit. Returns a refresh, for when the choice changes another way (an edge picked instead).
    /// </summary>
    Action AxisButtons(PmPage page, Avalonia.Controls.StackPanel s, Func<string?> current, Action<string> set)
    {
        var row = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var buttons = new List<(string axis, Button button)>();
        void Refresh()
        {
            foreach (var (a, b) in buttons)
            {
                var on = current() == a;
                if (on)
                {
                    b.Background = UI.Palette.SelectedBg;
                    b.BorderBrush = UI.Palette.Accent;
                }
                else { b.ClearValue(Button.BackgroundProperty); b.ClearValue(Button.BorderBrushProperty); }
                b.FontWeight = on ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
            }
        }
        foreach (var a in new[] { "X", "Y", "Z" })
        {
            var b = new Button { Content = a + " axis", Padding = new Avalonia.Thickness(6, 2), BorderThickness = new Avalonia.Thickness(1) };
            b.Click += (_, _) => { set(a); Refresh(); page.RaiseChanged(); };
            // hovering shows the axis in the graphics area (through the origin, in the triad's colors)
            var (dir, col) = a switch
            {
                "X" => (Vec3.UnitX, new Rgba(0.85f, 0.1f, 0.1f)),
                "Y" => (Vec3.UnitY, new Rgba(0.1f, 0.65f, 0.1f)),
                _ => (Vec3.UnitZ, new Rgba(0.1f, 0.3f, 0.9f)),
            };
            b.PointerEntered += (_, _) => Viewport.AxisPreview = (dir, col);
            b.PointerExited += (_, _) => Viewport.AxisPreview = null;
            b.DetachedFromVisualTree += (_, _) => Viewport.AxisPreview = null; // the page closed under the pointer
            row.Children.Add(b);
            buttons.Add((a, b));
        }
        s.Children.Add(row);
        Refresh();
        return Refresh;
    }

    void LinearPatternCommand() => OpenLinearPatternPage(new LinearPatternFeature { Seeds = SeedsFromSelection() }.Sized(Units), true);

    void OpenLinearPatternPage(LinearPatternFeature f, bool isNew)
    {
        var page = new PmPage("Linear Pattern", "lpattern");
        var st = StateForEdit(f, isNew);
        var d1 = page.Section("Direction 1");
        var dirBox = page.Selection(d1, "Direction", SelFilter.Edge | SelFilter.Face | SelFilter.Plane | SelFilter.Axis, "Linear edge, face, plane, axis of a cylindrical face, or an axis button", 1);
        if (DirItem(f.Direction, st) is { } di) dirBox.Items.Add(di);
        var axisLbl = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = UI.Palette.TextMuted };
        Action? axes1 = null;
        void UpdAxis() { axisLbl.Text = f.Direction.Axis != null ? $"Using the {f.Direction.Axis} axis" : ""; axes1?.Invoke(); }
        dirBox.Changed += () => { if (dirBox.Items.FirstOrDefault()?.ToDirRef() is { } dr) { f.Direction = dr; UpdAxis(); } };
        axes1 = AxisButtons(page, d1, () => f.Direction.Axis, a => { f.Direction = new DirRef { Axis = a }; dirBox.Items.Clear(); dirBox.Refresh(); UpdAxis(); });
        d1.Children.Add(axisLbl); UpdAxis();
        page.Check(d1, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Number(d1, "Spacing", () => f.Spacing, v => f.Spacing = v, "dimension");
        page.Number(d1, "Instances", () => f.Count, v => f.Count = v, "lpattern", "");
        var d2 = page.Section("Direction 2");
        var axisLbl2 = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = UI.Palette.TextMuted };
        Action? axes2 = null;
        void UpdAxis2() { axisLbl2.Text = f.Direction2?.Axis != null ? $"Using the {f.Direction2.Axis} axis" : ""; axes2?.Invoke(); }
        var use2 = page.Check(d2, "Use direction 2", () => f.Direction2 != null, v => { f.Direction2 = v ? (f.Direction2 ?? new DirRef { Axis = "Y" }) : null; UpdAxis2(); });
        axes2 = AxisButtons(page, d2, () => f.Direction2?.Axis, a => { f.Direction2 = new DirRef { Axis = a }; use2.IsChecked = true; UpdAxis2(); });
        d2.Children.Add(axisLbl2); UpdAxis2();
        page.Check(d2, "Reverse direction", () => f.Reverse2, v => f.Reverse2 = v);
        page.Number(d2, "Spacing", () => f.Spacing2, v => f.Spacing2 = v, "dimension");
        page.Number(d2, "Instances", () => f.Count2, v => f.Count2 = v, "lpattern", "");
        var sk = page.Section("Instances to Skip");
        var skipBox = page.IntList(sk, "Instances", () => f.Skip, v => f.Skip = v, 2,
            "Instances to skip, e.g. 2, 5, 7-9, or click their numbers in the graphics area. The seed is 1; numbering runs along direction 1, then row by row along direction 2.");
        var seeds = SeedBox(page, f.Seeds);
        page.Activate(f.Seeds.Count == 0 ? seeds : dirBox);
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the features to pattern." : null);
        ShowInstanceMarkers(f, st, f.Seeds, () => f.Skip, skipBox, page);
    }

    void CircularPatternCommand() => OpenCircularPatternPage(new CircularPatternFeature { Seeds = SeedsFromSelection() }, true);

    void OpenCircularPatternPage(CircularPatternFeature f, bool isNew)
    {
        var page = new PmPage("Circular Pattern", "cpattern");
        var st = StateForEdit(f, isNew);
        var p = page.Section("Parameters");
        var axBox = page.Selection(p, "Axis", SelFilter.Edge | SelFilter.Face | SelFilter.Plane | SelFilter.Axis, "Axis of a cylindrical face or circular edge (right-click > Select Axis), a linear edge, or an axis button", 1);
        if (DirItem(f.Axis, st) is { } ai) axBox.Items.Add(ai);
        var axisLbl = new TextBlock { FontSize = UI.TypeScale.Small, Foreground = UI.Palette.TextMuted };
        Action? axes = null;
        void UpdAxis() { axisLbl.Text = f.Axis.Axis != null ? $"Using the {f.Axis.Axis} axis" : ""; axes?.Invoke(); }
        axBox.Changed += () => { if (axBox.Items.FirstOrDefault()?.ToDirRef() is { } dr) { f.Axis = dr; UpdAxis(); } };
        axes = AxisButtons(page, p, () => f.Axis.Axis, a => { f.Axis = new DirRef { Axis = a }; axBox.Items.Clear(); axBox.Refresh(); UpdAxis(); });
        p.Children.Add(axisLbl); UpdAxis();
        page.Check(p, "Reverse direction", () => f.Reverse, v => f.Reverse = v);
        page.Number(p, "Angle", () => f.Angle, v => f.Angle = v, "cpattern", "°");
        page.Number(p, "Instances", () => f.Count, v => f.Count = v, null, "");
        page.Check(p, "Equal spacing", () => f.EqualSpacing, v => f.EqualSpacing = v);
        var sk = page.Section("Instances to Skip");
        var skipBox = page.IntList(sk, "Instances", () => f.Skip, v => f.Skip = v, 2,
            "Instances to skip, e.g. 2, 5, 7-9, or click their numbers in the graphics area. The seed is 1; numbering runs around the axis in the pattern direction.");
        var seeds = SeedBox(page, f.Seeds);
        page.Activate(f.Seeds.Count == 0 ? seeds : axBox);
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the features to pattern." : null);
        ShowInstanceMarkers(f, st, f.Seeds, () => f.Skip, skipBox, page);
    }

    InstanceMarkers? _instanceMarkers;

    /// <summary>
    /// Numbered markers at the pattern's instances (centre of the seeds' tools within the model, on the face toward
    /// the eye, moved by each instance's transform);
    /// clicking one adds it to or removes it from the skip list (written into the page's field, which updates the
    /// preview). Removed when the page closes.
    /// </summary>
    void ShowInstanceMarkers(Feature f, ModelState st, List<string> seeds, Func<List<int>> getSkip, TextBox skipBox, PmPage page)
    {
        _instanceMarkers?.Dispose();
        // the direction the seeds go into the part: a circular pattern's axis; for a linear pattern the sketch normal
        // of an extruded or hole seed, else the normal of its two directions (none with one direction only)
        Vec3? DepthAxis()
        {
            if (f is CircularPatternFeature cp) return Resolver.ResolveDir(cp.Axis, st).dir.Normalized;
            if (f is not LinearPatternFeature lp) return null;
            foreach (var id in seeds)
            {
                var sk = Session.Doc.Features.FirstOrDefault(x => x.Id == id) switch
                {
                    ExtrudeFeature e => e.SketchId, HoleFeature h => h.SketchId, _ => null,
                };
                if (sk != null && st.Sketches.TryGetValue(sk, out var sr)) return sr.Frame.Normal.Normalized;
            }
            if (lp.Direction2 == null) return null;
            var n = Vec3.Cross(Resolver.ResolveDir(lp.Direction, st).dir, Resolver.ResolveDir(lp.Direction2, st).dir);
            return n.Length > 1e-9 ? n.Normalized : null;
        }
        List<(int, Vec3, Vec3)> Positions()
        {
            var tool = Box3.Empty;
            foreach (var id in seeds) if (st.Tools.TryGetValue(id, out var t)) tool.Add(t.Tool.Bounds);
            if (tool.IsEmpty) return new();
            // a through-all cut's tool reaches far past the part: centre on the part of it within the model
            var box = tool;
            var mb = st.Bounds;
            if (!mb.IsEmpty && box.Overlaps(mb)) box = new Box3 { Min = Vec3.Max(box.Min, mb.Min), Max = Vec3.Min(box.Max, mb.Max) };
            var c = box.Center;
            Vec3 front = c, back = c;
            if (DepthAxis() is { } ax)
            {
                // on the part's faces at either end along the depth (the model's points within the seed's tool), so
                // the markers sit on the face toward the eye rather than inside the part
                double lo = double.MaxValue, hi = double.MinValue, tol = 1e-6 * Math.Max(1, tool.Diagonal);
                foreach (var body in st.Bodies)
                {
                    var ps = body.Mesh.Positions;
                    for (int i = 0; i + 2 < ps.Length; i += 3)
                    {
                        var p = new Vec3(ps[i], ps[i + 1], ps[i + 2]);
                        if (!tool.Contains(p, tol)) continue;
                        var d = Vec3.Dot(p, ax);
                        lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                    }
                }
                if (lo <= hi)
                {
                    var dc = Vec3.Dot(c, ax);
                    front = c + ax * (lo - dc); back = c + ax * (hi - dc);
                }
            }
            var ts = Regenerator.PatternTransforms(f, st, Session.Lookup) ?? new();
            var res = new List<(int, Vec3, Vec3)> { (1, front, back) };
            for (int i = 0; i < ts.Count; i++) res.Add((i + 2, ts[i].Point(front), ts[i].Point(back)));
            return res;
        }
        var markers = new InstanceMarkers(Viewport, Positions, getSkip, list => skipBox.Text = string.Join(", ", list));
        _instanceMarkers = markers;
        page.Changed += () => { if (_instanceMarkers == markers) markers.Rebuild(); };
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
        BeginEdit(f, isNew, page, overlay: false, hint: f.Seeds.Count == 0 ? "Select the features to mirror." : null);
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
        var main = page.Selection(m, "Main body", SelFilter.Body, "Main body; select one of its faces", 1);
        if (!string.IsNullOrEmpty(f.MainBody)) main.Items.Add(new SelItem { Kind = SelKind.Body, BodyId = f.MainBody });
        main.Changed += () => f.MainBody = main.Items.FirstOrDefault()?.BodyId ?? "";
        var tools = page.Selection(m, "Bodies to combine", SelFilter.Body, "Bodies to combine with the main body");
        foreach (var b in f.ToolBodies) tools.Items.Add(new SelItem { Kind = SelKind.Body, BodyId = b });
        tools.Changed += () => f.ToolBodies = tools.Items.Select(i => i.BodyId!).ToList();
        page.Info(m, "To create separate bodies, extrude with Merge result cleared.");
        BeginEdit(f, isNew, page, overlay: false);
    }
}
