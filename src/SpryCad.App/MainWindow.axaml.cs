using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Model;
using SpryCad.Core.Storage;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App;

public partial class MainWindow : Window
{
    /// <summary>The session of the active tab (see MainWindow.Tabs.cs).</summary>
    public DocumentSession Session { get; private set; } = new();
    public readonly SelectionSet Selection = new();
    readonly ModelMode _modelMode;
    SketchEditor? _sketch;
    PmPage? _page;
    readonly DispatcherTimer _previewTimer;
    readonly DispatcherTimer _autosave;
    bool _firstRebuild = true;

    public static MainWindow? Instance { get; private set; }
    readonly ViewSelector _viewSelector;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Icons.Scaling = Screens.Primary?.Scaling ?? 1; // (before any icon is made: at 100% the 16 px drawings are used where there are any)
        _modelMode = new ModelMode(Selection);
        Viewport.Mode = _modelMode;
        Viewport.ReverseWheel = UserSettings.ReverseWheel;
        Viewport.RotateAbout = UserSettings.RotateAbout;
        Viewport.Camera.Perspective = UserSettings.Perspective;
        Viewport.AntiAliasing = UserSettings.AntiAliasing;
        Viewport.SoftwareRenderer += () => Status("The view is drawn without the graphics card (a software renderer), slow with large models: see Tools > Options > Graphics.");
        Viewport.Overlay = Overlay;
        Viewport.Selection = Selection;
        Viewport.ContextRequested += OnViewportContext;
        Viewport.CameraChanged += UpdateTriadLabels;
        _viewSelector = new ViewSelector(Viewport, NormalTo);
        GraphicsArea.Children.Add(_viewSelector);
        // a click elsewhere in the graphics area puts the View Selector away (and still does what it does)
        Viewport.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_viewSelector.IsOpen && e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) _viewSelector.Close();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _modelMode.DoubleClicked += OnModelDoubleClick;
        _modelMode.PickHandler = PickToPage;

        _tab = AddTab(Session);
        UpdateTabHeader(_tab);
        Selection.Changed += OnSelectionChanged;
        SetupUnitsSwitch();

        Tree.SelectedChanged += OnTreeSelected;
        Tree.EditRequested += id => EditFeature(id);
        Tree.ContextRequested += OnTreeContext;
        Tree.SketchVisibilityToggled += id => SetSketchShown(id, null);
        Tree.HoverChanged += OnTreeHover;
        Tree.ExpandToggled += () => RebuildTree();
        Tree.RollbackTo += (id, after) => RollbackTo(id, after);
        Tree.RollbackRequested += t => { if (t == null) RollForward(); };
        Tree.MoveRequested += MoveFeature;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RunPreview(); };
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosave.Tick += (_, _) => AutoSave();
        _autosave.Start();

        BuildMenu();
        BuildQuickAccess();
        BuildCommandManager();
        InitPartsList();
        BuildTaskPane();
        BuildHud();
        BuildFlyoutTree();
        BuildTriadLabels();
        BuildPanelTabs();
        BuildGestures();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => StopArrowSpin();
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            if (Session.FilePath == null) Session.RequestRebuild();
            Viewport.StandardView("Trimetric", fit: false); // nothing to fit yet: keep the default zoom
            // work left unsaved when SpryCAD last closed without saving it (a crash, a power cut)
            Dispatcher.UIThread.Post(() => OfferRecovery(atStart: true), DispatcherPriority.Background);
        };
        // every part's local copy let go of (and its lock): what was not saved was chosen not to be
        Closed += (_, _) => { foreach (var t in _parts) t.Session.Close(); };
    }

    // ------------------------------------------------------------------------------------------
    // rebuild results -> scene

    void OnRebuilt()
    {
        UpdateUndoButtons();
        RefreshScene();
        // a preview's result: the tree shows the document and the real result, which a preview leaves as they were
        // (it rebuilds a copy; a change to the document rebuilds the tree itself)
        if (!(Session.PreviewActive && Asm == null && ReferenceEquals(_treeDoc, Session.Doc) && ReferenceEquals(_treeResult, Session.Result)))
            RebuildTree();
        var r = Session.Result;
        if (r != null)
        {
            // failures must not hide behind "Rebuilt N features": name the first problem, count the rest
            var bad = Session.Doc.Features.Where(f => !f.Suppressed && r.Status.TryGetValue(f.Id, out var st) && !st.Ok).ToList();
            var warned = Session.Doc.Features.Count(f => r.Status.TryGetValue(f.Id, out var st) && st.Ok && st.Warnings.Count > 0);
            var text = Session.PreviewActive ? "Preview"
                : r.Rebuilt == 0 ? "Up to date"
                : $"Rebuilt {r.Rebuilt} feature{(r.Rebuilt == 1 ? "" : "s")} in {r.Milliseconds:0} ms";
            if (!Session.PreviewActive && bad.Count > 0)
            {
                text = $"✖ {bad[0].Name}: {r.Status[bad[0].Id].Error}" + (bad.Count > 1 ? $"  (+{bad.Count - 1} more error{(bad.Count > 2 ? "s" : "")})" : "");
                StatusRebuild.Foreground = Palette.Danger;
            }
            else if (!Session.PreviewActive && warned > 0)
            {
                text += $"  ⚠ {warned} warning{(warned == 1 ? "" : "s")}";
                StatusRebuild.Foreground = Palette.Warning;
            }
            else StatusRebuild.Foreground = StatusNormalBrush;
            StatusRebuild.Text = text;
            ToolTip.SetTip(StatusRebuild, bad.Count + warned == 0 ? null : string.Join(Environment.NewLine,
                Session.Doc.Features.Where(f => r.Status.TryGetValue(f.Id, out var st) && (!st.Ok || st.Warnings.Count > 0))
                    .Select(f => $"{f.Name}: {r.Status[f.Id].Error ?? string.Join("; ", r.Status[f.Id].Warnings)}")));
            ShowTimeOnScreen();
        }
        if (_page != null && _edit != null && Session.PreviewActive && Session.PreviewResult is { } pr)
        {
            var st = pr.Status.GetValueOrDefault(_edit.Feature.Id);
            _page.Message = st?.Error ?? (st is { Warnings.Count: > 0 } ? string.Join("\n", st.Warnings) : _edit.Hint);
        }
        UpdateProblemEdges();
        if (_firstRebuild) { _firstRebuild = false; }
        if (_tab.FitAfterLoad is { } fitRev && Session.ResultRevision >= fitRev && !Session.PreviewActive)
        { _tab.FitAfterLoad = null; Viewport.StandardView("Trimetric", fit: true, animate: false); } // a part just opened or shown: at once
    }

    /// <summary>
    /// The time from the change to its result on screen, added to the rebuild's status once the frame showing it is
    /// drawn (the rebuild's own time is only part of it), with where it went in the status's tip.
    /// </summary>
    void ShowTimeOnScreen()
    {
        if (Session.Timing is not { Shown: 0 } timing) return;
        var session = Session;
        var text = StatusRebuild.Text;
        Viewport.AfterNextFrame(at =>
        {
            if (!ReferenceEquals(session.Timing, timing) || timing.Shown != 0) return;
            timing.Shown = at;
            if (!ReferenceEquals(session, Session) || StatusRebuild.Text != text) return; // another tab, or a newer status
            StatusRebuild.Text = $"{text}  ·  on screen in {timing.TotalMs:0} ms";
            var tip = ToolTip.GetTip(StatusRebuild) as string;
            ToolTip.SetTip(StatusRebuild, tip == null ? timing.Breakdown : tip + Environment.NewLine + Environment.NewLine + timing.Breakdown);
        });
    }

    /// <summary>
    /// Red edges where a feature failed: the feature being edited (live preview), else the feature selected in
    /// the tree.
    /// </summary>
    void UpdateProblemEdges()
    {
        FeatureStatus? st = null;
        if (_edit != null && Session.PreviewActive) st = Session.PreviewResult?.Status.GetValueOrDefault(_edit.Feature.Id);
        else if (Tree.SelectedId is { } id) st = Session.Result?.Status.GetValueOrDefault(id);
        Viewport.ProblemEdges = st is { Ok: false } ? st.ProblemEdges : Array.Empty<Vec3[]>();
        Viewport.Redraw();
    }

    static IBrush StatusNormalBrush => Palette.TextMuted;

    /// <summary>
    /// The status bar's units, as SolidWorks': clicked, a menu of the unit systems, the one picked the document's (a
    /// part's or an assembly's; undoable). Only how lengths show and are typed changes: the model is in millimetres.
    /// </summary>
    void SetupUnitsSwitch()
    {
        StatusUnits.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(StatusUnits, "Units: click to change");
        StatusUnits.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(StatusUnits).Properties.IsLeftButtonPressed) return;
            // (above the status bar, its right edge on the label: over the window, never off its side or bottom)
            var menu = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.TopEdgeAlignedRight };
            foreach (var (u, text) in new[] { (UnitSystem.MMGS, "MMGS (millimeter, gram, second)"), (UnitSystem.IPS, "IPS (inch, pound, second)") })
            {
                var mi = new MenuItem { Header = text, Icon = Units == u ? Icons.Make("ok", 14) : null };
                mi.Click += (_, _) => SetUnits(u);
                menu.Items.Add(mi);
            }
            menu.ShowAt(StatusUnits);
            e.Handled = true;
        };
    }

    void SetUnits(UnitSystem u)
    {
        if (Units == u) return;
        if (Asm is { } a) { a.BeginChange(); a.Model.Doc.Units = u; a.EndChange(); }
        else Session.Change(() => Session.Doc.Units = u);
        StatusUnits.Text = Units.ToString();
        RefreshScene();
        Status($"Units: {u}.");
    }

    void OnDocumentChanged()
    {
        Title = "SpryCAD - " + (_tab != null ? TitleOf(_tab) : Session.Title);
        StatusUnits.Text = Units.ToString();
        PmPage.CurrentUnits = () => Units;
        UpdateUndoButtons();
        Viewport.PartColor = new Rgba((float)Session.Doc.Color[0], (float)Session.Doc.Color[1], (float)Session.Doc.Color[2]);
        RebuildTree();
        RefreshRevisions();
    }

    // what the feature tree was last built from
    object? _treeDoc, _treeResult;

    void RebuildTree()
    {
        if (Asm is { } a) { _asmTree.Show(a); if (FlyoutHost.IsVisible) RebuildFlyoutTree(); return; }
        var t0 = TreeProfile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _treeDoc = Session.Doc; _treeResult = Session.Result;
        Tree.Swatches = FeatureSwatches();
        if (_flyTree != null) _flyTree.Swatches = Tree.Swatches;
        Tree.Rebuild(Session.Doc, TreeTitle(), Session.Result);
        if (FlyoutHost.IsVisible) RebuildFlyoutTree();
        if (TreeProfile) NoteTree(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    /// <summary>The part's name atop the tree; while it shows the display saved with it (just opened), saying so.</summary>
    string TreeTitle() => Session.Title.TrimEnd(' ', '*') + (Session.SavedDisplay != null ? " (as saved, rebuilding)" : "");

    // SPRYCAD_PROFILE_TREE: the feature tree's rebuilds (count, average and worst time) written to the log every 2 s
    static readonly bool TreeProfile = Environment.GetEnvironmentVariable("SPRYCAD_PROFILE_TREE") != null;
    int _trCalls; double _trMs, _trMax; long _trSince = System.Diagnostics.Stopwatch.GetTimestamp();

    void NoteTree(double ms)
    {
        _trCalls++; _trMs += ms; _trMax = Math.Max(_trMax, ms);
        var span = System.Diagnostics.Stopwatch.GetElapsedTime(_trSince).TotalSeconds;
        if (span < 2) return;
        App.Note($"tree: {_trCalls} rebuilds in {span:0.0} s, {_trMs / _trCalls:0.00} ms average, {_trMax:0.00} ms worst ({Session.Doc.Features.Count} features)");
        _trCalls = 0; _trMs = 0; _trMax = 0; _trSince = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Pushes the current rebuild result (or rolled back / preview state) into the viewport.</summary>
    /// <summary>
    /// Bend and bend tangent lines of the bodies shown flat (a body is its flat pattern's own solid: no feature after
    /// the Flat Pattern changed it), on the unfolded face; the viewport puts them on the face toward the viewer.
    /// </summary>
    static List<ViewportControl.BendLine> BendLinesOf(ModelState state)
    {
        var lines = new List<ViewportControl.BendLine>();
        foreach (var body in state.Bodies)
        {
            if (!state.Flats.TryGetValue(body.Id, out var flat) || !ReferenceEquals(body.Solid.Native, flat.Body)) continue;
            var fr = flat.Frame;
            Vec3 P(Vec2 p) => fr.ToWorld(p.X, p.Y, 0);
            foreach (var bend in flat.Bends)
            {
                ViewportControl.BendLine L(Vec2 a, Vec2 b, ViewportControl.BendLineKind kind) => new(P(a), P(b), kind, fr.Normal, flat.Thickness, bend.Allowance);
                lines.Add(L(bend.A, bend.B, bend.Up ? ViewportControl.BendLineKind.BendUp : ViewportControl.BendLineKind.BendDown));
                foreach (var (a, b) in bend.Tangents) lines.Add(L(a, b, ViewportControl.BendLineKind.Tangent));
            }
        }
        return lines;
    }

    public void RefreshScene()
    {
        UpdateColorByLabels();
        if (Asm != null) { RefreshAssemblyScene(); return; }
        Viewport.Instances = Array.Empty<MeshInstance>();
        var doc = Session.Doc;
        var result = Session.DisplayResult;
        var baseResult = Session.Result;
        ModelState state = result?.State ?? ModelState.Initial();
        IReadOnlyList<Body> preview = Array.Empty<Body>();

        if (_sketch != null && baseResult != null)
        {
            // editing a sketch: show the model as it was just before the sketch
            var idx = doc.IndexOf(_sketch.Feature.Id);
            state = StateBefore(baseResult, idx < 0 ? doc.ActiveCount : idx);
        }
        else if (_edit != null && _edit.OverlayPreview && baseResult != null)
        {
            var idx = _edit.IsNew ? doc.ActiveCount : doc.IndexOf(_edit.Feature.Id);
            state = StateBefore(baseResult, idx);
            // the preview overlay only when the feature being edited rebuilt: a failed one (e.g. a new chamfer with no
            // edges yet) leaves the model unchanged, and that whole model would show as the yellow overlay
            if (Session.PreviewActive && Session.PreviewResult is { } pr && pr.Status.GetValueOrDefault(_edit.Feature.Id) is { Ok: true })
                preview = pr.State.Bodies;
        }

        Viewport.Bodies = state.Bodies;
        Viewport.ColorByFeature = _colorByFeature
            ? FeatureColors.Of(doc).ToDictionary(kv => kv.Key, kv => new Rgba((float)kv.Value[0], (float)kv.Value[1], (float)kv.Value[2]))
            : null;
        Viewport.PreviewBodies = preview;
        Viewport.BendLines = BendLinesOf(state);
        Viewport.CosmeticThreads = state.Cosmetics.Values.ToList();
        // a slow part just opened: the display saved with it, until the rebuild is done (nothing to pick meanwhile)
        if (result == null && Session.SavedDisplay is { } saved)
        {
            var col = doc.Color;
            Viewport.Instances = new[] { new MeshInstance("__saved", saved.Mesh, System.Numerics.Matrix4x4.Identity, new Rgba((float)col[0], (float)col[1], (float)col[2])) };
            Viewport.CosmeticThreads = saved.Threads;
            if (_tab.FitAfterLoad != null) { _tab.FitAfterLoad = null; Viewport.StandardView("Trimetric", fit: true, animate: false); }
        }

        // planes: the origin planes (tinted as the triad's axes) when planes are shown; reference planes shown or
        // hidden as sketches are (shown while nothing uses them, unless set in the tree), and those the feature or
        // sketch being edited refers to; any of them while hovered or selected
        var b = state.Bounds;
        var half = b.IsEmpty ? 50 : Math.Max(30, b.Diagonal * 0.55);
        var used = UsedByActive();
        var editRefs = RefersTo(_edit?.Feature ?? _sketch?.Feature);
        var planes = new List<PlaneDisplay>();
        // the origin planes: as set by their eyes in the tree, else shown in an empty part (not while sketching or
        // making a plane there); and those what is being edited refers to
        var editingPlane = _edit?.Feature is PlaneFeature;
        foreach (var std in new[] { "Front", "Top", "Right" })
        {
            var shown = doc.SketchVisibility?.ContainsKey(std) == true ? doc.SketchShown(std, true) : state.Bodies.Count == 0 && _sketch == null && !editingPlane;
            planes.Add(new PlaneDisplay(std, null, Resolver.StdPlane(std), half, shown || editRefs.Contains(std), Tint: ViewportControl.OriginPlaneTint(std)));
        }
        PlaneDisplay Reference(PlaneFeature f, Frame fr)
        {
            var (lo, hi) = f.Size is { } sz ? (new Vec2(sz.MinX, sz.MinY), new Vec2(sz.MaxX, sz.MaxY)) : PlaneFit(f.Id, fr, state, half * 0.8);
            var shown = doc.SketchShown(f.Id, used.Contains(f.Id)) || editRefs.Contains(f.Id) || _edit?.Feature.Id == f.Id;
            return new PlaneDisplay(f.Name, f.Id, fr, half * 0.8, shown, lo, hi);
        }
        foreach (var f in doc.Features.Take(doc.ActiveCount).OfType<PlaneFeature>())
            if (state.Planes.TryGetValue(f.Id, out var fr) && !f.Suppressed && !(_edit?.Feature is PlaneFeature { } ep && ep.Id == f.Id))
                planes.Add(Reference(f, fr));
        // the plane being created/edited: its live preview (a new plane is not in the document yet), with its size handles
        if (_edit?.Feature is PlaneFeature pf && Session.PreviewActive && Session.PreviewResult?.State.Planes.TryGetValue(pf.Id, out var pfr) == true)
            planes.Add(Reference(pf, pfr));
        Viewport.Planes = planes;
        Viewport.HandlesOn = (_edit?.Feature as PlaneFeature)?.Id;
        Viewport.PlaneSized = (lo, hi) =>
        {
            if (_edit?.Feature is not PlaneFeature ef) return;
            ef.Size = new PlaneExtent { MinX = lo.X, MinY = lo.Y, MaxX = hi.X, MaxY = hi.Y };
            _page?.RaiseChanged();
            RefreshScene();
        };
        // reference axes, and the one being made (its live preview)
        var axes = new List<AxisDisplay>();
        foreach (var f in doc.Features.Take(doc.ActiveCount).OfType<AxisFeature>())
            if (state.Axes.TryGetValue(f.Id, out var ax) && !f.Suppressed && _edit?.Feature.Id != f.Id)
                axes.Add(new AxisDisplay(f.Name, f.Id, ax.A, ax.B, doc.SketchShown(f.Id, used.Contains(f.Id)) || editRefs.Contains(f.Id)));
        if (_edit?.Feature is AxisFeature af && Session.PreviewActive && Session.PreviewResult?.State.Axes.TryGetValue(af.Id, out var afx) == true)
            axes.Add(new AxisDisplay(af.Name, af.Id, afx.A, afx.B));
        Viewport.Axes = axes;
        // (all of them when the Planes view is on; otherwise each as above: editing a plane shows only it and its references)
        Viewport.ShowPlanes = _showPlanes || _planePickPage != null; // (and while a sketch's plane is being picked)

        // visible sketches: as shown/hidden in the tree, else while no active feature uses them; also the selected
        // ones, and those the feature being edited uses (to be picked again, e.g. a sweep's path or a helical
        // sweep's centerline as the axis)
        var consumed = doc.ConsumedByActive();
        var editing = _edit?.Feature.Consumes.ToHashSet() ?? new HashSet<string>();
        var selectedSketches = Selection.Items.Where(s => s.Kind == SelKind.Sketch).Select(s => s.FeatureId).ToHashSet();
        var sk = new List<SketchDisplay>();
        foreach (var (id, sr) in state.Sketches)
        {
            if (_sketch != null && id == _sketch.Feature.Id) continue;
            if (doc.Find(id) is HoleFeature) continue;
            if (!doc.SketchShown(id, consumed.Contains(id)) && !selectedSketches.Contains(id) && !editing.Contains(id)) continue;
            sk.Add(new SketchDisplay(id, sr));
        }
        Viewport.Sketches = sk;
        Viewport.Redraw();
    }

    /// <summary>
    /// Shows or hides a sketch, reference plane or axis in the graphics area (null: the other way round from now), e.g. a sketch a feature
    /// uses, to pick it again for a sweep; saved with the part, undoable, nothing rebuilt.
    /// </summary>
    void SetSketchShown(string id, bool? shown)
    {
        var doc = Session.Doc;
        bool consumed;
        if (id is "Front" or "Top" or "Right") consumed = Session.Result?.State.Bodies.Count > 0; // (an origin plane: hidden once there is a body)
        else if (doc.Find(id) is SketchFeature or PlaneFeature or AxisFeature) consumed = UsedByActive().Contains(id);
        else return;
        var show = shown ?? !doc.SketchShown(id, consumed);
        if (show == doc.SketchShown(id, consumed)) return;
        Session.Change(() => doc.SetSketchShown(id, show, consumed));
        Status($"{doc.Find(id)?.Name ?? id + " Plane"} {(show ? "shown" : "hidden")}");
    }

    /// <summary>Exact geometry of a selected face/edge for the status bar (like SolidWorks).</summary>
    string Describe(SelItem s)
    {
        var body = Viewport.Bodies.FirstOrDefault(b => b.Id == s.BodyId);
        if (body == null) return "";
        if (s.Kind == SelKind.Face)
        {
            var face = body.Solid.Faces.FirstOrDefault(f => f.Tag.Name == s.Name);
            if (face == null) return "";
            var es = face.Tag.Surface.IsCurved ? new ExactSurface(face.Tag.Surface) : new ExactSurface(SurfaceDef.PlaneDef, face.Plane);
            var area = body.Solid.Faces.Where(f => f.Tag.Name == s.Name).Sum(f => f.Area());
            return $"  —  {es.Describe()}, area ≈ {area:0.##}";
        }
        if (s.Kind == SelKind.Edge && s.EdgeIndex >= 0 && s.EdgeIndex < body.Solid.Topology.Edges.Count)
        {
            var e = body.Solid.Topology.Edges[s.EdgeIndex];
            if (e.IsLinear) return $"  —  Line, length {e.Length:0.###}";
            var surfs = new[] { e.FaceA, e.FaceB }.Select(n => body.Solid.Faces.FirstOrDefault(f => f.Tag.Name == n)?.Tag.Surface).ToList();
            var cyl = surfs.FirstOrDefault(x => x is { Kind: SurfaceKind.Cylinder or SurfaceKind.Cone or SurfaceKind.Torus });
            if (cyl != null && cyl.Kind == SurfaceKind.Cylinder) return $"  —  {(e.Closed ? "Circle" : "Arc")} Ø{2 * cyl.Radius:0.###}";
            return "  —  Curve";
        }
        return "";
    }

    static ModelState StateBefore(RegenResult r, int index)
    {
        if (index <= 0 || r.States.Count == 0) return ModelState.Initial();
        return r.States[Math.Min(index, r.States.Count) - 1];
    }

    // ------------------------------------------------------------------------------------------
    // selection / tree sync

    void OnSelectionChanged()
    {
        UpdateBreadcrumbs();
        var s = Selection.First;
        if (_sketch == null)
        {
            // the tree and its flyout copy alike
            foreach (var t in new[] { Tree, _flyTree })
            {
                if (t == null) continue;
                if (s == null) t.Select(null);
                else if (s.Kind is SelKind.Sketch or SelKind.Feature) t.Select(s.FeatureId);
                else if (s.Kind == SelKind.Plane) t.Select(s.FeatureId ?? s.Name);
                // a picked face/edge shows which feature made it
                t.Relate(s is { Kind: SelKind.Face or SelKind.Edge } ? s.OwningFeatureId : null);
            }
        }
        Status(s == null ? "Ready" : Selection.Count == 1 ? s.Label(Session.Doc) + " selected" + Describe(s) : $"{Selection.Count} items selected");
        RefreshScene();
    }

    void OnTreeSelected(string? id)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateProblemEdges); // after the tree selection settles
        if (id == null) return;
        if (_page?.ActiveBox != null && (_page.ActiveBox.Filter & SelFilter.Feature) != 0 && Session.Doc.Find(id) is { } feat)
        {
            _page.HandlePick(new SelItem { Kind = SelKind.Feature, FeatureId = feat.Id });
            RefreshScene();
            return;
        }
        if (_page?.ActiveBox != null && (_page.ActiveBox.Filter & SelFilter.Plane) != 0 && id is "Front" or "Top" or "Right")
        {
            _page.HandlePick(new SelItem { Kind = SelKind.Plane, Name = id, Frame = Resolver.StdPlane(id) });
            return;
        }
        // an origin axis into a box that takes axes or lines
        if (_page?.ActiveBox != null && WorldAxisItem(id) is { } wax && PmPage.Matches(_page.ActiveBox.Filter, wax))
        {
            _page.HandlePick(wax);
            RefreshScene();
            return;
        }
        if (_page?.ActiveBox != null && (_page.ActiveBox.Filter & SelFilter.Sketch) != 0 && Session.Doc.Find(id) is SketchFeature sf)
        {
            _page.HandlePick(new SelItem { Kind = SelKind.Sketch, FeatureId = sf.Id });
            return;
        }
        if (_sketch != null) return;
        switch (id)
        {
            case "Front":
            case "Top":
            case "Right":
                Selection.Set(new SelItem { Kind = SelKind.Plane, Name = id, Frame = Resolver.StdPlane(id) });
                break;
            case "AxisX":
            case "AxisY":
            case "AxisZ":
                Selection.Set(WorldAxisItem(id)!);
                break;
            case "Origin":
            case "OriginPoint":
                Selection.Set(new SelItem { Kind = SelKind.Origin });
                break;
            case "__part":
                Selection.Clear();
                break;
            default:
                var f = Session.Doc.Find(id);
                if (f is SketchFeature) Selection.Set(new SelItem { Kind = SelKind.Sketch, FeatureId = id, Frame = Session.Result?.State.Sketches.GetValueOrDefault(id)?.Frame });
                else if (f is PlaneFeature) Selection.Set(new SelItem { Kind = SelKind.Plane, Name = f.Name, FeatureId = id, Frame = Session.Result?.State.Planes.GetValueOrDefault(id) });
                else if (f is AxisFeature) Selection.Set(new SelItem { Kind = SelKind.Axis, Name = f.Name, FeatureId = id });
                else if (f != null) Selection.Set(new SelItem { Kind = SelKind.Feature, FeatureId = id });
                break;
        }
    }

    /// <summary>
    /// An axis of the origin as a pick, from its tree row ("AxisX") or its letter ("X"); drawn through the origin as
    /// far as the origin planes reach. Null for anything else.
    /// </summary>
    SelItem? WorldAxisItem(string id)
    {
        var a = id.StartsWith("Axis") ? id[4..] : id;
        if (a is not ("X" or "Y" or "Z")) return null;
        var b = Session.Result?.State.Bounds ?? default;
        var half = b.IsEmpty ? 50 : Math.Max(30, b.Diagonal * 0.55);
        return new SelItem { Kind = SelKind.Axis, Name = a, Point = Vec3.Zero, Normal = a == "X" ? Vec3.UnitX : a == "Y" ? Vec3.UnitY : Vec3.UnitZ, AxisHalfLength = half };
    }

    void OnTreeHover(string id)
    {
        if (string.IsNullOrEmpty(id)) { Viewport.Hover = null; Viewport.Redraw(); return; }
        Viewport.Hover = id switch
        {
            "Front" or "Top" or "Right" => new SelItem { Kind = SelKind.Plane, Name = id },
            "AxisX" or "AxisY" or "AxisZ" => WorldAxisItem(id),
            "Origin" or "OriginPoint" => new SelItem { Kind = SelKind.Origin },
            _ => Session.Doc.Find(id) switch
            {
                SketchFeature => new SelItem { Kind = SelKind.Sketch, FeatureId = id },
                PlaneFeature pf => new SelItem { Kind = SelKind.Plane, Name = pf.Name, FeatureId = id },
                AxisFeature axf => new SelItem { Kind = SelKind.Axis, Name = axf.Name, FeatureId = id },
                Feature => new SelItem { Kind = SelKind.Feature, FeatureId = id },
                _ => null,
            },
        };
        Viewport.Redraw();
    }

    // ------------------------------------------------------------------------------------------
    // keyboard

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (UI.Card.IsOpen) return; // a question is up (its card has the keys): no shortcuts meanwhile
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var inText = e.Source is TextBox;
        // keys typed into an input control (combo, check box, button, list) are that control's, not view shortcuts
        var inControl = inText || e.Source is ComboBox or ComboBoxItem or CheckBox or Button or ListBox or ListBoxItem or Slider or MenuItem;
        if (_viewSelector.IsOpen && e.Key == Key.Escape) { _viewSelector.Close(restore: true); e.Handled = true; return; }
        if (ctrl)
        {
            bool handled = true;
            switch (e.Key)
            {
                case Key.N: _ = NewFile(); break;
                case Key.O: _ = OpenFileDialog(); break;
                case Key.S: if (shift) _ = SaveAs(); else _ = Save(); break;
                case Key.Z: if (!inText) Undo(); else handled = false; break;
                case Key.Y: if (!inText) Redo(); else handled = false; break;
                case Key.B: Rebuild(); break;
                case Key.K: OpenCommandPalette(atCursor: false); break; // (the command palette, in any mode)
                case Key.Q: Rebuild(force: true); break;
                case Key.W or Key.F4: _ = CloseTab(_tab); break;
                case Key.Tab: _ = CycleTab(shift ? -1 : 1); break;
                case Key.E: ShowPartsList(); break;
                case Key.D1: Viewport.StandardView("Front"); break;
                case Key.D2: Viewport.StandardView("Back"); break;
                case Key.D3: Viewport.StandardView("Left"); break;
                case Key.D4: Viewport.StandardView("Right"); break;
                case Key.D5: Viewport.StandardView("Top"); break;
                case Key.D6: Viewport.StandardView("Bottom"); break;
                case Key.D7: Viewport.StandardView("Isometric"); break;
                case Key.D8: NormalTo(); break;
                case Key.Left: if (!inText) { Viewport.Camera.Pan(40, 0); Viewport.Redraw(); } else handled = false; break;
                case Key.Right: if (!inText) { Viewport.Camera.Pan(-40, 0); Viewport.Redraw(); } else handled = false; break;
                case Key.Up: if (!inText) { Viewport.Camera.Pan(0, 40); Viewport.Redraw(); } else handled = false; break;
                case Key.Down: if (!inText) { Viewport.Camera.Pan(0, -40); Viewport.Redraw(); } else handled = false; break;
                default: handled = false; break;
            }
            e.Handled = handled;
            return;
        }
        // PropertyManager: Enter = OK (from any field), Esc = cancel the page, as in SolidWorks
        if (_page != null && _sketch == null)
        {
            if (e.Key == Key.Escape)
            {
                if (_edit != null) CancelEdit(); else _page.OnCancel?.Invoke();
                e.Handled = true; return;
            }
            // (a feature being edited, or an assembly's page: Insert Components, Mate, ...)
            if (e.Key == Key.Enter && (_edit != null || Asm != null) && !(e.Source is TextBox { AcceptsReturn: true }))
            {
                // let a text field commit its value first (it parses on change), then accept the page
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _page?.OnOk?.Invoke());
                e.Handled = true; return;
            }
        }
        // Convert Entities (a page within the sketch): Enter converts, Esc goes back to the sketch
        if (Converting && e.Key is Key.Enter or Key.Escape && !(e.Source is TextBox))
        {
            if (e.Key == Key.Enter) ConvertOk(); else EndConvert();
            e.Handled = true; return;
        }
        // Mirror and Offset pages: Enter is OK, Esc goes back to the sketch
        if (InEntityPage && e.Key is Key.Enter or Key.Escape && !(e.Source is TextBox))
        {
            if (e.Key == Key.Enter) _entityPage!.OnOk?.Invoke(); else EndEntityPage();
            e.Handled = true; return;
        }
        if (inControl) return;
        if (_sketch != null && _sketch.IsEditingDimension) return;
        if (_sketch != null && _sketch.KeyDown(Viewport, e.Key, e.KeyModifiers)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                // (in an assembly: the selected component or mate too)
                if (_sketch == null)
                {
                    Selection.Clear();
                    if (Asm is { Selected: not null } or { SelectedMate: not null }) SelectMate(null);
                    if (_asmOriginSel != null) { _asmOriginSel = null; RefreshAssemblyScene(); }
                    if (_normalToPick) { _normalToPick = false; RefreshAssemblyScene(); Status("Normal To cancelled."); }
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (_sketch == null && _page == null) { DeleteSelected(); e.Handled = true; }
                break;
            case Key.F2 when Asm is { SelectedMate: { } mateId } && _mating == null:
                OnMateCommand(mateId, "rename"); e.Handled = true;
                break;
            case Key.F2 when Asm is { Selected: { } compPath } && _page == null:
                _ = RenameComponent(compPath); e.Handled = true;
                break;
            case Key.F2:
                // rename the feature selected in the tree (or owning the selected face)
                if (_sketch == null && _page == null && Session.Doc.Find(Tree.SelectedId ?? Selection.First?.FeatureId ?? "") is { } rf)
                { _ = RenameFeature(rf); e.Handled = true; }
                break;
            case Key.S when _sketch == null && _page == null:
                // (the command palette at the cursor; in a sketch S is the spline, so there Ctrl+K)
                OpenCommandPalette(atCursor: true); e.Handled = true;
                break;
            case Key.Enter when _sketch == null && _page == null && _edit == null:
                if (RepeatLastCommand()) e.Handled = true;
                break;
            case Key.F:
                Viewport.ZoomToFit(); e.Handled = true;
                break;
            case Key.Space:
                _viewSelector.Toggle(); e.Handled = true;
                break;
            case Key.Left when alt: Viewport.Camera.Roll(-0.26f); Viewport.Redraw(); e.Handled = true; break;
            case Key.Right when alt: Viewport.Camera.Roll(0.26f); Viewport.Redraw(); e.Handled = true; break;
            case Key.Left or Key.Right or Key.Up or Key.Down: ArrowDown(e.Key, shift); e.Handled = true; break;
        }
    }

    // arrow keys: those held down, and those held long enough to repeat (spinning)
    readonly HashSet<Key> _arrowsDown = new(), _arrowsSpinning = new();
    bool _spinFast;

    static (int yaw, int pitch) ArrowDir(Key k) => k switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), _ => (0, 1) };

    /// <summary>
    /// A tap rotates a 15° (Shift: 90°) step. Held down, once the key repeats, the view spins steadily until it is
    /// released (stepping on every repeat was jerky: each one restarted the step animation midway).
    /// </summary>
    void ArrowDown(Key k, bool shift)
    {
        if (_arrowsDown.Add(k))
        {
            var (yaw, pitch) = ArrowDir(k);
            var step = shift ? 90 : 15;
            Viewport.Rotate(yaw * step, pitch * step);
            return;
        }
        _spinFast = shift;
        if (_arrowsSpinning.Add(k)) UpdateArrowSpin();
    }

    void UpdateArrowSpin()
    {
        float yaw = 0, pitch = 0;
        foreach (var k in _arrowsSpinning) { var (y, p) = ArrowDir(k); yaw += y; pitch += p; }
        var rate = _spinFast ? 180f : 60f; // degrees per second
        Viewport.SetSpin(yaw * rate, pitch * rate);
    }

    void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_arrowsDown.Remove(e.Key) && _arrowsSpinning.Remove(e.Key)) UpdateArrowSpin();
    }

    /// <summary>Stops an arrow-key spin whose key-up will never arrive (the window lost focus).</summary>
    void StopArrowSpin()
    {
        _arrowsDown.Clear(); _arrowsSpinning.Clear();
        Viewport.SetSpin(0, 0);
    }

    // ------------------------------------------------------------------------------------------
    // status / helpers

    public void Status(string text) => StatusText.Text = text;

    /// <summary>
    /// The units values are shown and typed in: the active assembly's or part's. Lengths are millimetres whatever they are.
    /// </summary>
    UnitSystem Units => Asm?.Model.Doc.Units ?? Session.Doc.Units;

    void ShowPage(PmPage? page)
    {
        if (page != null) HideBreadcrumbs(); // (a command started)
        // the Measure tool's drawing belongs to its page
        if (_measurePage != null && page != _measurePage) EndMeasure();
        // the Section View page replaced by another: the section kept as it is, its arrow gone
        if (_sectionPage != null && page != _sectionPage) { _sectionPage = null; _sectionOffsetBox = null; Viewport.SectionArrow = false; }
        // the plane-pick page closed (cancelled, or replaced by another command): the planes go back to as set
        if (_planePickPage != null && page != _planePickPage)
        {
            _planePickPage = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshScene);
        }
        // pattern instance markers belong to the page that showed them
        if (page != _page) { _instanceMarkers?.Dispose(); _instanceMarkers = null; }
        _page = page;
        if (page == null)
        {
            PmHost.Content = null;
            _modelMode.Filter = SelFilter.Any;
            Viewport.Emphasis = Array.Empty<SelItem>();
            UpdatePanelTabs();
            return;
        }
        PmHost.Content = page.View;
        page.Lookup = Session.Lookup;
        page.ActiveBoxChanged += () => UpdateFilterFromPage();
        page.Changed += () => UpdateEmphasis();
        foreach (var b in page.Boxes) b.LabelOf = i => i.Label(Session.Doc);
        foreach (var b in page.Boxes) b.Refresh();
        UpdateFilterFromPage();
        UpdateEmphasis();
        UpdatePanelTabs();
    }

    void UpdateFilterFromPage()
    {
        if (_page?.ActiveBox is { } box) _modelMode.Filter = box.Filter.HasFlag(SelFilter.Feature) ? (box.Filter | SelFilter.Face) : box.Filter;
        else _modelMode.Filter = SelFilter.Any;
        UpdateEmphasis();
    }

    void UpdateEmphasis()
    {
        if (_page == null) { Viewport.Emphasis = Array.Empty<SelItem>(); return; }
        Viewport.Emphasis = _page.Boxes.SelectMany(b => b.Items).ToList();
        Viewport.Redraw();
    }

    /// <summary>Viewport picks go to the active PropertyManager selection box first.</summary>
    bool PickToPage(SelItem item)
    {
        if (_page?.ActiveBox == null) return false;
        if ((_page.ActiveBox.Filter & SelFilter.Feature) != 0 && item.Kind == SelKind.Face && (_page.ActiveBox.Filter & SelFilter.Face) == 0)
        {
            var fid = item.OwningFeatureId;
            if (fid == null || Session.Doc.Find(fid) == null) return true;
            item = new SelItem { Kind = SelKind.Feature, FeatureId = fid };
        }
        var ok = _page.HandlePick(item);
        if (ok) UpdateEmphasis();
        return true;
    }

    public static void ShowErrorStatic(string msg) => Instance?.Status("⚠ " + msg);

    /// <summary>
    /// Autosave: every part with unsaved changes as its local copy's working revision (Core.Storage.WorkingCopy),
    /// offered back if SpryCAD closes without saving it. Also renews the parts' locks on their files now and then.
    /// </summary>
    // an autosave still writing (the next tick waits for it)
    Task? _autosaving;
    DateTime _locksRefreshed = DateTime.UtcNow;

    void AutoSave()
    {
        if (DateTime.UtcNow - _locksRefreshed > WorkingCopy.LockRefresh)
        {
            _locksRefreshed = DateTime.UtcNow;
            foreach (var t in _parts) t.Session.RefreshLock();
        }
        if (_autosaving is { IsCompleted: false }) return;
        // (an assembly tab's part session is a blank stand-in, never a part to keep)
        var dirty = _parts.Where(t => t.Asm == null && t.Session.Dirty && !t.Session.IsPending).ToList();
        if (dirty.Count == 0) return;
        // copies of the parts, written off the UI thread: compressing an imported model's file takes long enough to be
        // felt, and the part being edited is not touched by it
        var copies = new List<(DocumentSession session, CadDocument doc)>();
        try { foreach (var t in dirty) copies.Add((t.Session, t.Session.Doc.Clone())); }
        catch { return; /* best effort */ }
        _autosaving = Task.Run(() =>
        {
            foreach (var (session, doc) in copies)
            {
                try { session.SaveWorking(doc); }
                catch (Exception ex) { App.Log(ex, "Autosave"); }
            }
        });
    }

    /// <summary>
    /// Offers back the parts SpryCAD closed without saving (at start, and Recover Autosave): restored as they were last
    /// autosaved, unsaved still; or discarded; or kept to be offered again.
    /// </summary>
    async void OfferRecovery(bool atStart)
    {
        List<Recoverable> found;
        try { found = WorkingCopy.FindRecoverable(); }
        catch (Exception ex) { App.Log(ex, "Recovery"); return; }
        if (found.Count == 0) { if (!atStart) Status("Nothing to recover: no part was left unsaved."); return; }
        var one = found.Count == 1;
        var list = string.Join("\n", found.Select(r => $"    {r.Name}   (autosaved {r.SavedUtc.ToLocalTime():g})"));
        var choice = await Dialogs.YesNoCancel(this, "Recover Unsaved Work",
            $"SpryCAD closed without saving {(one ? "this part" : "these parts")}:\n\n{list}\n\n" +
            $"Restore {(one ? "it" : "them")} as last autosaved? (Cancel keeps {(one ? "it" : "them")} to decide later.)", "Restore", "Discard");
        if (choice == null) return;
        if (choice == false) { foreach (var r in found) WorkingCopy.Discard(r); Status("Discarded the unsaved work."); return; }
        int n = 0;
        foreach (var r in found) { if (!await RecoverInTab(r)) break; n++; }
        if (n > 0) Status(n == 1 ? "Restored the unsaved part: save it to keep it." : $"Restored {n} unsaved parts: save them to keep them.");
    }

    async Task<bool> RecoverInTab(Recoverable r)
    {
        if (IsUntouched(_tab)) { if (!await ResolvePendingEdit()) return false; LeaveModes(); }
        else if (await NewTab() is null) return false;
        try
        {
            Viewport.ClearPivot();
            if (r.Source == null) Session.UntitledName = r.Name;
            Session.Recover(r);
            _tab.FitAfterLoad = Session.RequestedRevision;
            UpdateTabHeader(_tab);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex, "Recover " + r.WorkingPath);
            await Dialogs.Message(this, "Recover failed", ex.Message);
            return false;
        }
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if ((!HasPendingEdit && !_parts.Any(DirtyOf)) || _closeConfirmed) return;
        e.Cancel = true;
        // the pending edit (active tab) first, then each part with unsaved changes, shown as it is asked about
        if (!await ResolvePendingEdit()) return;
        foreach (var t in _parts.Where(DirtyOf).ToList())
        {
            if (!await SwitchTo(t)) return;
            var r = await Dialogs.YesNoCancel(this, "SpryCAD", $"Save changes to {TitleOf(t).TrimEnd(' ', '*')}?");
            if (r == null) return;
            if (r == true && !await Save()) return;
        }
        _closeConfirmed = true;
        Close();
    }
    bool _closeConfirmed;

    // ------------------------------------------------------------------------------------------
    // triad labels

    readonly TextBlock[] _triadLabels = new TextBlock[3];

    void BuildTriadLabels()
    {
        var colors = new[] { Brushes.Firebrick, Brushes.ForestGreen, Brushes.RoyalBlue };
        var names = new[] { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            _triadLabels[i] = new TextBlock { Text = names[i], Foreground = colors[i], FontWeight = FontWeight.Bold, FontSize = 11, IsHitTestVisible = false };
            Overlay.Children.Add(_triadLabels[i]);
        }
        Overlay.Children.Add(_planeLabel);
        UpdateTriadLabels();
    }

    // the name of the plane hovered or selected, small along its side (as the tree names it)
    readonly TextBlock _planeLabel = new()
    {
        FontSize = 12, FontWeight = FontWeight.SemiBold, IsHitTestVisible = false, IsVisible = false,
        RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
    };

    void UpdatePlaneLabel()
    {
        var hover = Viewport.Hover is { Kind: SelKind.Plane } h ? h : null;
        var it = hover ?? Selection.Items.FirstOrDefault(s => s.Kind == SelKind.Plane);
        var name = it == null ? null : it.FeatureId != null ? Session.Doc.Find(it.FeatureId)?.Name : it.Name + " Plane";
        if (it == null || name == null || Viewport.PlaneLabelPlacement(it.Name ?? "", it.FeatureId) is not { } place) { _planeLabel.IsVisible = false; return; }
        // laid in the plane: along a side, foreshortened and skewed with it (too flat to read: none)
        var m = place.m;
        if (Math.Abs(m.M11 * m.M22 - m.M12 * m.M21) < 0.15) { _planeLabel.IsVisible = false; return; }
        _planeLabel.Text = name;
        _planeLabel.Foreground = hover != null ? Brushes.DarkOrange : Palette.Accent;
        _planeLabel.RenderTransform = new MatrixTransform(m);
        Canvas.SetLeft(_planeLabel, place.at.X);
        Canvas.SetTop(_planeLabel, place.at.Y);
        _planeLabel.IsVisible = true;
    }

    void UpdateTriadLabels()
    {
        var axes = new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ };
        for (int i = 0; i < 3; i++)
        {
            var p = Viewport.TriadLabel(axes[i]);
            Canvas.SetLeft(_triadLabels[i], p.X - 4); Canvas.SetTop(_triadLabels[i], p.Y - 8);
        }
        UpdatePlaneLabel();
        if (_sketch != null)
        {
            var c = _sketch.Status;
            StatusSketch.Text = c.Status switch
            {
                Core.Sketching.SketchStatus.FullyDefined => "Fully Defined",
                Core.Sketching.SketchStatus.OverDefined => "Over Defined",
                _ => "Under Defined",
            };
        }
    }
}
