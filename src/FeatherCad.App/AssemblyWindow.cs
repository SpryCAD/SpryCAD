using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FeatherCad.App.Session;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Assembly;
using FeatherCad.Kernel;
using Transform = FeatherCad.Kernel.Transform;

namespace FeatherCad.App;

/// <summary>
/// An assembly in a window of its own: the components (tree at the left), the graphics area, and the commands to
/// insert components (linked to their part files, or embedded for a self-contained assembly), fix or float them, show
/// them lightweight, and move floating ones (drag; Shift+drag turns). Parts are found by identity wherever they
/// went (<see cref="ComponentResolver"/>); a part that is not found shows from the assembly's cache and is listed,
/// never asked about. Double-clicking a linked component opens its part in the main window.
/// </summary>
public sealed class AssemblyWindow : Window
{
    readonly MainWindow _main;
    AssemblyModel _model;
    bool _dirty;
    readonly ViewportControl _vp = new();
    readonly ListBox _tree = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _notes = new() { Spacing = 4, Margin = new Thickness(8, 6) };
    readonly AssemblyMode _mode;
    string? _selected, _hover;
    readonly Stack<string> _undo = new(), _redo = new();
    Button _fix = null!, _float = null!, _light = null!, _delete = null!;
    bool _syncing;

    static readonly FilePickerFileType AsmType = new("FeatherCAD Assembly") { Patterns = new[] { "*" + AssemblyDocument.FileExtension } };
    static readonly FilePickerFileType PartType = new("FeatherCAD Part") { Patterns = new[] { "*.fcad" } };

    public string? FilePath => _model.Path;

    public AssemblyWindow(MainWindow main, AssemblyModel model)
    {
        _main = main;
        _model = model;
        Width = 1200; Height = 800; MinWidth = 700; MinHeight = 450;
        Icon = main.Icon;
        Background = new SolidColorBrush(Color.Parse("#FFF3F5F8"));
        _mode = new AssemblyMode(this);
        _vp.Mode = _mode;
        _vp.Camera.Rotation = Camera.StandardViews["Trimetric"];

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
        Button Cmd(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4), MinHeight = 0 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            bar.Children.Add(b);
            return b;
        }
        Cmd("Insert Component…", "Insert parts linked to their files (an edit to a part shows in every assembly using it)", () => _ = InsertDialog(embed: false));
        Cmd("Insert Embedded…", "Insert copies of parts kept inside the assembly: one self-contained file (a small assembly, a shared one)", () => _ = InsertDialog(embed: true));
        _fix = Cmd("Fix", "Fix the selected component in place", () => SetFixed(true));
        _float = Cmd("Float", "Let the selected component move (drag it; Shift+drag turns it)", () => SetFixed(false));
        _light = Cmd("Lightweight", "Show the selected part's components from the assembly's cache (no rebuild), or resolve them again", ToggleLightweight);
        _delete = Cmd("Delete", "Delete the selected component (Delete)", DeleteSelected);
        bar.Children.Add(new Border { Width = 12 });
        Cmd("Zoom to Fit", "Zoom to fit (F)", () => _vp.ZoomToFit());
        Cmd("Save", "Save the assembly (Ctrl+S)", () => _ = Save(false));
        Cmd("Save As…", "Save the assembly under another name (Ctrl+Shift+S)", () => _ = Save(true));

        _tree.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _selected = (_tree.SelectedItem as ListBoxItem)?.Tag as string;
            Refresh(tree: false);
        };
        _tree.DoubleTapped += (_, _) => { if (_selected != null) _ = EditPart(_selected); };

        var left = new DockPanel { Width = 280 };
        var notesHost = new ScrollViewer { Content = _notes, MaxHeight = 220 };
        DockPanel.SetDock(notesHost, Dock.Bottom);
        left.Children.Add(notesHost);
        left.Children.Add(_tree);
        var leftBorder = new Border { Child = left, Background = new SolidColorBrush(Color.Parse("#FFF5F7FA")), BorderBrush = new SolidColorBrush(Color.Parse("#FFD5DCE6")), BorderThickness = new Thickness(0, 0, 1, 0) };
        var statusBar = new Border { Child = _status, Height = 24, Padding = new Thickness(8, 0), Background = new SolidColorBrush(Color.Parse("#FFE9EDF2")) };
        var top = new Border { Child = bar, Background = new SolidColorBrush(Color.Parse("#FFF8FAFC")), BorderBrush = new SolidColorBrush(Color.Parse("#FFD5DCE6")), BorderThickness = new Thickness(0, 0, 0, 1) };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(statusBar, Dock.Bottom); DockPanel.SetDock(leftBorder, Dock.Left);
        root.Children.Add(top); root.Children.Add(statusBar); root.Children.Add(leftBorder); root.Children.Add(_vp);
        Content = root;

        KeyDown += OnKey;
        Closing += async (_, e) =>
        {
            if (!_dirty || _closingConfirmed) return;
            e.Cancel = true;
            var ans = await Dialogs.YesNoCancel(this, "FeatherCAD", $"Save changes to {Title_()}?");
            if (ans == null) return;
            if (ans == true && !await Save(false)) return;
            _closingConfirmed = true;
            Close();
        };
        Opened += (_, _) => { Refresh(); _vp.ZoomToFit(animate: false); _vp.Focus(); };
        Refresh();
        if (_model.Notes.Count > 0) Status(_model.Notes.Count == 1 ? _model.Notes[0] : $"{_model.Notes.Count} notes about the parts (at the bottom left)");
    }

    bool _closingConfirmed;

    string Title_() => _model.Path != null ? Path.GetFileName(_model.Path) : "Assembly";

    void Status(string s) => _status.Text = s;

    // ------------------------------------------------------------------------------------------
    // the scene and the tree

    /// <summary>A placement as the viewport's row-vector matrix.</summary>
    static Matrix4x4 ModelMatrix(Transform t) => new(
        (float)t.M00, (float)t.M10, (float)t.M20, 0,
        (float)t.M01, (float)t.M11, (float)t.M21, 0,
        (float)t.M02, (float)t.M12, (float)t.M22, 0,
        (float)t.T.X, (float)t.T.Y, (float)t.T.Z, 1);

    static Rgba Mix(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    /// <summary>Shows the model: the viewport's placed meshes, the tree, the notes, the buttons and the title.</summary>
    internal void Refresh(bool tree = true)
    {
        var inst = new List<MeshInstance>();
        foreach (var c in _model.Doc.Components)
        {
            if (c.Suppressed || _model.DisplayOf(c) is not { } pd || pd.Mesh.Indices.Length == 0) continue;
            var col = c.Color ?? pd.Color;
            var rgba = new Rgba((float)col[0], (float)col[1], (float)col[2]);
            if (c.Id == _selected) rgba = Mix(rgba, ViewportControl.SelectColor, 0.55f);
            else if (c.Id == _hover) rgba = Mix(rgba, new Rgba(1f, 0.8f, 0.5f), 0.35f);
            inst.Add(new MeshInstance(c.Id, pd.Mesh, ModelMatrix(c.Placement.ToTransform()), rgba));
        }
        _vp.Instances = inst;
        _vp.Redraw();
        if (tree) RefreshTree();
        var sel = _selected != null ? _model.Doc.Find(_selected) : null;
        _fix.IsEnabled = sel is { Fixed: false }; _float.IsEnabled = sel is { Fixed: true };
        _light.IsEnabled = sel != null; _delete.IsEnabled = sel != null;
        _light.Content = sel is { Lightweight: true } ? "Resolve" : "Lightweight";
        Title = $"{Title_()}{(_dirty ? " *" : "")} — FeatherCAD Assembly";
    }

    void RefreshTree()
    {
        _syncing = true;
        try
        {
            _tree.Items.Clear();
            foreach (var c in _model.Doc.Components)
            {
                var pd = _model.DisplayOf(c);
                var res = _model.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part));
                // SolidWorks' marks: (f) fixed, (-) floating; then what is special about its part
                var tags = new List<string>();
                if (c.Part.Embedded != null) tags.Add("embedded");
                if (pd?.Source == PartSource.Cache) tags.Add(pd.Problem != null ? "missing: cached copy" : "lightweight");
                if (pd?.Source == PartSource.None) tags.Add("missing");
                var text = new TextBlock { Text = $"{(c.Fixed ? "(f)" : "(-)")} {c.Name}" + (tags.Count > 0 ? $"   [{string.Join(", ", tags)}]" : "") };
                if (pd?.Problem != null) text.Foreground = new SolidColorBrush(Color.Parse("#FFC62828"));
                var item = new ListBoxItem { Content = text, Tag = c.Id, Padding = new Thickness(8, 3) };
                ToolTip.SetTip(item, c.Part.Embedded != null ? $"Embedded in the assembly ({c.Part.Embedded})"
                    : res?.Path ?? $"Not found (last at {c.Part.AbsolutePath ?? c.Part.Path})");
                _tree.Items.Add(item);
                if (c.Id == _selected) _tree.SelectedItem = item;
            }
        }
        finally { _syncing = false; }
        _notes.Children.Clear();
        foreach (var n in _model.Notes.TakeLast(12))
            _notes.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#FF5A6B85")) });
    }

    internal void Hover(string? id) { if (id != _hover) { _hover = id; Refresh(tree: false); } }

    internal void Select(string? id) { _selected = id; Refresh(); }

    internal Component? Find(string? id) => id != null ? _model.Doc.Find(id) : null;

    internal ViewportControl Viewport => _vp;

    // ------------------------------------------------------------------------------------------
    // editing (undo: snapshots of the assembly's JSON; embedded files only ever added, so kept)

    internal void BeginChange() { _undo.Push(_model.Doc.ToJson()); _redo.Clear(); }

    internal void Changed() { _dirty = true; Refresh(); }

    void Restore(string json)
    {
        var d = AssemblyDocument.FromJson(json);
        _model.Doc.Components = d.Components;
        _model.Doc.NextComponentId = d.NextComponentId;
        // a part none of the restored components uses any more is not shown; one used again is built again
        foreach (var key in _model.Parts.Keys.Where(k => !_model.ComponentsOf(k).Any()).ToList()) _model.Parts.Remove(key);
        foreach (var key in _model.Doc.Components.Select(c => ComponentResolver.Key(c.Part)).Distinct().Where(k => !_model.Parts.ContainsKey(k)).ToList())
            _model.LoadPart(key);
        if (_selected != null && _model.Doc.Find(_selected) == null) _selected = null;
        Changed();
    }

    void Undo() { if (_undo.Count == 0) return; _redo.Push(_model.Doc.ToJson()); Restore(_undo.Pop()); }
    void Redo() { if (_redo.Count == 0) return; _undo.Push(_model.Doc.ToJson()); Restore(_redo.Pop()); }

    async Task InsertDialog(bool embed)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = embed ? "Insert Embedded Component" : "Insert Component",
            FileTypeFilter = new[] { PartType },
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        BeginChange();
        Status($"Inserting {string.Join(", ", paths.Select(Path.GetFileName))}…");
        var ids = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                // beside what is there (the first one at the origin: it is the fixed ground)
                var at = PlaceBeside();
                var c = await Task.Run(() => _model.Insert(p, at, embed));
                ids.Add(c.Id);
            }
            catch (Exception ex) { await Dialogs.Message(this, "Insert Component", $"{Path.GetFileName(p)} could not be inserted: {ex.Message}"); }
        }
        if (ids.Count == 0) { _undo.Pop(); return; }
        _selected = ids[^1];
        Changed();
        Status($"Inserted {ids.Count} component{(ids.Count == 1 ? "" : "s")}" + (embed ? " (embedded)" : "") + ". Drag a floating component to move it.");
        _vp.ZoomToFit();
    }

    /// <summary>Where a new component goes: the origin for the first, else beside the assembly along +X.</summary>
    Transform PlaceBeside()
    {
        var b = Box3.Empty;
        foreach (var c in _model.Doc.Components)
            if (_model.DisplayOf(c) is { } pd && !pd.Mesh.Bounds.IsEmpty)
            {
                var t = c.Placement.ToTransform();
                for (int k = 0; k < 8; k++)
                {
                    var mb = pd.Mesh.Bounds;
                    b.Add(t.Point(new Vec3((k & 1) == 0 ? mb.Min.X : mb.Max.X, (k & 2) == 0 ? mb.Min.Y : mb.Max.Y, (k & 4) == 0 ? mb.Min.Z : mb.Max.Z)));
                }
            }
        return b.IsEmpty ? Transform.Identity : Transform.Translation(new Vec3(b.Max.X + 0.2 * Math.Max(10, b.Diagonal), 0, 0));
    }

    void SetFixed(bool fix)
    {
        if (Find(_selected) is not { } c || c.Fixed == fix) return;
        BeginChange();
        c.Fixed = fix;
        Changed();
    }

    void ToggleLightweight()
    {
        if (Find(_selected) is not { } c) return;
        BeginChange();
        var key = ComponentResolver.Key(c.Part);
        var light = !c.Lightweight;
        foreach (var k in _model.ComponentsOf(key)) k.Lightweight = light;
        // shown from the cache when there is one for its revision (saved once); else built as before
        if (light) _model.RefreshCaches();
        _model.LoadPart(key);
        Changed();
        Status(light ? $"{c.Name}: shown from the assembly's copy (no rebuild)." : $"{c.Name}: resolved from its part.");
    }

    void DeleteSelected()
    {
        if (Find(_selected) is not { } c) return;
        BeginChange();
        _model.Remove(c);
        _selected = null;
        Changed();
    }

    async Task EditPart(string id)
    {
        if (Find(id) is not { } c) return;
        var path = _model.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part))?.Path;
        if (c.Part.Embedded != null) { Status($"{c.Name} is embedded in the assembly: editing embedded parts comes in a later step."); return; }
        if (path == null) { Status($"{c.Name}: its part was not found."); return; }
        _main.Activate();
        await _main.OpenFileAsync(path);
    }

    /// <summary>Saves (asking where for a new assembly, or with <paramref name="saveAs"/>); false if cancelled or it failed.</summary>
    async Task<bool> Save(bool saveAs)
    {
        var path = _model.Path;
        if (saveAs || path == null)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Assembly As",
                DefaultExtension = "fcasm",
                SuggestedFileName = path != null ? Path.GetFileName(path) : "Assembly1" + AssemblyDocument.FileExtension,
                FileTypeChoices = new[] { AsmType },
            });
            path = f?.TryGetLocalPath();
            if (path == null) return false;
        }
        try
        {
            _model.Save(path);
            _dirty = false;
            RecentFiles.Add(path);
            Refresh();
            Status("Saved " + path);
            return true;
        }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); return false; }
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.S) { _ = Save(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None) { _vp.ZoomToFit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Select(null); e.Handled = true; }
    }

    // ------------------------------------------------------------------------------------------
    // opening

    /// <summary>A new, empty assembly.</summary>
    public static AssemblyWindow New(MainWindow main) => new(main, AssemblyModel.New());

    /// <summary>Opens an assembly file (parts found and built off the UI thread); null with a message if it cannot be read.</summary>
    public static async Task<AssemblyWindow?> Open(MainWindow main, string path)
    {
        try
        {
            var model = await Task.Run(() =>
            {
                var doc = AssemblyDocument.Load(path);
                return PartsIndexStore.Use(ix => AssemblyModel.Open(doc, path, ix));
            });
            RecentFiles.Add(path);
            var w = new AssemblyWindow(main, model);
            // references updated (parts found in new places): unsaved until saved, as the notes say
            if (model.Resolution.Changed) w._dirty = true;
            return w;
        }
        catch (Exception ex)
        {
            await Dialogs.Message(main, "Open Assembly", $"{Path.GetFileName(path)} could not be opened: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Picking and moving components: hover and click select; dragging a floating component moves it in the view's
/// plane through the point grabbed, Shift+drag turns it about its middle (left-right about the view's up, up-down
/// about its right). A fixed component cannot be dragged.
/// </summary>
sealed class AssemblyMode(AssemblyWindow w) : IViewportMode
{
    Component? _drag;
    Vec3 _grab, _normal, _center;
    Point _last;
    bool _turn, _moved;

    public Cursor? Cursor => null;

    public void PointerMove(ViewportControl vp, PointerInfo p)
    {
        if (_drag == null) { w.Hover(vp.PickInstance(p.X, p.Y)?.id); return; }
        if (!_moved) { w.BeginChange(); _moved = true; }
        var t = _drag.Placement.ToTransform();
        if (_turn)
        {
            double dx = p.X - _last.X, dy = p.Y - _last.Y;
            var up = Camera.V(vp.Camera.Up); var right = Camera.V(vp.Camera.Right);
            var r = Transform.Rotation(_center, up, dx * 0.01) * Transform.Rotation(_center, right, dy * 0.01);
            _drag.Placement = Placement.From(r * t);
        }
        else
        {
            // the grabbed point follows the cursor in the plane through it, square to the view
            var (o, d) = vp.Camera.Ray(p.X, p.Y);
            var den = Vec3.Dot(d, _normal);
            if (Math.Abs(den) < 1e-12) return;
            var hit = o + d * (Vec3.Dot(_grab - o, _normal) / den);
            _drag.Placement = Placement.From(Transform.Translation(hit - _grab) * t);
            _grab = hit;
        }
        _last = new Point(p.X, p.Y);
        w.Refresh(tree: false);
    }

    public void PointerDown(ViewportControl vp, PointerInfo p)
    {
        var hit = vp.PickInstance(p.X, p.Y);
        w.Select(hit?.id);
        if (hit is not { } h || w.Find(h.id) is not { Fixed: false } c) return;
        _drag = c; _grab = h.point; _moved = false; _turn = p.Shift;
        _normal = vp.Camera.Ray(p.X, p.Y).dir;
        _last = new Point(p.X, p.Y);
        // its middle: the placed box's center
        var pd = vp.Instances.FirstOrDefault(i => i.Id == c.Id).Mesh;
        var b = pd?.Bounds ?? Box3.Empty;
        _center = b.IsEmpty ? h.point : c.Placement.ToTransform().Point(b.Center);
    }

    public void PointerUp(ViewportControl vp, PointerInfo p)
    {
        if (_drag != null && _moved) w.Changed();
        _drag = null;
    }

    public bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods) => false;
    public void Draw(ViewportControl vp, DynamicGeometry g) { }
    public void UpdateOverlay(ViewportControl vp) { }
}
