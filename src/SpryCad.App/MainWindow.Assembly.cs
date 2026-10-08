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
/// Assemblies, in document tabs as parts are (SolidWorks): an assembly tab shows its components in the design tree,
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
        AssemblyModel? m;
        try { m = await Task.Run(() => AssemblyModel.FromStep(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path))); }
        catch (Exception ex) { await Dialogs.Message(this, "Import STEP Assembly", $"{Path.GetFileName(path)} could not be imported: {ex.Message}"); return; }
        // one part after all: a part tab, as any STEP part
        if (m == null) { _stepParts.Add(Path.GetFullPath(path)); await OpenFileAsync(path); return; }
        var blank = IsUntouched(_tab) ? _tab : null;
        var t = AddAssemblyTab(new AssemblySession(m) { UntitledName = Path.GetFileNameWithoutExtension(path) });
        t.Source = path;
        RecentFiles.Add(path);
        if (!await SwitchTo(t)) return;
        if (blank != null && blank != t) RemoveTab(blank);
        Viewport.StandardView("Trimetric", fit: true, animate: false);
        Status($"Imported {m.Doc.Components.Count} components ({m.Parts.Count} parts, embedded), each locked to the first: delete a lock (under Mates) to move a part." +
               (m.Notes.Count > 0 ? $" {m.Notes.Count} notes under the tree." : ""));
    }

    /// <summary>
    /// Exports the assembly: STEP as an assembly (each part once, each configuration of it as a part of its own;
    /// each component placed), STL as all the components'
    /// solids where they are. Parts shown only from the assembly's copy (missing, lightweight) cannot be written.
    /// </summary>
    async Task ExportAssembly(string kind)
    {
        if (Asm is not { } a) return;
        var keys = a.Model.Doc.Components.Where(c => !c.Suppressed).Select(AssemblyModel.DisplayKey).Distinct().ToList();
        var missing = keys.Where(k => a.Model.Parts.GetValueOrDefault(k)?.Doc == null).ToList();
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
                var solids = live.ToDictionary(k => k, k => new Core.Regen.Regenerator { ThreadOverride = Core.Regen.ThreadDisplay.Modeled }.Rebuild(model.Parts[k].Doc!).State.Bodies.Select(b => (b.Id, b.Solid)).ToList());
                var comps = model.Doc.Components.Where(c => !c.Suppressed && live.Contains(AssemblyModel.DisplayKey(c))).ToList();
                using var fs = File.Create(path);
                if (kind == "step")
                {
                    // (a configured part named with its configuration: "Screw (M4, Length 12)")
                    string NameOf(Component c) { var l = model.ConfigLabel(c); return c.Name.Split('<')[0] + (l.Length > 0 ? $" ({l})" : ""); }
                    var parts = live.Select(k => (NameOf(model.ComponentsShowing(k).First()), (IReadOnlyList<(string, Solid)>)solids[k])).ToList();
                    StepWriter.WriteAssembly(fs, baseName, parts, comps.Select(c => (live.IndexOf(AssemblyModel.DisplayKey(c)), c.Name, c.Placement.ToTransform())).ToList());
                }
                else StlWriter.Write(fs, comps.SelectMany(c => solids[AssemblyModel.DisplayKey(c)].Select(s => s.Solid.Transformed(c.Placement.ToTransform()))));
            });
            Status($"Exported {path}" + (missing.Count > 0 ? $" ({missing.Count} part(s) left out: shown only from the assembly's copy; resolve them first)" : ""));
        }
        catch (Exception ex) { await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    /// <summary>The active tab's assembly (null: a part is active).</summary>
    AssemblySession? Asm => _tab?.Asm;

    static string TitleOf(PartTab t) => t.Asm?.Title ?? t.Session.Title;
    static bool DirtyOf(PartTab t) => t.Asm?.Dirty ?? t.Session.Dirty;

    /// <summary>The design tree tab's content: the part's tree and the assembly's (the active document's shows).</summary>
    Control TreePanel()
    {
        PanelHost.Children.Remove(Tree);
        _asmTree = new AssemblyTree { IsVisible = false };
        _asmTree.Selected += id => SelectComponent(id);
        _asmTree.MateSelected += SelectMate;
        _asmTree.Opened += OpenComponent;
        _asmTree.MateCommand += OnMateCommand;
        _asmTree.Command += (id, cmd) =>
        {
            if (Asm is not { } a) return;
            a.Selected = id; a.SelectedMate = null;
            switch (cmd)
            {
                case "open": OpenComponent(id); break;
                case "fix": SetFixed(true); break;
                case "float": SetFixed(false); break;
                case "lightweight": ToggleLightweight(); break;
                case "configure": _ = ConfigureComponent(); break;
                case "delete": DeleteComponent(); break;
            }
        };
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
        var t = AddAssemblyTab(new AssemblySession(AssemblyModel.New()) { UntitledName = NextAssemblyName() });
        if (!await SwitchTo(t)) { RemoveTab(t); return; }
        Status("New assembly: Insert Components to bring in parts (the first is fixed at the origin).");
        InsertComponentCommand();
    }

    PartTab AddAssemblyTab(AssemblySession s)
    {
        var t = AddTab(new DocumentSession());
        t.Asm = s;
        s.Changed += () =>
        {
            UpdateTabHeader(t);
            if (t == _tab) OnAssemblyChanged();
            // its preview in the list of open documents follows it
            if (_partRows.TryGetValue(t, out var row)) _ = LoadThumb(t, row.Image);
        };
        UpdateTabHeader(t);
        return t;
    }

    /// <summary>Opens an assembly in a tab (its tab shown if it is open already); its parts found and built in the background.</summary>
    async Task OpenAssembly(string path)
    {
        if (_parts.FirstOrDefault(t => t.Asm?.FilePath is { } p && PartIndex.SamePath(p, path)) is { } open) { await SwitchTo(open); return; }
        Status("Opening " + Path.GetFileName(path) + "…");
        AssemblySession s;
        try { s = await Task.Run(() => AssemblySession.Open(path)); }
        catch (Exception ex) { await Dialogs.Message(this, "Open Assembly", $"{Path.GetFileName(path)} could not be opened: {ex.Message}"); return; }
        var blank = IsUntouched(_tab) ? _tab : null;
        var t = AddAssemblyTab(s);
        RecentFiles.Add(path);
        if (!await SwitchTo(t)) return;
        if (blank != null && blank != t) RemoveTab(blank);
        Viewport.StandardView("Trimetric", fit: true, animate: false);
        var notes = s.Model.Notes;
        var opened = notes.Count == 0 ? "Opened " + path : notes.Count == 1 ? notes[0] : $"Opened {Path.GetFileName(path)}: {notes.Count} notes about its parts (under the tree)";
        Status(opened);
        // parts without a current display cache: built now, each shown when done (the busy text says which)
        if (s.Model.Pending.Count > 0)
        {
            await s.BuildPending(p =>
            {
                if (_tab?.Asm != s) return;
                BusyText.IsVisible = p != null;
                if (p != null) BusyText.Text = p;
            });
            if (_tab?.Asm == s) BusyText.IsVisible = false;
        }
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
        if (asm && _activeTab != "Assembly") ShowTab("Assembly");
        else if (!asm && _activeTab == "Assembly") ShowTab("Features");
        _asmHover = null;
        // (a part's planes are named as the assembly's: the assembly's marks go with it)
        if (!asm) Viewport.Marked = Array.Empty<(SelItem, Rgba)>();
        if (asm) OnAssemblyChanged();
    }

    void OnAssemblyChanged()
    {
        if (Asm is not { } a) return;
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

    static Rgba MixColor(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    /// <summary>The components placed in the graphics area (selected and hovered ones tinted), with the standard planes.</summary>
    public void RefreshAssemblyScene()
    {
        if (Asm is not { } a) return;
        var inst = new List<MeshInstance>();
        var threads = new List<Core.Regen.CosmeticThread>();
        var box = Box3.Empty;
        foreach (var c in a.Model.Doc.Components)
        {
            if (c.Suppressed || a.Model.DisplayOf(c) is not { } pd || pd.Mesh.Indices.Length == 0) continue;
            var col = c.Color ?? pd.Color;
            var rgba = new Rgba((float)col[0], (float)col[1], (float)col[2]);
            // while faces are picked (Mate) the components keep their colors: the faces are highlighted
            if (_mating == null && c.Id == a.Selected) rgba = MixColor(rgba, ViewportControl.SelectColor, 0.55f);
            else if (_mating == null && c.Id == _asmHover) rgba = MixColor(rgba, new Rgba(1f, 0.8f, 0.5f), 0.35f);
            var t = c.Placement.ToTransform();
            inst.Add(new MeshInstance(c.Id, pd.Mesh, ModelMatrix(t), rgba, FaceHighlights(c.Id)));
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
        Viewport.Planes = new List<PlaneDisplay>
        {
            new("Front", null, Frame.Front, half), new("Top", null, Frame.Top, half), new("Right", null, Frame.Right, half),
        };
        Viewport.ShowPlanes = _showPlanes || inst.Count == 0;
        // the origin planes the selected mate (or the Mate page's picks) use: shown highlighted as its faces are (the
        // pick selected in the Mate Selections box in full blue, the other paler); one hovered on the page in orange
        var refs = _mating?.Picks ?? (SelectedMate() is { } sm ? new List<MateRef> { sm.A, sm.B } : new List<MateRef>());
        var marked = new List<(SelItem, Rgba)>();
        static SelItem PlaneItem(string name) => new() { Kind = SelKind.Plane, Name = name };
        if (_hoverPick is { Component: null } hp) marked.Add((PlaneItem(hp.Face), ViewportControl.HoverColor));
        for (int i = 0; i < refs.Count; i++)
            if (refs[i].Component == null) marked.Add((PlaneItem(refs[i].Face), PickColor(i)));
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
        Tip("Insert Components\nBrings parts into the assembly: open parts or files, linked (an edit to the part shows in every assembly using it) or embedded (kept inside the assembly: one self-contained file).");
        Cmd("mates", "Mate", MateCommand);
        Tip("Mate\nPositions components relative to each other: pick two faces (or a face and an origin plane), then coincident, parallel, perpendicular, concentric, distance or angle.");
        Sep();
        Cmd("fix", "Fix", () => SetFixed(true));
        Tip("Fix\nFixes the selected component where it is.");
        Cmd("float", "Float", () => SetFixed(false));
        Tip("Float\nLets the selected component move: drag it in the graphics area (Shift+drag turns it).");
        Cmd("lightweight", "Lightweight", ToggleLightweight);
        Tip("Lightweight / Resolve\nShows the selected part's components from the assembly's copy (no rebuild: fast for big assemblies), or from the part again.");
        Cmd("configure", "Configure", () => _ = ConfigureComponent());
        Tip("Configure\nSets the selected component's part parameters for this instance: a design table's row (a screw's size), user-facing variables (its length). One part file, many configurations.");
        Sep();
        Cmd("open", "Open\nPart", () => { if (Asm?.Selected is { } id) OpenComponent(id); });
        Tip("Open Part\nOpens the selected component's part in a tab of its own (double-click does too).");
    }

    // ------------------------------------------------------------------------------------------
    // commands

    /// <summary>
    /// Insert Component (PropertyManager): the parts open in other tabs, or Browse… for files; linked, or embedded in
    /// the assembly. The first component is fixed at the origin, the others go beside the assembly.
    /// </summary>
    void InsertComponentCommand()
    {
        if (Asm == null) return;
        var page = new PmPage("Insert Component", "insertcomp");
        var s = page.Section("Part to Insert");
        page.Info(s, "Pick an open part (double-click inserts it), or Browse… for part files.");
        var list = new ListBox { MaxHeight = 280, MinHeight = 60 };
        foreach (var t in _parts.Where(t => t.Asm == null && t.Session.FilePath != null))
            list.Items.Add(new ListBoxItem { Content = TitleOf(t), Tag = t.Session.FilePath });
        if (list.Items.Count == 0) page.Info(s, "No saved part is open.");
        else s.Children.Add(list);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 6, 0, 0) };
        s.Children.Add(browse);
        var o = page.Section("Options");
        bool embed = false;
        page.Check(o, "Embed in the assembly", () => embed, v => embed = v);
        page.Info(o, "Embedded parts are kept inside the assembly: one file to keep or share (a small assembly, a hinge). Linked parts stay in their files.");
        void Close() { ShowPage(null); HideConfirmCorner(); }
        async Task InsertAndClose(IReadOnlyList<string> paths)
        {
            Close();
            await InsertParts(paths, embed);
        }
        async void Ok()
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is string path) await InsertAndClose(new[] { path });
            else Status("Pick a part in the list, or Browse… for one.");
        }
        list.DoubleTapped += (_, _) => Ok();
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Insert Component", FileTypeFilter = new[] { FcadType }, AllowMultiple = true,
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) await InsertAndClose(paths);
        };
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK: insert the part picked (Enter)", "Cancel (Esc)");
    }

    async Task InsertParts(IReadOnlyList<string> paths, bool embed)
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
                var c = await Task.Run(() => PartsIndexStore.Use(_ => a.Model.Insert(p, at, embed)));
                last = c.Id;
            }
            catch (Exception ex) { await Dialogs.Message(this, "Insert Component", $"{Path.GetFileName(p)} could not be inserted: {ex.Message}"); }
        }
        if (last == null) { a.AbandonChange(); Status("Nothing inserted."); return; }
        a.Selected = last;
        a.EndChange();
        Viewport.ZoomToFit();
        Status($"Inserted {paths.Count} component{(paths.Count == 1 ? "" : "s")}{(embed ? " (embedded)" : "")}. Drag a floating component (-) to move it; Shift+drag turns it.");
    }

    /// <summary>Where a new component goes: the origin for the first, else beside the assembly along +X.</summary>
    static Transform PlaceBeside(AssemblyModel m)
    {
        var b = Box3.Empty;
        foreach (var c in m.Doc.Components)
            if (m.DisplayOf(c) is { } pd && !pd.Mesh.Bounds.IsEmpty)
            {
                var t = c.Placement.ToTransform(); var mb = pd.Mesh.Bounds;
                for (int k = 0; k < 8; k++)
                    b.Add(t.Point(new Vec3((k & 1) == 0 ? mb.Min.X : mb.Max.X, (k & 2) == 0 ? mb.Min.Y : mb.Max.Y, (k & 4) == 0 ? mb.Min.Z : mb.Max.Z)));
            }
        return b.IsEmpty ? Transform.Identity : Transform.Translation(new Vec3(b.Max.X + 0.2 * Math.Max(10, b.Diagonal), 0, 0));
    }

    void SetFixed(bool fix)
    {
        if (Asm is not { } a || FindComponent(a.Selected) is not { } c) { Status("Select a component first."); return; }
        if (c.Fixed == fix) return;
        a.BeginChange();
        c.Fixed = fix;
        // floating now: the mates may move it (and fixed: others may have to move instead)
        if (a.Model.Doc.Mates.Count > 0) a.Model.SolveMates();
        a.EndChange();
    }

    void ToggleLightweight()
    {
        if (Asm is not { } a || FindComponent(a.Selected) is not { } c) { Status("Select a component first."); return; }
        a.BeginChange();
        var key = ComponentResolver.Key(c.Part);
        var light = !c.Lightweight;
        foreach (var k in a.Model.ComponentsOf(key)) k.Lightweight = light;
        // shown from the copy of its revision (made now if the assembly has none yet); resolved: built from the part
        if (light) a.Model.RefreshCaches();
        a.Model.LoadPart(key);
        a.EndChange();
        Status(light ? $"{c.Name}: lightweight (shown from the assembly's copy, no rebuild)." : $"{c.Name}: resolved from its part.");
    }

    /// <summary>
    /// Configures the selected component (or all of its part's instances): the part's user-facing parameters for it,
    /// its display built, the mates solved again (its faces may have moved).
    /// </summary>
    async Task ConfigureComponent()
    {
        if (Asm is not { } a || FindComponent(a.Selected) is not { } c) { Status("Select a component first."); return; }
        var partName = c.Name.Split('<')[0];
        if (a.Model.PartDocument(c) is not { } part) { await Dialogs.Message(this, "Configure", $"{c.Name}: its part was not found, so it cannot be configured."); return; }
        if (part.UserParameters().Count == 0)
        {
            await Dialogs.Message(this, "Configure", $"{partName} has no user-facing parameters. Open the part and mark variables as User in Equations, or give it a design table.");
            return;
        }
        var instances = a.Model.ComponentsOf(ComponentResolver.Key(c.Part)).ToList();
        var chosen = await Dialogs.Configure(this, part, c.Parameters, $"Configure {c.Name}",
            $"{partName} for this instance. Values left as the part's own follow the part when it changes.", instances.Count > 1 ? $"All {instances.Count} instances of {partName}" : null);
        if (chosen is not { } ch) return;
        var comps = ch.all ? instances : new List<Component> { c };
        var config = Configurations.KeyOf(part.Normalize(ch.values));
        if (comps.All(x => Configurations.KeyOf(x.Parameters) == config)) return;
        a.BeginChange();
        try { a.Model.Configure(comps, part, ch.values); }
        catch (Exception ex) { a.AbandonChange(); await Dialogs.Message(this, "Configure failed", ex.Message); return; }
        if (a.Model.Doc.Mates.Count > 0) a.Model.SolveMates();
        a.EndChange();
        var problem = a.Model.DisplayOf(c)?.Problem;
        Status($"{(comps.Count > 1 ? $"{comps.Count} instances of {partName}" : c.Name)}: {part.Label(ch.values)}" + (problem != null ? $" (could not be built: {problem})" : "."));
    }

    void DeleteComponent()
    {
        // (Delete with a mate selected in the tree deletes the mate)
        if (Asm is { SelectedMate: { } mateId } && _mating == null) { OnMateCommand(mateId, "delete"); return; }
        if (Asm is not { } a || FindComponent(a.Selected) is not { } c) return;
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
            Status("Saved " + path);
            return true;
        }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); return false; }
    }

    async Task<bool> ConfirmDiscardAssembly()
    {
        if (Asm is not { Dirty: true } a) return true;
        var r = await Dialogs.YesNoCancel(this, "SpryCAD", $"Save changes to {a.Title.TrimEnd(' ', '*')}?");
        if (r == null) return false;
        if (r == true) return await SaveAssembly(false);
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // IAssemblyHost (the graphics area's assembly mode)

    public void HoverComponent(string? id)
    {
        if (id == _asmHover) return;
        _asmHover = id;
        RefreshAssemblyScene();
    }

    /// <summary>A mate picked in the tree: its two faces (or origin planes) shown highlighted on the model.</summary>
    void SelectMate(string? id)
    {
        if (Asm is not { } a || (id == a.SelectedMate && a.Selected == null)) return;
        a.SelectedMate = id;
        a.Selected = null;
        RefreshAssemblyScene();
        _asmTree.SelectMateRow(id);
        if (SelectedMate() is { } m)
        {
            string Of(MateRef r) => r.Component == null ? r.Face + " Plane" : $"{FindComponent(r.Component)?.Name} ({r.Face})";
            Status($"{m.Name}: {Of(m.A)} and {Of(m.B)}{(m.Suppressed ? ", suppressed" : "")} (double-click to edit)");
        }
    }

    /// <summary>The mate selected in the tree (none while the Mate page shows its own picks).</summary>
    Mate? SelectedMate() => Asm is { SelectedMate: { } id } a && _mating == null ? a.Model.Doc.Mates.FirstOrDefault(m => m.Id == id) : null;

    public void SelectComponent(string? id)
    {
        if (Asm is not { } a || (a.Selected == id && a.SelectedMate == null)) return;
        a.SelectedMate = null;
        a.Selected = id;
        RefreshAssemblyScene();
        _asmTree.Select(id);
        if (FindComponent(id) is { } c)
            Status($"{c.Name}: {(c.Fixed ? "fixed" : "floating (drag to move, Shift+drag to turn)")}" +
                   (c.Part.Embedded != null ? ", embedded" : a.Model.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part))?.Path is { } p ? ", " + p : ""));
    }

    public void OpenComponent(string id)
    {
        if (FindComponent(id) is not { } c || Asm is not { } a) return;
        if (c.Part.Embedded != null) { Status($"{c.Name} is embedded in the assembly: editing embedded parts comes later."); return; }
        if (a.Model.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part))?.Path is not { } path) { Status($"{c.Name}: its part was not found."); return; }
        _ = OpenFileAsync(path);
    }

    public Component? FindComponent(string? id) => id != null ? Asm?.Model.Doc.Find(id) : null;
    public void BeginAssemblyChange() => Asm?.BeginChange();
    public void EndAssemblyChange() => Asm?.EndChange();
}
