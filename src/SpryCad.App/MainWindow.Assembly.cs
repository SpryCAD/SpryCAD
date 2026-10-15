using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;
using Transform = SpryCad.Kernel.Transform;

namespace SpryCad.App;

/// <summary>
/// Assemblies, in document tabs as parts are (SolidWorks): an assembly tab shows its components (parts, and other
/// assemblies as sub-assemblies: rigid, or flexible with their own mates solved in it) in the design tree,
/// the Assembly tab of the CommandManager (Insert Components, Fix, Float, Lightweight, Open Part) and the components
/// placed in the graphics area, where floating ones are dragged into place (<see cref="AssemblyMode"/>). Save, Save As,
/// Close, Undo / Redo and Delete act on the assembly. Parts are found by identity wherever they went; a part not found
/// shows from the assembly's copy and is listed under the tree, never asked about.
/// </summary>
public partial class MainWindow : IAssemblyHost
{
    AssemblyMode? _asmMode;
    AssemblyTree _asmTree = null!;
    string? _asmHover;

    static readonly FilePickerFileType AssemblyType = new("SpryCAD Assembly") { Patterns = new[] { "*" + AssemblyDocument.FileExtension } };

    static bool IsAssembly(string path) => Path.GetExtension(path).Equals(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>STEP files that looked like assemblies but hold a single part: opened as parts.</summary>
    readonly HashSet<string> _stepParts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A STEP file holding an assembly (placed uses of parts), not a single part.</summary>
    bool IsStepAssembly(string path)
    {
        if (!IsStep(path) || _stepParts.Contains(Path.GetFullPath(path))) return false;
        try { return StepImport.IsAssembly(File.ReadAllBytes(path)); }
        catch (IOException) { return false; }
    }

    /// <summary>An assembly file, or a STEP file holding an assembly: opened in an assembly tab.</summary>
    bool OpensAsAssembly(string path) => IsAssembly(path) || IsStepAssembly(path);

    Task OpenAnyAssembly(string path) => IsAssembly(path) ? OpenAssembly(path) : OpenStepAssembly(path);

    /// <summary>
    /// A STEP assembly opened as a new assembly (not saved yet, named after the file): its parts embedded, each use
    /// placed as the file has it, the first fixed and the others locked to it (delete a lock to move a part).
    /// </summary>
    async Task OpenStepAssembly(string path)
    {
        if (_parts.FirstOrDefault(t => t.Asm != null && t.Source is { } src && PartIndex.SamePath(src, path)) is { } open) { await SwitchTo(open); return; }
        Status("Importing " + Path.GetFileName(path) + "…");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AssemblyModel? m;
        // (the parts' displays built after: the assembly shows at once, each part as it is done)
        try { m = await Task.Run(() => AssemblyModel.FromStep(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), defer: true)); }
        catch (Exception ex) { App.Log(ex, "Import STEP Assembly"); await Dialogs.Message(this, "Import STEP Assembly", $"{Path.GetFileName(path)} could not be imported: {ex.Message}"); return; }
        // one part after all: a part tab, as any STEP part
        if (m == null) { _stepParts.Add(Path.GetFullPath(path)); await OpenFileAsync(path); return; }
        var blank = IsUntouched(_tab) ? _tab : null;
        var t = AddAssemblyTab(new AssemblySession(m) { UntitledName = Path.GetFileNameWithoutExtension(path) });
        t.Source = path;
        RecentFiles.Add(path);
        if (!await SwitchTo(t)) return;
        if (blank != null && blank != t) RemoveTab(blank);
        Viewport.StandardView("Trimetric", fit: true, animate: false);
        var shownAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var imported = $"Imported {m.Doc.Components.Count} components from {m.Parts.Count} embedded part{(m.Parts.Count == 1 ? "" : "s")}. Delete a component's lock under Mates to move it." +
                       (m.Notes.Count > 0 ? $" {m.Notes.Count} note{(m.Notes.Count == 1 ? "" : "s")} in the design tree." : "");
        Status(imported);
        // the parts built, several at once, each shown when done; the view fitted to them after (nothing to fit before)
        await BuildPendingOf(t.Asm!);
        var builtAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_tab != t) return;
        Viewport.StandardView("Trimetric", fit: true, animate: false);
        // how long it took, once the frame with every part is drawn: read and made, the parts built, the first full frame
        Viewport.AfterNextFrame(at =>
        {
            static double S(long a, long b) => System.Diagnostics.Stopwatch.GetElapsedTime(a, b).TotalSeconds;
            if (_tab == t) Status($"{imported} Displayed in {S(started, at):0.0} s (read {S(started, shownAt):0.0} s, build {S(shownAt, builtAt):0.0} s, draw {S(builtAt, at):0.0} s).");
        });
    }

    /// <summary>
    /// Exports the assembly: STEP as an assembly (each part once, each configuration of it as a part of its own;
    /// each component placed), STL as all the components'
    /// solids where they are. Parts shown only from the assembly's copy (missing, lightweight) cannot be written.
    /// </summary>
    async Task ExportAssembly(string kind)
    {
        if (Asm is not { } a) return;
        // every part at every depth, each display (a part, as configured, in the assembly holding it) once
        var placed = a.Model.PlacedParts().ToList();
        var keys = placed.Select(x => x.pd).Distinct().ToList();
        var missing = keys.Where(pd => pd.Doc == null).ToList();
        if (keys.Count == 0) { await Dialogs.Message(this, "Export", "The assembly has no components to export."); return; }
        var type = kind == "step"
            ? new FilePickerFileType("STEP AP214") { Patterns = new[] { "*.step", "*.stp" } }
            : new FilePickerFileType("STL (binary)") { Patterns = new[] { "*.stl" } };
        var baseName = a.FilePath != null ? Path.GetFileNameWithoutExtension(a.FilePath) : a.UntitledName;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = kind == "step" ? "Export STEP Assembly" : "Export STL", DefaultExtension = kind == "step" ? "step" : "stl",
            SuggestedFileName = baseName + (kind == "step" ? ".step" : ".stl"), FileTypeChoices = new[] { type },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        Status($"Exporting {Path.GetFileName(path)}…");
        try
        {
            var model = a.Model;
            await Task.Run(() =>
            {
                var live = keys.Except(missing).ToList();
                // each part's solids, built from its document (threads modeled, as part exports do)
                var solids = new System.Collections.Concurrent.ConcurrentDictionary<PartDisplay, List<(string Id, Solid Solid)>>();
                PartWork.Each(live, k => k.Doc!, k => solids[k] = new Core.Regen.Regenerator { ThreadOverride = Core.Regen.ThreadDisplay.Modeled }.Rebuild(k.Doc!).State.Bodies.Select(b => (b.Id, b.Solid)).ToList());
                var comps = placed.Where(x => x.pd.Doc != null).ToList();
                using var fs = File.Create(path);
                if (kind == "step")
                {
                    // (a configured part named with its configuration: "Screw (M4, Length 12)"; the parts of
                    // sub-assemblies placed where they are in this one)
                    string NameOf(Occurrence o) { var l = o.Owner.ConfigLabel(o.Component); return o.Component.Name.Split('<')[0] + (l.Length > 0 ? $" ({l})" : ""); }
                    var parts = live.Select(k => (NameOf(comps.First(x => x.pd == k).o), (IReadOnlyList<(string, Solid)>)solids[k])).ToList();
                    StepWriter.WriteAssembly(fs, baseName, parts, comps.Select(x => (live.IndexOf(x.pd), model.NameAt(x.o.Path).Replace('/', '-'), x.o.World)).ToList());
                }
                else StlWriter.Write(fs, comps.SelectMany(x => solids[x.pd].Select(s => s.Solid.Transformed(x.o.World))));
            });
            Status($"Exported {path}." + (missing.Count > 0 ? $" {missing.Count} lightweight or missing part{(missing.Count == 1 ? " was" : "s were")} not exported; resolve {(missing.Count == 1 ? "it" : "them")} first." : ""));
        }
        catch (Exception ex) { App.Log(ex, "Export failed"); await Dialogs.Message(this, "Export Failed", ex.Message); }
    }

    /// <summary>The active tab's assembly (null: a part is active).</summary>
    AssemblySession? Asm => _tab?.Asm;

    /// <summary>The active document is an assembly (for dialogs outside the window, e.g. Options).</summary>
    internal bool IsAssemblyActive => Asm != null;

    static string TitleOf(PartTab t) =>
        (t.Asm?.Title ?? t.Session.Title) + (t.EmbeddedName == null ? ""
            : t.EmbeddedIn is { } e ? $" (in {e.Title.TrimEnd(' ', '*')})"
            : t.EmbeddedPath is { } p ? $" (in {Path.GetFileNameWithoutExtension(p)})" : " (embedded)");
    static bool DirtyOf(PartTab t) => t.Asm?.Dirty ?? t.Session.Dirty;

    /// <summary>The design tree tab's content: the part's tree and the assembly's (the active document's shows).</summary>
    Control TreePanel()
    {
        PanelHost.Children.Remove(Tree);
        _asmTree = new AssemblyTree { IsVisible = false };
        _asmTree.Selected += id => SelectComponent(id, exact: true);
        _asmTree.MateSelected += SelectMate;
        _asmTree.PlaneVisibilityToggled += ToggleAssemblyPlane;
        // an origin plane clicked while faces are being picked (a mate, a motor, a pattern): it is the pick
        _asmTree.OriginPicked += OnAssemblyOriginPicked;
        _asmTree.Hovered += OnAssemblyTreeHover;
        _asmTree.Opened += OpenComponent;
        _asmTree.MateCommand += OnMateCommand;
        _asmTree.Command += OnComponentCommand;
        _asmTree.AssemblyCommand += OnAssemblyCommand;
        _asmTree.PatternCommand += OnPatternCommand;
        InitPartDrop();
        return new Panel { Children = { Tree, _asmTree } };
    }

    /// <summary>The lowest "AssemblyN" no open assembly goes by.</summary>
    string NextAssemblyName()
    {
        var taken = new HashSet<string>(_parts.Where(t => t.Asm != null).Select(t => t.Asm!.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : t.Asm!.UntitledName),
            StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++) if (!taken.Contains("Assembly" + n)) return "Assembly" + n;
    }

    void NewAssembly() => _ = NewAssemblyAsync();

    async Task NewAssemblyAsync()
    {
        var model = AssemblyModel.New();
        model.Doc.Units = UserSettings.Units; // (in the units chosen last)
        var t = AddAssemblyTab(new AssemblySession(model) { UntitledName = NextAssemblyName() });
        if (!await SwitchTo(t)) { RemoveTab(t); return; }
        Status("New assembly created. Insert a component to begin; the first is fixed at the origin.");
        InsertComponentCommand();
    }

    PartTab AddAssemblyTab(AssemblySession s)
    {
        var t = AddTab(new DocumentSession());
        t.Asm = s;
        // its parts and sub-assemblies saved (here or anywhere else): shown as they now are
        s.Watcher = new SourceWatcher(files => _ = ReloadSources(t, files));
        s.Watcher.Watch(s.Model.SourceFiles());
        s.Changed += () =>
        {
            UpdateTabHeader(t);
            if (t == _tab) OnAssemblyChanged();
            // its preview in the list of open documents follows it
            if (_partRows.TryGetValue(t, out var row)) _ = LoadThumb(t, row.Image);
            // (components inserted, removed, found: the files watched follow)
            s.Watcher?.Watch(s.Model.SourceFiles());
        };
        // parts' displays arriving (built in the background): only the scene
        s.DisplaysChanged += () => { if (t == _tab) RefreshAssemblyScene(); };
        UpdateTabHeader(t);
        if (!_watchingActivation)
        {
            // back in the window: files changed meanwhile that the watchers missed (network folders) are caught by time
            _watchingActivation = true;
            Activated += (_, _) => { foreach (var p in _parts) p.Asm?.Watcher?.CheckStamps(); };
        }
        return t;
    }

    bool _watchingActivation;

    /// <summary>
    /// Files of an open assembly's parts or sub-assemblies were written: what was saved with changes is shown as it
    /// now is (only that: other parts are not read), then built in the background as when opening.
    /// </summary>
    async Task ReloadSources(PartTab t, IReadOnlyCollection<string> files)
    {
        if (t.Asm is not { } s || !_parts.Contains(t)) return;
        List<string> changed;
        try { changed = s.Reload(files); }
        catch (Exception ex) { App.Log(ex, "Reload assembly sources"); return; }
        if (changed.Count == 0) return;
        if (_tab == t)
            Status($"Reloaded {string.Join(", ", changed.Take(4))}{(changed.Count > 4 ? $" and {changed.Count - 4} more" : "")}.");
        await BuildPendingOf(s);
    }

    /// <summary>
    /// Rebuild (Ctrl+B, Force: Ctrl+Q): in a part, its features built again; in an assembly, the parts saved since they
    /// were read shown as they now are, and the mates solved again.
    /// </summary>
    void Rebuild(bool force = false)
    {
        if (Asm is not { } a) { if (force) Session.ForceRebuild(); else Session.RequestRebuild(); return; }
        _ = RebuildAssembly(a);
    }

    async Task RebuildAssembly(AssemblySession a)
    {
        var files = a.Model.Resolution.Parts.Values.Select(p => p.Path).OfType<string>().ToList();
        List<string> changed;
        try { changed = a.Reload(files); }
        catch (Exception ex) { App.Log(ex, "Rebuild assembly"); Status($"Rebuild failed: {ex.Message}"); return; }
        if (changed.Count == 0)
        {
            if (a.Model.HasMates) a.Model.SolveMates();
            RefreshAssemblyScene();
            _asmTree.Show(a);
            if (FlyoutHost.IsVisible) RebuildFlyoutTree();
        }
        Status(changed.Count == 0 ? "Rebuild complete. All parts up to date."
            : $"Rebuild complete. Reloaded {string.Join(", ", changed.Take(4))}{(changed.Count > 4 ? $" and {changed.Count - 4} more" : "")}.");
        await BuildPendingOf(a);
    }

    /// <summary>
    /// A part's command (features, sketches, equations) asked for in an assembly: refused, saying so, rather than done
    /// to the assembly tab's hidden part. True when refused (the command goes no further).
    /// </summary>
    internal bool PartOnly(string what, string? instead = null)
    {
        if (Asm == null) return false;
        Status($"{what} is available in parts only{(instead != null ? "; " + instead : ". Open the part to use it")}.");
        return true;
    }

    /// <summary>How threads are shown (View > Threads): in the part, or in the assembly and all it holds.</summary>
    Core.Regen.ThreadDisplay ThreadMode => Asm?.Model.Threads ?? Session.Threads;

    /// <summary>Shows threads another way: the part rebuilt, or the assembly's parts built again in the background.</summary>
    async Task SetThreadMode(Core.Regen.ThreadDisplay mode)
    {
        if (Asm is not { } a) { Session.Threads = mode; return; }
        a.Model.SetThreads(mode);
        RefreshAssemblyScene();
        await BuildPendingOf(a);
    }

    /// <summary>An assembly's displays waiting to be built: built in the background, the busy text saying which while it is shown.</summary>
    async Task BuildPendingOf(AssemblySession s)
    {
        if (!s.Model.AnyPending) return;
        await s.BuildPending(p =>
        {
            if (_tab?.Asm != s) return;
            BusyText.IsVisible = p != null;
            if (p != null) BusyText.Text = p;
        });
        if (_tab?.Asm == s) BusyText.IsVisible = false;
    }

    /// <summary>Opens an assembly in a tab (its tab shown if it is open already); its parts found and built in the background.</summary>
    async Task OpenAssembly(string path)
    {
        if (_parts.FirstOrDefault(t => t.Asm?.FilePath is { } p && PartIndex.SamePath(p, path)) is { } open) { await SwitchTo(open); return; }
        Status("Opening " + Path.GetFileName(path) + "…");
        AssemblySession s;
        try { s = await Task.Run(() => AssemblySession.Open(path)); }
        catch (Exception ex) { App.Log(ex, "Open Assembly"); await Dialogs.Message(this, "Open Assembly", $"{Path.GetFileName(path)} could not be opened: {ex.Message}"); return; }
        var blank = IsUntouched(_tab) ? _tab : null;
        var t = AddAssemblyTab(s);
        if (!IsEmbeddedCopy(path)) RecentFiles.Add(path);
        if (!await SwitchTo(t)) return;
        if (blank != null && blank != t) RemoveTab(blank);
        Viewport.StandardView("Trimetric", fit: true, animate: false);
        var notes = s.Model.Notes;
        var opened = notes.Count == 0 ? "Opened " + path + "." : notes.Count == 1 ? notes[0] : $"Opened {Path.GetFileName(path)}. {notes.Count} part notes in the design tree.";
        Status(opened);
        // virtual parts whose original files changed since they were made virtual here
        await OfferChangedOriginals(s);
        // parts without a current display cache: built now, each shown when done (the busy text says which)
        await BuildPendingOf(s);
    }

    /// <summary>
    /// The window as the active document needs it: for an assembly its tree, the Assembly tab of the CommandManager
    /// (the part tabs hidden) and picking components; for a part as before.
    /// </summary>
    void ApplyDocumentKind()
    {
        bool asm = Asm != null;
        Tree.IsVisible = !asm;
        _asmTree.IsVisible = asm;
        UpdateFlyoutTree();
        _asmMode ??= new AssemblyMode(this);
        Viewport.Mode = asm ? _asmMode : _modelMode;
        foreach (var (name, tab) in _tabs) tab.IsVisible = name is "Assembly" ? asm : !(asm && name is "Features" or "Sketch" or "Sheet Metal");
        // the part's Insert menu (sketches, features) has nothing to act on in an assembly
        foreach (var mi in MainMenu.Items.OfType<MenuItem>().Where(m => m.Header as string == "_Insert")) mi.IsEnabled = !asm;
        foreach (var mi in _partOnlyMenu) mi.IsEnabled = !asm;
        EndSection(); // (another model shown)
        if (asm && _activeTab != "Assembly") ShowTab("Assembly");
        else if (!asm && _activeTab == "Assembly") ShowTab("Features");
        _asmHover = null;
        ClearAssemblyView();
        // (a part's planes are named as the assembly's: the assembly's marks go with it)
        if (!asm) Viewport.Marked = Array.Empty<(SelItem, Rgba)>();
        if (asm) OnAssemblyChanged();
    }

    void OnAssemblyChanged()
    {
        if (Asm is not { } a) return;
        StatusUnits.Text = a.Model.Doc.Units.ToString();
        Title = "SpryCAD - " + a.Title;
        UpdateUndoButtons();
        RefreshAssemblyScene();
        RebuildTree();
    }

    // ------------------------------------------------------------------------------------------
    // the scene

    /// <summary>A placement as the viewport's row-vector matrix.</summary>
    static System.Numerics.Matrix4x4 ModelMatrix(Transform t) => new(
        (float)t.M00, (float)t.M10, (float)t.M20, 0,
        (float)t.M01, (float)t.M11, (float)t.M21, 0,
        (float)t.M02, (float)t.M12, (float)t.M22, 0,
        (float)t.T.X, (float)t.T.Y, (float)t.T.Z, 1);

    /// <summary>What makes two components the same part for Color by Part: the part they use, in the configuration they show.</summary>
    static string PartKey(Component c) => ComponentResolver.Key(c.Part) + "|" + c.Configuration;

    static Rgba MixColor(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    /// <summary>The components placed in the graphics area (selected and hovered ones tinted), with the standard planes.</summary>
    public void RefreshAssemblyScene()
    {
        if (Asm is not { } a) return;
        UpdateConfigureButton();
        var inst = new List<MeshInstance>();
        var threads = new List<Core.Regen.CosmeticThread>();
        var box = Box3.Empty;
        // (a component, or a sub-assembly with all it holds)
        static bool Under(string path, string? of) => of != null && (path == of || path.StartsWith(of + "/", StringComparison.Ordinal));
        var explode = ShownExplode(a);
        // Color by Part (the Color by Feature button, in an assembly): each part, in each configuration, a color of
        // its own, the same on every instance of it; in the order the parts first appear (hidden ones too), so hiding
        // or adding a component leaves the others' colors as they were
        Dictionary<string, int>? partColor = null;
        if (_colorByFeature)
        {
            partColor = new();
            foreach (var (o, _) in a.Model.PlacedParts(hidden: true))
                partColor.TryAdd(PartKey(o.Component), partColor.Count);
        }
        foreach (var (o, pd) in a.Model.PlacedParts())
        {
            if (pd.Mesh.Indices.Length == 0 || IsolatedOut(o.Path)) continue;
            var col = partColor != null ? FeatureColors.Color(partColor[PartKey(o.Component)]) : o.Color ?? pd.Color;
            var rgba = new Rgba((float)col[0], (float)col[1], (float)col[2]);
            // while faces are picked (Mate, Motor) the components keep their colors: the faces are highlighted
            if (_clashes?.Contains(o.Path) == true) rgba = MixColor(rgba, new Rgba(0.95f, 0.15f, 0.1f), 0.75f);
            else if (!PickingFaces && Under(o.Path, a.Selected)) rgba = MixColor(rgba, ViewportControl.SelectColor, 0.55f);
            else if (!PickingFaces && Under(o.Path, _asmHover)) rgba = MixColor(rgba, new Rgba(1f, 0.8f, 0.5f), 0.35f);
            var t = Drawn(o, explode);
            inst.Add(new MeshInstance(o.Path, pd.Mesh, ModelMatrix(t), rgba, FaceHighlights(o.Path)));
            // its cosmetic threads, placed with it
            foreach (var th in pd.Threads)
                threads.Add(th with { Origin = t.Point(th.Origin), Axis = t.Vector(th.Axis), RefDir = t.Vector(th.RefDir) });
            if (!pd.Mesh.Bounds.IsEmpty) { box.Add(t.Point(pd.Mesh.Bounds.Min)); box.Add(t.Point(pd.Mesh.Bounds.Max)); }
        }
        var half = box.IsEmpty ? 50 : Math.Max(20, box.Diagonal * 0.4);
        Viewport.Bodies = Array.Empty<Core.Regen.Body>();
        Viewport.PreviewBodies = Array.Empty<Core.Regen.Body>();
        Viewport.BendLines = Array.Empty<ViewportControl.BendLine>();
        Viewport.CosmeticThreads = threads;
        Viewport.Sketches = Array.Empty<SketchDisplay>();
        // the origin planes: as their eyes in the tree say, else shown while the assembly is empty; tinted as the triad
        bool StdShown(string std) => a.Model.Doc.PlaneVisibility?.TryGetValue(std, out var v) == true ? v : a.Model.Doc.Components.Count == 0;
        var planes = new List<PlaneDisplay>();
        foreach (var (std, fr) in new[] { ("Front", Frame.Front), ("Top", Frame.Top), ("Right", Frame.Right) })
            planes.Add(new PlaneDisplay(std, null, fr, half, StdShown(std), Tint: ViewportControl.OriginPlaneTint(std)));
        // a sub-assembly's origin plane picked (or hovered on the Mate page): drawn where the sub-assembly is
        static Frame Placed(Frame f, Transform t) => new(t.Point(f.Origin), t.Vector(f.XDir), t.Vector(f.YDir), t.Vector(f.Normal));
        var subPlanes = (PagePicks ?? (SelectedMate() is { } spm ? new List<MateRef> { spm.A, spm.B } : new List<MateRef>())).Append(_hoverPick)
            .Where(r => r is { Component: { } } && a.Model.Find(r.Component) is { Component.IsAssembly: true }).ToList();
        foreach (var r in subPlanes)
        {
            var f = r!.Face switch { "Top" => Frame.Top, "Right" => Frame.Right, _ => Frame.Front };
            if (!planes.Any(p => p.FeatureId == r.Component && p.Name == r.Face))
                planes.Add(new PlaneDisplay(r.Face, r.Component, Placed(f, a.Model.WorldOf(r.Component!)), Math.Max(10, a.Model.WorldBounds(r.Component!).Diagonal * 0.4)));
        }
        Viewport.Planes = planes;
        Viewport.ShowPlanes = _showPlanes; // (each plane as its eye says, unless the Planes view shows them all)
        // the origin planes the selected mate (or the Mate page's picks) use: shown highlighted as its faces are (the
        // pick selected in the Mate Selections box in full blue, the other paler); one hovered on the page in orange
        var refs = PagePicks ?? (SelectedMate() is { } sm ? new List<MateRef> { sm.A, sm.B } : new List<MateRef>());
        var marked = new List<(SelItem, Rgba)>();
        static SelItem PlaneItem(string name, string? owner = null) => new() { Kind = SelKind.Plane, Name = name, FeatureId = owner };
        bool IsPlane(MateRef r) => r.Component == null || a.Model.Find(r.Component) is { Component.IsAssembly: true };
        if (_hoverPick is { } hp && IsPlane(hp)) marked.Add((PlaneItem(hp.Face, hp.Component), ViewportControl.HoverColor));
        for (int i = 0; i < refs.Count; i++)
            if (IsPlane(refs[i])) marked.Add((PlaneItem(refs[i].Face, refs[i].Component), PickColor(i)));
        // a round face picked: its axis drawn; a center: a cross there. While picking, a round face under the
        // pointer shows its axis too (what a click on it mates)
        static SelItem AxisItem((Vec3 a, Vec3 b) s) => new() { Kind = SelKind.Axis, Point = (s.a + s.b) * 0.5, Normal = (s.b - s.a).Normalized, AxisHalfLength = Vec3.Distance(s.a, s.b) / 2 };
        for (int i = 0; i < refs.Count; i++)
        {
            if (refs[i].Component is not { } rc || IsPlane(refs[i])) continue;
            if (refs[i].End != null) marked.Add((new SelItem { Kind = SelKind.Vertex, IsCenter = true, Point = a.Model.World(refs[i]).P }, PickColor(i)));
            else if (refs[i].Kind == MateGeomKind.Axis && a.Model.AxisSegment(rc, refs[i].Face) is { } seg) marked.Add((AxisItem(seg), PickColor(i)));
        }
        if (PickingFaces && _hoverFace is { } hov && a.Model.AxisSegment(hov.comp, hov.face) is { } hseg) marked.Add((AxisItem(hseg), ViewportControl.HoverColor));
        // an origin item picked in the tree (a plane, an axis, the point): shown selected, as in a part
        if (_asmOriginSel is { } os && OriginItem(os) is { } oi) marked.Add((oi, ViewportControl.SelectColor));
        Viewport.Emphasis = Array.Empty<SelItem>();
        Viewport.Marked = marked;
        Viewport.Instances = inst;
        Viewport.Redraw();
    }

    // ------------------------------------------------------------------------------------------
    // the Assembly tab of the CommandManager

    void AssemblyCommands()
    {
        void Tip(string tip) => ToolTip.SetTip(CommandBar.Children[^1], tip);
        Cmd("insertcomp", "Insert\nComponents", InsertComponentCommand);
        Tip("Insert Components\nInserts parts or sub-assemblies, linked to their files or embedded in the assembly.");
        Cmd("mates", "Mate", MateCommand);
        Tip("Mate\nPositions two components relative to each other using faces, edges or planes.");
        Cmd("motor", "Motor", MotorCommand);
        Tip("Motor\nRotates a component about a cylindrical face's axis at a set speed to simulate motion through its mates.");
        Cmd("lpattern", "Linear\nPattern", () => PatternPage(PatternKind.Linear));
        Tip("Linear Component Pattern\nCopies components along one direction; the instances follow their seed components.");
        Cmd("cpattern", "Circular\nPattern", () => PatternPage(PatternKind.Circular));
        Tip("Circular Component Pattern\nCopies components around an axis; the instances follow their seed components.");
        Cmd("mirror", "Mirror\nComponents", () => PatternPage(PatternKind.Mirror));
        Tip("Mirror Components\nMirrors components across a plane without creating opposite-hand parts.");
        Sep();
        Cmd("formsub", "Form\nSub-assembly", () => FormSubAssemblyPage());
        Tip("Form New Sub-assembly\nMoves the selected components and the mates between them into a new sub-assembly.");
        Cmd("replace", "Replace\nComponent", () => _ = ReplaceComponent());
        Tip("Replace Component\nReplaces the selected component with another file, keeping its position and any mates that still resolve.");
        Sep();
        Cmd("fix", "Fix", () => SetFixed(true));
        Tip("Fix\nFixes the selected component where it is.");
        Cmd("float", "Float", () => SetFixed(false));
        Tip("Float\nFrees the selected component so it can be dragged; Shift+drag rotates it.");
        Cmd("lightweight", "Lightweight", ToggleLightweight);
        Tip("Lightweight / Resolve\nLoads the selected part's components from the assembly's cached copy for speed, or resolves them from the part.");
        Cmd("configure", "Configure", () => _ = ConfigureComponent());
        Tip("Configure\nSets the selected component's design table row or user parameters for this instance.");
        _configureButton = (Button)CommandBar.Children[^1];
        ToolTip.SetShowOnDisabled(_configureButton, true); // greyed out: still says what it is for
        UpdateConfigureButton(force: true);
        Sep();
        Cmd("open", "Open\nPart", () => { if (Asm?.Selected is { } id) OpenComponent(id); });
        Tip("Open Part\nOpens the selected component's part or sub-assembly in its own tab; embedded files save back into the assembly.");
        Sep();
        Cmd("hide", "Hide /\nShow", ToggleHidden);
        Tip("Hide / Show\nHides or shows the selected component; hidden components are still mated and solved.");
        Cmd("isolate", "Isolate", () => Isolate());
        Tip("Isolate\nShows only the selected component; click again to show all components.");
        Cmd("explode", "Exploded\nView", ExplodeCommand);
        Tip("Exploded View\nMoves components apart in steps along set directions to show how the assembly fits together.");
        Cmd("interference", "Interference\nDetection", InterferenceCommand);
        Tip("Interference Detection\nFinds components whose volumes overlap and highlights each pair in red.");
        Cmd("bom", "Bill of\nMaterials", BomCommand);
        Tip("Bill of Materials\nLists parts and sub-assemblies with quantities, indented, top-level or parts-only, for copying or CSV export.");
        Cmd("packgo", "Pack\nand Go", () => _ = PackAndGoCommand());
        Tip("Pack and Go\nCopies the assembly and all referenced files to a folder or zip, optionally renaming them.");
    }

    Button? _configureButton;
    string? _configureFor;

    /// <summary>
    /// The Assembly tab's Configure, as the component menu's: only for a selected component whose part has something
    /// to configure (a design table, user-facing variables). Looked up once per selection, since finding the part may
    /// read its file.
    /// </summary>
    void UpdateConfigureButton(bool force = false)
    {
        if (_configureButton == null) return;
        var id = Asm?.Selected;
        if (!force && id == _configureFor) return;
        _configureFor = id;
        _configureButton.IsEnabled = id != null && !id.Contains('/') && FindComponent(id) is { IsAssembly: false } c && Asm!.Model.PartDocument(c)?.UserParameters().Count > 0;
    }

    // ------------------------------------------------------------------------------------------
    // commands

    /// <summary>
    /// Insert Component (PropertyManager): the parts and assemblies open in other tabs, or Browse… for files; parts
    /// linked or embedded in the assembly; assemblies (sub-assemblies, linked) rigid or flexible. The first component
    /// is fixed at the origin, the others go beside the assembly.
    /// </summary>
    void InsertComponentCommand()
    {
        if (Asm is not { } self) return;
        var page = new PmPage("Insert Component", "insertcomp");
        var s = page.Section("Part/Assembly to Insert");
        page.Info(s, "Select an open part or assembly, or click Browse… to insert a file. Double-click to insert.");
        var list = new ListBox { MaxHeight = 280, MinHeight = 60 };
        foreach (var t in _parts.Where(t => t.Asm == null && t.Session.FilePath != null))
            list.Items.Add(new ListBoxItem { Content = TitleOf(t), Tag = t.Session.FilePath });
        // (saved assemblies other than this one: sub-assemblies)
        foreach (var t in _parts.Where(t => t.Asm is { FilePath: not null } x && x != self))
            list.Items.Add(new ListBoxItem { Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { Icons.Make("assembly", 16), new TextBlock { Text = TitleOf(t) } } }, Tag = t.Asm!.FilePath });
        if (list.Items.Count == 0) page.Info(s, "No saved part or assembly is open.");
        else s.Children.Add(list);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 6, 0, 0) };
        s.Children.Add(browse);
        var o = page.Section("Options");
        bool embed = false;
        page.Check(o, "Embed parts in the assembly", () => embed, v => embed = v);
        page.Info(o, "Embedded parts are stored in the assembly file. Linked parts stay in their own files.");
        var sa = page.Section("Sub-assemblies");
        bool flexible = false;
        page.Combo(sa, "Solve as", new List<(bool, string)> { (false, "Rigid"), (true, "Flexible") }, () => flexible, v => flexible = v);
        page.Info(sa, "A rigid sub-assembly moves as one body. A flexible sub-assembly solves its own mates here, so its parts can move.");
        void Close() { ShowPage(null); HideConfirmCorner(); }
        async Task InsertAndClose(IReadOnlyList<string> paths)
        {
            Close();
            await InsertParts(paths, embed, flexible);
        }
        async void Ok()
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is string path) await InsertAndClose(new[] { path });
            else Status("Select a part or assembly in the list, or click Browse…");
        }
        list.DoubleTapped += (_, _) => Ok();
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Insert Component", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("SpryCAD Part or Assembly") { Patterns = new[] { "*" + CadDocument.FileExtension, "*" + AssemblyDocument.FileExtension } }, FcadType, AssemblyType },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) await InsertAndClose(paths);
        };
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK (Enter)", "Cancel (Esc)");
    }

    async Task InsertParts(IReadOnlyList<string> paths, bool embed, bool flexible = false)
    {
        if (Asm is not { } a) return;
        a.BeginChange();
        Status($"Inserting {string.Join(", ", paths.Select(Path.GetFileName))}…");
        string? last = null;
        foreach (var p in paths)
        {
            try
            {
                var at = PlaceBeside(a.Model);
                // (assemblies are linked, never embedded: sub-assemblies)
                var c = await Task.Run(() => PartsIndexStore.Use(_ => AssemblyModel.IsAssemblyFile(p) ? a.Model.InsertAssembly(p, at, flexible) : a.Model.Insert(p, at, embed)));
                last = c.Id;
            }
            catch (Exception ex) { App.Log(ex, "Insert Component"); await Dialogs.Message(this, "Insert Component", $"{Path.GetFileName(p)} could not be inserted: {ex.Message}"); }
        }
        if (last == null) { a.AbandonChange(); Status("Nothing inserted."); return; }
        a.Selected = last;
        a.EndChange();
        Viewport.ZoomToFit();
        var subs = paths.Count(AssemblyModel.IsAssemblyFile);
        Status($"Inserted {paths.Count} component{(paths.Count == 1 ? "" : "s")}{(subs > 0 && flexible ? $", {subs} flexible sub-assembl{(subs == 1 ? "y" : "ies")}" : "")}. Drag a floating component to move it; Shift+drag rotates it.");
    }

    /// <summary>Where a new component goes: the origin for the first, else beside the assembly along +X.</summary>
    static Transform PlaceBeside(AssemblyModel m)
    {
        var b = Box3.Empty;
        foreach (var c in m.Doc.Components)
            if (!c.Suppressed && m.WorldBounds(c.Id) is { IsEmpty: false } cb) { b.Add(cb.Min); b.Add(cb.Max); }
        return b.IsEmpty ? Transform.Identity : Transform.Translation(new Vec3(b.Max.X + 0.2 * Math.Max(10, b.Diagonal), 0, 0));
    }

    /// <summary>
    /// The selected component, if it is one of the assembly's own (a part of a sub-assembly is changed in the
    /// sub-assembly: said so). Null: nothing to act on.
    /// </summary>
    Component? SelectedOwn()
    {
        if (Asm is not { } a || FindComponent(a.Selected) is not { } c) { Status("Select a component first."); return null; }
        if (a.Selected!.Contains('/'))
        {
            // (a sub-assembly's component: hide, suppress and color it from its menu, for this instance)
            var sub = a.Model.Find(a.Selected[..a.Selected.IndexOf('/')])!.Component;
            Status($"{c.Name} belongs to sub-assembly {sub.Name}. Open the sub-assembly to edit it, or select {sub.Name}.");
            return null;
        }
        return c;
    }

    void SetFixed(bool fix)
    {
        if (Asm is not { } a || SelectedOwn() is not { } c) return;
        if (c.Pattern != null) { Status($"{c.Name} is a pattern instance; the pattern controls its position."); return; }
        if (c.Fixed == fix) return;
        a.BeginChange();
        c.Fixed = fix;
        // floating now: the mates may move it (and fixed: others may have to move instead)
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
    }

    /// <summary>A sub-assembly made flexible (its own mates solved here, its parts free to move) or rigid (as its file has it).</summary>
    void SetFlexible(bool flexible)
    {
        if (Asm is not { } a || SelectedOwn() is not { } c) return;
        if (!c.IsAssembly) { Status($"{c.Name} is a part. Only sub-assemblies can be rigid or flexible."); return; }
        if (c.Flexible == flexible) return;
        a.BeginChange();
        a.Model.SetFlexible(c, flexible);
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        Status(flexible ? $"{c.Name} set to flexible."
            : $"{c.Name} set to rigid.");
    }

    void ToggleLightweight()
    {
        if (Asm is not { } a || SelectedOwn() is not { } c) return;
        if (c.IsAssembly) { Status($"{c.Name} is a sub-assembly. Set its parts to lightweight inside it."); return; }
        a.BeginChange();
        var key = ComponentResolver.Key(c.Part);
        var light = !c.Lightweight;
        foreach (var k in a.Model.ComponentsOf(key)) k.Lightweight = light;
        // shown from the copy of its revision (made now if the assembly has none yet); resolved: built from the part
        if (light) a.Model.RefreshCaches();
        a.Model.LoadPart(key);
        a.EndChange();
        Status(light ? $"{c.Name} set to lightweight." : $"{c.Name} resolved.");
    }

    /// <summary>
    /// Configures the selected component (or all of its part's instances): the part's user-facing parameters for it,
    /// its display built, the mates solved again (its faces may have moved).
    /// </summary>
    async Task ConfigureComponent()
    {
        if (Asm is not { } a || SelectedOwn() is not { } c) return;
        if (c.IsAssembly) { Status($"{c.Name} is a sub-assembly. Configure its parts inside it."); return; }
        var partName = c.Name.Split('<')[0];
        if (a.Model.PartDocument(c) is not { } part) { await Dialogs.Message(this, "Configure", $"Could not configure {c.Name}: part file not found."); return; }
        if (part.UserParameters().Count == 0)
        {
            await Dialogs.Message(this, "Configure", $"{partName} has no user parameters. Mark variables as User in Equations, or add a design table to the part.");
            return;
        }
        var instances = a.Model.ComponentsOf(ComponentResolver.Key(c.Part)).ToList();
        var chosen = await Dialogs.Configure(this, part, c.Parameters, $"Configure {c.Name}",
            $"Parameters of {partName} for this instance. Values left at the part's defaults follow the part.", instances.Count > 1 ? $"All {instances.Count} instances of {partName}" : null);
        if (chosen is not { } ch) return;
        var comps = ch.all ? instances : new List<Component> { c };
        var config = Configurations.KeyOf(part.Normalize(ch.values));
        if (comps.All(x => Configurations.KeyOf(x.Parameters) == config)) return;
        a.BeginChange();
        try { a.Model.Configure(comps, part, ch.values); }
        catch (Exception ex) { App.Log(ex, "Configure failed"); a.AbandonChange(); await Dialogs.Message(this, "Configure Failed", ex.Message); return; }
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        var problem = a.Model.DisplayOf(c)?.Problem;
        Status($"{(comps.Count > 1 ? $"{comps.Count} instances of {partName}" : c.Name)} configured: {part.Label(ch.values)}." + (problem != null ? $" Rebuild failed: {problem}" : ""));
    }

    void DeleteComponent()
    {
        // (Delete with a mate selected in the tree deletes the mate)
        if (Asm is { SelectedMate: { } mateId } && _mating == null) { OnMateCommand(mateId, "delete"); return; }
        if (Asm is not { } a || a.Selected == null || SelectedOwn() is not { } c) return;
        if (c.Pattern != null) { Status($"{c.Name} is a pattern instance. Edit or delete the pattern instead."); return; }
        a.BeginChange();
        a.Model.Remove(c);
        a.Selected = null;
        a.EndChange();
        Status($"Deleted {c.Name}.");
    }

    async Task<bool> SaveAssembly(bool saveAs)
    {
        if (Asm is not { } a) return false;
        var path = a.FilePath;
        // an embedded sub-assembly being edited: saved back into its assembly (and the assembly saved)
        if (!saveAs && _tab.EmbeddedName != null)
        {
            try { if (await SaveEmbedded(_tab) is { } done) return done; }
            catch (Exception ex) { App.Log(ex, "Save embedded"); await Dialogs.Message(this, "Save Failed", ex.Message); return false; }
        }
        if (saveAs || path == null)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Assembly As",
                DefaultExtension = "sasm",
                SuggestedFileName = (path != null ? Path.GetFileNameWithoutExtension(path) : a.UntitledName) + AssemblyDocument.FileExtension,
                FileTypeChoices = new[] { AssemblyType },
            });
            path = f?.TryGetLocalPath();
            if (path == null) return false;
        }
        try
        {
            a.Save(path);
            RecentFiles.Add(path);
            Status("Saved " + path + ".");
            if (saveAs && _tab.EmbeddedName != null) { _tab.EmbeddedIn = null; _tab.EmbeddedName = null; _tab.EmbeddedPath = null; UpdateTabHeader(_tab); }
            return true;
        }
        catch (Exception ex) { App.Log(ex, "Save failed"); await Dialogs.Message(this, "Save Failed", ex.Message); return false; }
    }

    async Task<bool> ConfirmDiscardAssembly()
    {
        if (Asm is not { Dirty: true } a) return true;
        var r = await Dialogs.YesNoCancel(this, "Save Changes", $"Save changes to {a.Title.TrimEnd(' ', '*')}?");
        if (r == null) return false;
        if (r == true) return await SaveAssembly(false);
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // IAssemblyHost (the graphics area's assembly mode)

    public void HoverComponent(string? id)
    {
        // (a part of a rigid sub-assembly: the sub-assembly, which moves as one)
        if (id != null && Asm is { } a) id = a.Model.Selectable(id);
        if (id == _asmHover) return;
        _asmHover = id;
        RefreshAssemblyScene();
    }

    /// <summary>A mate picked in the tree: its two faces (or origin planes) shown highlighted on the model.</summary>
    void SelectMate(string? id)
    {
        if (Asm is not { } a || (id == a.SelectedMate && a.Selected == null)) return;
        a.SelectedMate = id;
        _asmOriginSel = null;
        a.Selected = null;
        RefreshAssemblyScene();
        _asmTree.SelectMateRow(id);
        if (SelectedMate() is { } m)
        {
            string Of(MateRef r) => r.Component == null ? r.Face + " Plane" : $"{a.Model.NameAt(r.Component)} ({(r.End != null ? "center of " : "")}{a.Model.FaceName(r)})";
            Status($"{m.Name}: {Of(m.A)} and {Of(m.B)}{(m.Suppressed ? ", suppressed" : "")}. Double-click to edit.");
        }
    }

    /// <summary>The mate selected in the tree (none while the Mate page shows its own picks).</summary>
    Mate? SelectedMate() => Asm is { SelectedMate: { } id } a && _mating == null ? a.Model.Doc.Mates.FirstOrDefault(m => m.Id == id) : null;

    /// <summary>Shows or hides an origin plane of the assembly (its eye in the tree): saved with it, undoable.</summary>
    void ToggleAssemblyPlane(string std)
    {
        if (Asm is not { } a) return;
        var doc = a.Model.Doc;
        var empty = doc.Components.Count == 0;
        var shown = doc.PlaneVisibility?.TryGetValue(std, out var v) == true ? v : empty;
        a.BeginChange();
        // (back to automatic when that is what it would be anyway)
        if (!shown == empty) doc.PlaneVisibility?.Remove(std);
        else (doc.PlaneVisibility ??= new())[std] = !shown;
        if (doc.PlaneVisibility is { Count: 0 }) doc.PlaneVisibility = null;
        a.EndChange();
        Status($"{std} Plane {(shown ? "hidden" : "shown")}.");
    }

    public void SelectComponent(string? id) => SelectComponent(id, exact: false);

    /// <summary>
    /// Selects a component (by path). Clicked in the graphics area (not <paramref name="exact"/>), a part of a rigid
    /// sub-assembly selects the sub-assembly, which moves as one; in the tree, the row clicked.
    /// </summary>
    void SelectComponent(string? id, bool exact)
    {
        if (Asm is { } sa && id != null && !exact) id = sa.Model.Selectable(id);
        if (Asm is not { } a || (a.Selected == id && a.SelectedMate == null && _asmOriginSel == null)) return;
        _asmOriginSel = null;
        a.SelectedMate = null;
        a.Selected = id;
        RefreshAssemblyScene();
        _asmTree.Select(id);
        if (a.Model.Find(id) is { } o)
        {
            var c = o.Component;
            var how = a.Model.Movable(o.Path) is not { } mv ? "fixed"
                : mv != o.Path ? $"moves with {a.Model.NameAt(mv)}" : "floating";
            var kind = c.IsAssembly ? (c.Flexible ? "flexible sub-assembly, " : "rigid sub-assembly, ") : "";
            Status($"{a.Model.NameAt(o.Path)}: {kind}{how}" +
                   (c.Part.Embedded != null ? ", embedded" : o.Owner.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part))?.Path is { } p ? ", " + p : ""));
        }
    }

    public void OpenComponent(string id)
    {
        if (Asm is not { } a || a.Model.Find(id) is not { } o) return;
        var c = o.Component;
        if (c.Part.Embedded != null) { _ = EditEmbedded(a, o); return; }
        if (o.Owner.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part))?.Path is not { } path) { Status($"{c.Name}: {(c.IsAssembly ? "assembly" : "part")} file not found."); return; }
        _ = c.IsAssembly ? OpenAssembly(path) : OpenFileAsync(path);
    }

    /// <summary>The component at a path (one of the assembly's own, or of a sub-assembly).</summary>
    public Component? FindComponent(string? id) => id != null ? Asm?.Model.Find(id)?.Component : null;
    // (not while exploded: what is shown is not where it is)
    public string? Movable(string id) => _exploded ? null : Asm?.Model.Movable(id);
    public Transform WorldOf(string id) => Asm?.Model.WorldOf(id) ?? Transform.Identity;
    public void SetWorld(string id, Transform world) => Asm?.Model.SetWorld(id, world);
    public Box3 BoundsOf(string id) => Asm?.Model.WorldBounds(id) ?? Box3.Empty;
    public void BeginAssemblyChange() => Asm?.BeginChange();
    public void EndAssemblyChange() => Asm?.EndChange();
}
