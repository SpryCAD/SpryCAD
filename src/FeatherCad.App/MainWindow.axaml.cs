using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FeatherCad.App.Session;
using FeatherCad.App.Sketcher;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.App;

public partial class MainWindow : Window
{
    public readonly DocumentSession Session = new();
    public readonly SelectionSet Selection = new();
    readonly ModelMode _modelMode;
    SketchEditor? _sketch;
    PmPage? _page;
    readonly DispatcherTimer _previewTimer;
    readonly DispatcherTimer _autosave;
    bool _firstRebuild = true;

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        _modelMode = new ModelMode(Selection);
        Viewport.Mode = _modelMode;
        UserSettings.Load();
        Viewport.ReverseWheel = UserSettings.ReverseWheel;
        Viewport.Camera.Perspective = UserSettings.Perspective;
        Viewport.Overlay = Overlay;
        Viewport.Selection = Selection;
        Viewport.ContextRequested += OnViewportContext;
        Viewport.CameraChanged += UpdateTriadLabels;
        _modelMode.DoubleClicked += OnModelDoubleClick;
        _modelMode.PickHandler = PickToPage;

        Session.Rebuilt += OnRebuilt;
        Session.DocumentChanged += OnDocumentChanged;
        Session.BusyChanged += busy => BusyText.IsVisible = busy;
        Selection.Changed += OnSelectionChanged;

        Tree.SelectedChanged += OnTreeSelected;
        Tree.EditRequested += id => EditFeature(id);
        Tree.ContextRequested += OnTreeContext;
        Tree.HoverChanged += OnTreeHover;
        Tree.ExpandToggled += () => RebuildTree();
        Tree.RollbackTo += (id, after) => RollbackTo(id, after);
        Tree.RollbackRequested += t => { if (t == null) RollForward(); };

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RunPreview(); };
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosave.Tick += (_, _) => AutoSave();
        _autosave.Start();

        BuildMenu();
        BuildQuickAccess();
        BuildCommandManager();
        BuildHud();
        BuildTriadLabels();
        BuildPanelTabs();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            if (Session.FilePath == null) Session.RequestRebuild();
            Viewport.StandardView("Trimetric");
        };
    }

    // ------------------------------------------------------------------------------------------
    // rebuild results -> scene

    void OnRebuilt()
    {
        UpdateUndoButtons();
        RefreshScene();
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
                StatusRebuild.Foreground = Brushes.Firebrick;
            }
            else if (!Session.PreviewActive && warned > 0)
            {
                text += $"  ⚠ {warned} warning{(warned == 1 ? "" : "s")}";
                StatusRebuild.Foreground = Brushes.DarkGoldenrod;
            }
            else StatusRebuild.Foreground = StatusNormalBrush;
            StatusRebuild.Text = text;
            ToolTip.SetTip(StatusRebuild, bad.Count + warned == 0 ? null : string.Join(Environment.NewLine,
                Session.Doc.Features.Where(f => r.Status.TryGetValue(f.Id, out var st) && (!st.Ok || st.Warnings.Count > 0))
                    .Select(f => $"{f.Name}: {r.Status[f.Id].Error ?? string.Join("; ", r.Status[f.Id].Warnings)}")));
        }
        if (_page != null && _edit != null && Session.PreviewActive && Session.PreviewResult is { } pr)
        {
            var st = pr.Status.GetValueOrDefault(_edit.Feature.Id);
            _page.Message = st?.Error ?? (st is { Warnings.Count: > 0 } ? string.Join("\n", st.Warnings) : _edit.Hint);
        }
        UpdateProblemEdges();
        if (_firstRebuild) { _firstRebuild = false; }
        if (_fitAfterLoad && !Session.PreviewActive) { _fitAfterLoad = false; Viewport.StandardView("Trimetric", fit: true); }
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

    static readonly IBrush StatusNormalBrush = new SolidColorBrush(Color.Parse("#FF6A778A"));

    void OnDocumentChanged()
    {
        Title = "FeatherCAD - " + Session.Title;
        StatusUnits.Text = Session.Doc.Units.ToString();
        PmPage.LengthUnit = UnitLabel;
        UpdateUndoButtons();
        Viewport.PartColor = new Rgba((float)Session.Doc.Color[0], (float)Session.Doc.Color[1], (float)Session.Doc.Color[2]);
        RebuildTree();
    }

    void RebuildTree() => Tree.Rebuild(Session.Doc, Session.Title.TrimEnd(' ', '*'), Session.Result);

    /// <summary>Pushes the current rebuild result (or rolled back / preview state) into the viewport.</summary>
    public void RefreshScene()
    {
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
            if (Session.PreviewActive && Session.PreviewResult is { } pr) preview = pr.State.Bodies;
        }

        Viewport.Bodies = state.Bodies;
        Viewport.PreviewBodies = preview;

        // planes
        var b = state.Bounds;
        var half = b.IsEmpty ? 50 : Math.Max(30, b.Diagonal * 0.55);
        var planes = new List<PlaneDisplay>
        {
            new("Front", null, Frame.Front, half),
            new("Top", null, Frame.Top, half),
            new("Right", null, Frame.Right, half),
        };
        foreach (var f in doc.Features.Take(doc.ActiveCount).OfType<PlaneFeature>())
            if (state.Planes.TryGetValue(f.Id, out var fr) && !f.Suppressed && !(_edit?.Feature is PlaneFeature { } ep && ep.Id == f.Id))
                planes.Add(new PlaneDisplay(f.Name, f.Id, fr, half * 0.8));
        // the plane being created/edited: its live preview (a new plane is not in the document yet)
        var editingPlane = _edit?.Feature is PlaneFeature;
        if (_edit?.Feature is PlaneFeature pf && Session.PreviewActive && Session.PreviewResult?.State.Planes.TryGetValue(pf.Id, out var pfr) == true)
            planes.Add(new PlaneDisplay(pf.Name, pf.Id, pfr, half * 0.8));
        Viewport.Planes = planes;
        Viewport.ShowPlanes = _showPlanes || editingPlane || state.Bodies.Count == 0 && _sketch == null;

        // visible sketches: not consumed by an active feature, or selected
        var consumed = new HashSet<string>();
        foreach (var f in doc.Features.Take(doc.ActiveCount)) if (!f.Suppressed) foreach (var c in f.Consumes) consumed.Add(c);
        var selectedSketches = Selection.Items.Where(s => s.Kind == SelKind.Sketch).Select(s => s.FeatureId).ToHashSet();
        var sk = new List<SketchDisplay>();
        foreach (var (id, sr) in state.Sketches)
        {
            if (_sketch != null && id == _sketch.Feature.Id) continue;
            if (consumed.Contains(id) && !selectedSketches.Contains(id)) continue;
            if (doc.Find(id) is HoleFeature) continue;
            sk.Add(new SketchDisplay(id, sr));
        }
        Viewport.Sketches = sk;
        Viewport.Redraw();
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
        var s = Selection.First;
        if (_sketch == null)
        {
            if (s == null) Tree.Select(null);
            else if (s.Kind is SelKind.Sketch or SelKind.Feature) Tree.Select(s.FeatureId);
            else if (s.Kind == SelKind.Plane) Tree.Select(s.FeatureId ?? s.Name);
            // a picked face/edge shows which feature made it
            Tree.Relate(s is { Kind: SelKind.Face or SelKind.Edge } ? s.OwningFeatureId : null);
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
            case "Origin":
            case "__part":
                Selection.Clear();
                break;
            default:
                var f = Session.Doc.Find(id);
                if (f is SketchFeature) Selection.Set(new SelItem { Kind = SelKind.Sketch, FeatureId = id, Frame = Session.Result?.State.Sketches.GetValueOrDefault(id)?.Frame });
                else if (f is PlaneFeature) Selection.Set(new SelItem { Kind = SelKind.Plane, Name = f.Name, FeatureId = id, Frame = Session.Result?.State.Planes.GetValueOrDefault(id) });
                else if (f != null) Selection.Set(new SelItem { Kind = SelKind.Feature, FeatureId = id });
                break;
        }
    }

    void OnTreeHover(string id)
    {
        if (string.IsNullOrEmpty(id)) { Viewport.Hover = null; Viewport.Redraw(); return; }
        Viewport.Hover = id switch
        {
            "Front" or "Top" or "Right" => new SelItem { Kind = SelKind.Plane, Name = id },
            _ => Session.Doc.Find(id) switch
            {
                SketchFeature => new SelItem { Kind = SelKind.Sketch, FeatureId = id },
                PlaneFeature pf => new SelItem { Kind = SelKind.Plane, Name = pf.Name, FeatureId = id },
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
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var inText = e.Source is TextBox;
        // keys typed into an input control (combo, check box, button, list) are that control's, not view shortcuts
        var inControl = inText || e.Source is ComboBox or ComboBoxItem or CheckBox or Button or ListBox or ListBoxItem or Slider or MenuItem;
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
                case Key.B: Session.RequestRebuild(); break;
                case Key.Q: Session.ForceRebuild(); break;
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
            if (e.Key == Key.Enter && _edit != null && !(e.Source is TextBox { AcceptsReturn: true }))
            {
                // let a text field commit its value first (it parses on change), then accept the page
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _page?.OnOk?.Invoke());
                e.Handled = true; return;
            }
        }
        if (inControl) return;
        if (_sketch != null && _sketch.IsEditingDimension) return;
        if (_sketch != null && _sketch.KeyDown(Viewport, e.Key, e.KeyModifiers)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                if (_sketch == null) { Selection.Clear(); e.Handled = true; }
                break;
            case Key.Delete:
                if (_sketch == null && _page == null) { DeleteSelected(); e.Handled = true; }
                break;
            case Key.F2:
                // rename the feature selected in the tree (or owning the selected face)
                if (_sketch == null && _page == null && Session.Doc.Find(Tree.SelectedId ?? Selection.First?.FeatureId ?? "") is { } rf)
                { _ = RenameFeature(rf); e.Handled = true; }
                break;
            case Key.F:
                Viewport.ZoomToFit(); e.Handled = true;
                break;
            case Key.Space:
                ShowOrientationFlyout(); e.Handled = true;
                break;
            case Key.Left: Viewport.Rotate(alt ? 0 : shift ? -90 : -15, 0); if (alt) { Viewport.Camera.Roll(-0.26f); Viewport.Redraw(); } e.Handled = true; break;
            case Key.Right: Viewport.Rotate(alt ? 0 : shift ? 90 : 15, 0); if (alt) { Viewport.Camera.Roll(0.26f); Viewport.Redraw(); } e.Handled = true; break;
            case Key.Up: Viewport.Rotate(0, shift ? -90 : -15); e.Handled = true; break;
            case Key.Down: Viewport.Rotate(0, shift ? 90 : 15); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------------------------------
    // status / helpers

    public void Status(string text) => StatusText.Text = text;

    /// <summary>Length unit label of the document ("mm" or "in").</summary>
    string UnitLabel => Session.Doc.Units == UnitSystem.IPS ? "in" : "mm";

    void ShowPage(PmPage? page)
    {
        _page = page;
        if (page == null)
        {
            PmHost.Content = null;
            PmHost.IsVisible = false;
            Tree.IsVisible = true;
            _modelMode.Filter = SelFilter.Any;
            Viewport.Emphasis = Array.Empty<SelItem>();
            UpdatePanelTabs();
            return;
        }
        PmHost.Content = page.View;
        PmHost.IsVisible = true;
        Tree.IsVisible = false;
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

    void AutoSave()
    {
        if (!Session.Dirty) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD");
            Directory.CreateDirectory(dir);
            Session.Doc.Save(Path.Combine(dir, "autosave" + CadDocument.FileExtension));
        }
        catch { /* best effort */ }
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if ((!Session.Dirty && !HasPendingEdit) || _closeConfirmed) return;
        e.Cancel = true;
        if (!await ResolvePendingEdit()) return;
        if (Session.Dirty)
        {
            var r = await Dialogs.YesNoCancel(this, "FeatherCAD", $"Save changes to {Session.Title.TrimEnd(' ', '*')}?");
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
        UpdateTriadLabels();
    }

    void UpdateTriadLabels()
    {
        var axes = new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ };
        for (int i = 0; i < 3; i++)
        {
            var p = Viewport.TriadLabel(axes[i]);
            Canvas.SetLeft(_triadLabels[i], p.X - 4); Canvas.SetTop(_triadLabels[i], p.Y - 8);
        }
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
