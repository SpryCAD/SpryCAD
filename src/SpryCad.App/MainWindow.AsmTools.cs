using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Assembly;
using SpryCad.Kernel;
using Transform = SpryCad.Kernel.Transform;

namespace SpryCad.App;

/// <summary>
/// The assembly's tools beyond inserting and mating: how components show (hide, suppress, isolate, appearance, also
/// for a sub-assembly's components in this instance), configurations, sub-assemblies made, dissolved and changed,
/// components replaced, patterns and mirrors, exploded views, the bill of materials, mass properties, interference
/// detection, Pack and Go, and editing embedded parts and sub-assemblies in tabs of their own.
/// </summary>
public partial class MainWindow
{
    /// <summary>Isolated components (paths): only they (and what they hold) are shown; null: all.</summary>
    HashSet<string>? _isolate;
    /// <summary>Showing the exploded view (as far as <see cref="_explodeT"/>).</summary>
    bool _exploded;
    double _explodeT = 1;
    /// <summary>Parts shown red (an interference picked in its window).</summary>
    HashSet<string>? _clashes;

    /// <summary>The explode offsets to show (null: not exploded).</summary>
    Dictionary<string, Vec3>? ShownExplode(AssemblySession a) =>
        _exploded && a.Model.Doc.Explodes is [var v, ..] ? AssemblyModel.ExplodeOffsets(v, _explodeT) : null;

    /// <summary>Where a component is drawn: where it is, or where the exploded view moves it.</summary>
    static Transform Drawn(Occurrence o, Dictionary<string, Vec3>? explode) =>
        explode == null ? o.World : Transform.Translation(AssemblyModel.ExplodeOffsetOf(o.Path, explode)) * o.World;

    /// <summary>Left out of the graphics area by an isolation (neither isolated, nor holding nor held by what is).</summary>
    bool IsolatedOut(string path) =>
        _isolate != null && !_isolate.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal) || p.StartsWith(path + "/", StringComparison.Ordinal));

    /// <summary>The view states that belong to one assembly (isolation, explosion, clashes), cleared when the document changes.</summary>
    void ClearAssemblyView() { _isolate = null; _exploded = false; _explodeT = 1; _clashes = null; }

    // ------------------------------------------------------------------------------------------
    // the tree's commands

    /// <summary>A component's command (its path) from the tree's menu.</summary>
    void OnComponentCommand(string path, string cmd)
    {
        if (Asm is not { } a) return;
        a.Selected = path; a.SelectedMate = null;
        switch (cmd)
        {
            case "open": OpenComponent(path); break;
            case "fix": SetFixed(true); break;
            case "float": SetFixed(false); break;
            case "lightweight": ToggleLightweight(); break;
            case "configure": _ = ConfigureComponent(); break;
            case "flexible": SetFlexible(true); break;
            case "rigid": SetFlexible(false); break;
            case "delete": DeleteComponent(); break;
            case "hide" or "show": ShowComponent(path, cmd == "show"); break;
            case "suppress" or "unsuppress": SuppressComponent(path, cmd == "suppress"); break;
            case "isolate": Isolate(path); break;
            case "appearance": _ = Appearance(path); break;
            case "replace": _ = ReplaceComponent(path); break;
            case "form": FormSubAssemblyPage(path); break;
            case "dissolve": _ = DissolveSub(path); break;
            case "moveout": _ = MoveOut(path); break;
            default:
                if (cmd.StartsWith("moveinto:")) _ = MoveInto(path, cmd["moveinto:".Length..]);
                else if (cmd.StartsWith("subconfig:")) SetSubConfiguration(path, cmd["subconfig:".Length..] is { Length: > 0 } n ? n : null);
                break;
        }
    }

    /// <summary>A command of the assembly's row (configurations, tools).</summary>
    void OnAssemblyCommand(string cmd)
    {
        if (Asm is not { } a) return;
        switch (cmd)
        {
            case "config-add": _ = AddConfiguration(); break;
            case "config-rename": _ = RenameConfiguration(); break;
            case "bom": BomCommand(); break;
            case "mass": AssemblyMassCommand(); break;
            case "interference": InterferenceCommand(); break;
            case "explode": ExplodeCommand(); break;
            case "packgo": _ = PackAndGoCommand(); break;
            case "showall": ShowAll(); break;
            default:
                if (cmd.StartsWith("config-delete:")) _ = DeleteConfiguration(cmd["config-delete:".Length..]);
                else if (cmd.StartsWith("config:")) ActivateConfiguration(cmd["config:".Length..]);
                break;
        }
    }

    void ShowComponent(string path, bool show)
    {
        if (Asm is not { } a) return;
        a.BeginChange();
        a.Model.SetHidden(path, !show);
        a.EndChange();
        Status($"{a.Model.NameAt(path)} {(show ? "shown" : "hidden")}{(path.Contains('/') ? " (in this assembly only)" : "")}.");
    }

    /// <summary>Hide / Show on the Assembly tab: the selected component.</summary>
    void ToggleHidden()
    {
        if (Asm is not { Selected: { } p } a || a.Model.Find(p) is not { } o) { Status("Select a component first."); return; }
        ShowComponent(p, o.Hidden);
    }

    void SuppressComponent(string path, bool suppress)
    {
        if (Asm is not { } a) return;
        a.BeginChange();
        a.Model.SetSuppressed(path, suppress);
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        Status($"{a.Model.NameAt(path)} {(suppress ? "suppressed (not shown, solved or counted)" : "unsuppressed")}{(path.Contains('/') ? " in this assembly only" : "")}.");
    }

    void ShowAll()
    {
        if (Asm is not { } a) return;
        _isolate = null;
        a.BeginChange();
        foreach (var o in a.Model.Occurrences(true).Where(o => o.Hidden).ToList()) a.Model.SetHidden(o.Path, false);
        a.EndChange();
        Status("All components shown.");
    }

    /// <summary>Isolate: only the component shown (Exit Isolate, or isolating it again, shows everything).</summary>
    void Isolate(string? path = null)
    {
        if (Asm is not { } a) return;
        path ??= a.Selected;
        if (_isolate != null && (path == null || _isolate.Contains(path))) { _isolate = null; Status("Everything shown again."); RefreshAssemblyScene(); return; }
        if (path == null) { Status("Select a component to isolate."); return; }
        _isolate = new HashSet<string> { path };
        RefreshAssemblyScene();
        Status($"{a.Model.NameAt(path)} isolated: Isolate again shows everything.");
    }

    static readonly (string name, double[]? rgb)[] Swatches =
    {
        ("As its part", null), ("Steel", new[] { 0.74, 0.76, 0.80 }), ("Dark grey", new[] { 0.35, 0.36, 0.38 }), ("White", new[] { 0.95, 0.95, 0.95 }),
        ("Red", new[] { 0.85, 0.20, 0.18 }), ("Orange", new[] { 0.95, 0.55, 0.15 }), ("Yellow", new[] { 0.95, 0.82, 0.20 }), ("Green", new[] { 0.25, 0.65, 0.30 }),
        ("Blue", new[] { 0.20, 0.45, 0.85 }), ("Purple", new[] { 0.55, 0.35, 0.75 }), ("Brass", new[] { 0.80, 0.65, 0.30 }), ("Copper", new[] { 0.78, 0.45, 0.30 }),
    };

    /// <summary>A component's appearance: a color of its own (for a sub-assembly's component, in this instance), or its part's.</summary>
    async Task Appearance(string path)
    {
        if (Asm is not { } a || a.Model.Find(path) is not { } o) return;
        double[]? chosen = null; bool picked = false;
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 330 };
        var w = Dialogs.MakeWindow($"Appearance: {o.Component.Name}", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = path.Contains('/') ? "Its color in this assembly (its sub-assembly is not changed):" : "Its color in this assembly (its part is not changed):", TextWrapping = TextWrapping.Wrap },
                grid,
            },
        }, 360);
        foreach (var (name, rgb) in Swatches)
        {
            var c = rgb ?? o.Display?.Color ?? new[] { 0.74, 0.76, 0.80 };
            var b = new Button
            {
                Width = 76, Height = 52, Margin = new Thickness(3), Padding = new Thickness(2),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Border { Height = 22, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb((byte)(c[0] * 255), (byte)(c[1] * 255), (byte)(c[2] * 255))), BorderBrush = Palette.TextMuted, BorderThickness = new Thickness(rgb == null ? 1 : 0) },
                        new TextBlock { Text = name, FontSize = TypeScale.Small, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            b.Click += (_, _) => { chosen = rgb; picked = true; w.Close(); };
            grid.Children.Add(b);
        }
        await w.ShowDialog(this);
        if (!picked) return;
        a.BeginChange();
        a.Model.SetColor(path, chosen);
        a.EndChange();
    }

    void SetSubConfiguration(string path, string? name)
    {
        if (Asm is not { } a || a.Model.Find(path) is not { Rel: null, Component: { IsAssembly: true } c }) return;
        a.BeginChange();
        a.Model.SetSubConfiguration(c, name, defer: true);
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        Status($"{c.Name}: {name ?? "as saved"}.");
        _ = BuildPendingOf(a);
    }

    // ------------------------------------------------------------------------------------------
    // configurations

    void ActivateConfiguration(string name)
    {
        if (Asm is not { } a || name == a.Model.Doc.ActiveConfigurationName) return;
        a.BeginChange();
        if (!a.Model.ActivateConfiguration(name, defer: true)) { a.AbandonChange(); return; }
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        Status($"Configuration: {name}.");
        _ = BuildPendingOf(a);
    }

    async Task AddConfiguration()
    {
        if (Asm is not { } a) return;
        var name = await Dialogs.Input(this, "Add Configuration", "Name (made as the assembly is now, then made active):", "Config" + (a.Model.Doc.ConfigurationNames.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        a.BeginChange();
        if (!a.Model.Doc.AddConfiguration(name)) { a.AbandonChange(); Status($"There is a configuration named {name.Trim()} already."); return; }
        a.EndChange();
        Status($"Configuration {name.Trim()} added and active: suppress, hide or configure components for it; the other configurations keep theirs.");
    }

    async Task RenameConfiguration()
    {
        if (Asm is not { } a) return;
        var from = a.Model.Doc.ActiveConfigurationName;
        var name = await Dialogs.Input(this, "Rename Configuration", "New name:", from);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == from) return;
        a.BeginChange();
        if (!a.Model.Doc.RenameConfiguration(from, name)) { a.AbandonChange(); Status($"There is a configuration named {name.Trim()} already."); return; }
        a.EndChange();
    }

    async Task DeleteConfiguration(string name)
    {
        if (Asm is not { } a) return;
        if (!await Dialogs.YesNo(this, "Delete Configuration", $"Delete the configuration {name}?", "Delete")) return;
        a.BeginChange();
        if (!a.Model.Doc.DeleteConfiguration(name)) { a.AbandonChange(); return; }
        a.EndChange();
    }

    // ------------------------------------------------------------------------------------------
    // structure: new sub-assemblies, dissolving, moving between levels, replacing

    /// <summary>
    /// Form New Sub-assembly: components of the assembly (the selected one checked) made into a new sub-assembly, saved
    /// as a file of its own or embedded here; rigid or flexible.
    /// </summary>
    void FormSubAssemblyPage(string? path = null)
    {
        if (Asm is not { } a) return;
        var own = a.Model.Doc.Components.Where(c => c.Pattern == null && a.Model.PatternOf(c) == null).ToList();
        if (own.Count == 0) { Status("Insert components first."); return; }
        var pick = new HashSet<Component>();
        if ((path ?? a.Selected) is { } sel && a.Model.Doc.Find(sel.Split('/')[0]) is { } sc && own.Contains(sc)) pick.Add(sc);
        var page = new PmPage("Form New Sub-assembly", "formsub");
        var s = page.Section("Components");
        page.Info(s, "Check the components to put in the new sub-assembly. They keep their places; the mates among them go with them.");
        var list = new StackPanel { Spacing = 2 };
        foreach (var c in own)
        {
            var cb = new CheckBox { Content = c.Name, IsChecked = pick.Contains(c) };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) pick.Add(c); else pick.Remove(c); };
            list.Children.Add(cb);
        }
        s.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
        var o = page.Section("Options");
        bool embed = false, flexible = true;
        page.Check(o, "Embed it in this assembly (no file of its own)", () => embed, v => embed = v);
        page.Combo(o, "Solve as", new List<(bool, string)> { (true, "Flexible"), (false, "Rigid") }, () => flexible, v => flexible = v);
        page.Info(o, "Flexible keeps the components moving as they do now.");
        void Close() { ShowPage(null); HideConfirmCorner(); }
        async void Ok()
        {
            if (pick.Count == 0) { Status("Check the components to put in it."); return; }
            string? file = null;
            if (!embed)
            {
                var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save the New Sub-assembly", DefaultExtension = "sasm", SuggestedFileName = "SubAssembly" + AssemblyDocument.FileExtension,
                    FileTypeChoices = new[] { AssemblyType },
                    SuggestedStartLocation = a.FilePath != null ? await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(a.FilePath)!) : null,
                });
                file = f?.TryGetLocalPath();
                if (file == null) return;
                if (a.FilePath != null && PartIndex.SamePath(file, a.FilePath)) { Status("Pick another file than this assembly's."); return; }
            }
            Close();
            a.BeginChange();
            try
            {
                var name = file ?? Path.Combine(Path.GetDirectoryName(a.FilePath ?? Path.GetTempPath())!, "SubAssembly" + AssemblyDocument.FileExtension);
                var sub = a.Model.FormSubAssembly(pick, name, flexible, embed);
                if (a.Model.HasMates) a.Model.SolveMates();
                a.Selected = sub.Id;
                a.EndChange();
                Status($"{sub.Name}: a new {(flexible ? "flexible" : "rigid")} sub-assembly of {pick.Count} component{(pick.Count == 1 ? "" : "s")}{(embed ? ", embedded" : ", saved as " + file)}.");
            }
            catch (Exception ex) { App.Log(ex, "Form sub-assembly"); a.AbandonChange(); await Dialogs.Message(this, "Form New Sub-assembly", ex.Message); }
        }
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK: make the sub-assembly (Enter)", "Cancel (Esc)");
    }

    async Task DissolveSub(string path)
    {
        if (Asm is not { } a || a.Model.Doc.Find(path) is not { IsAssembly: true } s) return;
        a.BeginChange();
        try
        {
            var made = a.Model.Dissolve(s);
            if (a.Model.HasMates) a.Model.SolveMates();
            a.Selected = null;
            a.EndChange();
            Status($"{s.Name} dissolved into {made.Count} component{(made.Count == 1 ? "" : "s")}{(s.Flexible ? "" : " (locked together as it was rigid; its own mates came along suppressed)")}.");
        }
        catch (Exception ex) { App.Log(ex, "Dissolve"); a.AbandonChange(); await Dialogs.Message(this, "Dissolve Sub-assembly", ex.Message); }
    }

    /// <summary>A change that saves a sub-assembly's file: asked first (it cannot be undone here); the undo history cleared after.</summary>
    async Task<bool> ConfirmSubChange(AssemblySession a, Component s, string what)
    {
        var shared = s.Part.Embedded == null ? $" {s.Name}'s file is saved: every assembly using it gets the change." : "";
        return await Dialogs.YesNo(this, what, $"{what}?{shared} This cannot be undone.", what.Split(' ')[0]);
    }

    async Task MoveInto(string path, string subId)
    {
        if (Asm is not { } a || a.Model.Doc.Find(path) is not { } x || a.Model.Doc.Find(subId) is not { } s) return;
        if (!await ConfirmSubChange(a, s, $"Move {x.Name} into {s.Name}")) return;
        try
        {
            var np = a.Model.MoveInto(x, s);
            if (a.Model.HasMates) a.Model.SolveMates();
            a.Selected = np;
            a.ClearUndo();
            a.EndChange();
            Status($"{x.Name} moved into {s.Name}.");
        }
        catch (Exception ex) { App.Log(ex, "Move into sub-assembly"); await Dialogs.Message(this, "Move to Sub-assembly", ex.Message); }
    }

    async Task MoveOut(string path)
    {
        if (Asm is not { } a || a.Model.Find(path) is not { } o || a.Model.Doc.Find(path.Split('/')[0]) is not { } s) return;
        if (!await ConfirmSubChange(a, s, $"Move {o.Component.Name} out of {s.Name}")) return;
        try
        {
            var c = a.Model.MoveOut(path);
            if (a.Model.HasMates) a.Model.SolveMates();
            a.Selected = c.Id;
            a.ClearUndo();
            a.EndChange();
            Status($"{c.Name} moved out of {s.Name} into this assembly.");
        }
        catch (Exception ex) { App.Log(ex, "Move out of sub-assembly"); await Dialogs.Message(this, "Move Out of Sub-assembly", ex.Message); }
    }

    async Task ReplaceComponent(string? path = null)
    {
        if (Asm is not { } a) return;
        path ??= a.Selected;
        if (path == null || path.Contains('/') || a.Model.Doc.Find(path) is not { } c) { Status("Select one of the assembly's own components to replace."); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replace {c.Name} with",
            FileTypeFilter = new[] { new FilePickerFileType("SpryCAD Part or Assembly") { Patterns = new[] { "*" + Core.Model.CadDocument.FileExtension, "*" + AssemblyDocument.FileExtension } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } file) return;
        var instances = a.Model.ComponentsOf(ComponentResolver.Key(c.Part)).Count();
        var all = instances > 1 && await Dialogs.YesNo(this, "Replace Component", $"Replace all {instances} instances of {c.Name.Split('<')[0]}, or just {c.Name}?", "All instances", "Just this one");
        a.BeginChange();
        try
        {
            var done = await Task.Run(() => PartsIndexStore.Use(_ => a.Model.Replace(c, file, all)));
            if (a.Model.HasMates) a.Model.SolveMates();
            a.EndChange();
            var broken = a.Model.BrokenMates().Count;
            Status($"Replaced {done.Count} component{(done.Count == 1 ? "" : "s")} with {Path.GetFileNameWithoutExtension(file)}" +
                   (broken > 0 ? $": {broken} mate{(broken == 1 ? "" : "s")} lost a face (red under Mates: right-click, Repair)." : "."));
        }
        catch (Exception ex) { App.Log(ex, "Replace component"); a.AbandonChange(); await Dialogs.Message(this, "Replace Component", ex.Message); }
    }

    // ------------------------------------------------------------------------------------------
    // embedded parts and sub-assemblies, edited in tabs of their own

    /// <summary>Where the copies of embedded documents being edited are kept (one folder per edit).</summary>
    static string EmbeddedEditDir => Path.Combine(AutosaveDir, "embedded");

    static bool IsEmbeddedCopy(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(EmbeddedEditDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens an embedded part or sub-assembly in a tab of its own: Save puts it back into the assembly (every component
    /// of it showing the change); Save As makes it a file of its own (no longer tied to the assembly).
    /// </summary>
    async Task EditEmbedded(AssemblySession a, Occurrence o)
    {
        var name = o.Component.Part.Embedded!;
        // already open: shown
        if (_parts.FirstOrDefault(t => t.EmbeddedIn == a && t.EmbeddedName == name) is { } open) { await SwitchTo(open); return; }
        if (!o.Owner.Doc.Files.TryGetValue(name, out var bytes)) { Status("Its embedded copy is missing."); return; }
        if (o.Owner != a.Model) { Status($"{o.Component.Name} is embedded in a sub-assembly: edit the sub-assembly to change it."); return; }
        var dir = Path.Combine(EmbeddedEditDir, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(name));
        await File.WriteAllBytesAsync(file, bytes);
        var parent = _tab;
        if (o.Component.IsAssembly) await OpenAssembly(file);
        else await OpenFileAsync(file);
        var t = _parts.FirstOrDefault(x => (x.Asm?.FilePath ?? x.Session.FilePath) is { } p && PartIndex.SamePath(p, file));
        if (t == null) return;
        t.EmbeddedIn = a; t.EmbeddedName = name; t.EmbeddedPath = a.FilePath;
        UpdateTabHeader(t);
        Status($"Editing {Path.GetFileNameWithoutExtension(name)}, embedded in {a.Title.TrimEnd(' ', '*')}: Save puts it back into the assembly and saves the assembly.");
    }

    /// <summary>The assembly a tab's embedded document belongs to, if its tab is open (the one it was opened from, or a tab of its file).</summary>
    AssemblySession? OpenEmbeddingAssembly(PartTab t)
    {
        if (t.EmbeddedIn is { } e && _parts.Any(p => p.Asm == e)) return e;
        return t.EmbeddedPath is { } file ? _parts.FirstOrDefault(p => p.Asm?.FilePath is { } f && PartIndex.SamePath(f, file))?.Asm : null;
    }

    /// <summary>
    /// A tab editing an embedded part or sub-assembly saved: the edit saved into its assembly, and the assembly saved
    /// (as a virtual component's assembly is): to its file, or Save As when it has none yet. The assembly's tab is used
    /// when open, else its file is opened, changed and saved without a tab. When the assembly cannot take it (never
    /// saved and closed, or no longer holding it), the edit is offered Save As as a file of its own. Null when the tab is
    /// not editing an embedded document; else whether it was saved (false: cancelled or failed, the edit kept unsaved).
    /// </summary>
    async Task<bool?> SaveEmbedded(PartTab t)
    {
        if (t.EmbeddedName is not { } name) return null;
        var what = t.Asm != null ? "sub-assembly" : "part";
        var a = OpenEmbeddingAssembly(t);
        var background = false;
        if (a == null && t.EmbeddedPath is { } asmFile && File.Exists(asmFile))
        {
            try { a = await Task.Run(() => PartsIndexStore.Use(_ => AssemblySession.Open(asmFile))); background = true; }
            catch (Exception ex) { App.Log(ex, "Open assembly to save embedded"); a = null; }
        }
        if (a == null || !a.Model.Doc.Files.ContainsKey(name))
        {
            var why = a == null
                ? t.EmbeddedPath == null ? "its assembly was closed without being saved" : $"its assembly ({t.EmbeddedPath}) cannot be opened"
                : "its assembly no longer holds it";
            if (!await Dialogs.YesNo(this, "Save", $"This {what} cannot be saved into its assembly: {why}. Save it as a file of its own?", "Save As…")) return false;
            return t.Asm != null ? await SaveAssembly(true) : await SaveAs();
        }
        // the edit written out, then into the assembly
        var copy = t.Asm?.FilePath ?? t.Session.FilePath!;
        if (t.Asm != null) t.Asm.Save(copy); else t.Session.Save(copy);
        a.BeginChange();
        a.Model.UpdateEmbedded(name, await File.ReadAllBytesAsync(copy), defer: true);
        if (a.Model.HasMates) a.Model.SolveMates();
        a.EndChange();
        // the assembly saved with it
        var path = a.FilePath;
        if (path == null)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save the Assembly Holding the {char.ToUpper(what[0]) + what[1..]}", DefaultExtension = "sasm",
                SuggestedFileName = a.UntitledName + AssemblyDocument.FileExtension, FileTypeChoices = new[] { AssemblyType },
            });
            path = f?.TryGetLocalPath();
            if (path == null) { Status($"Put into {a.Title.TrimEnd(' ', '*')}, which is not saved yet: save it to keep the {what}."); return true; }
        }
        a.Save(path);
        RecentFiles.Add(path);
        t.EmbeddedIn = background ? null : a;
        t.EmbeddedPath = path;
        UpdateTabHeader(t);
        if (!background) _ = BuildPendingOf(a);
        Status($"Saved into {Path.GetFileName(path)}" + (background ? "." : " (the assembly saved with it)."));
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // patterns

    /// <summary>The pattern page's state while it is open (faces clicked pick its direction, axis or plane).</summary>
    sealed class Patterning
    {
        public required ComponentPattern Pattern;
        public ComponentPattern? Editing;
        public Action? Update;
    }

    Patterning? _patterning;

    /// <summary>Linear, circular or mirror pattern of components: added, or (<paramref name="editing"/>) changed.</summary>
    void PatternPage(PatternKind kind, ComponentPattern? editing = null)
    {
        if (Asm is not { } a) return;
        if (_mating != null || _motor != null || _patterning != null) { Status("Finish the open page first (OK or Cancel)."); return; }
        if (editing != null) kind = editing.Kind;
        var p = editing != null
            ? new ComponentPattern { Kind = kind, Seeds = new(editing.Seeds), Reference = editing.Reference.Clone(), Count = editing.Count, Spacing = editing.Spacing, Reverse = editing.Reverse, Symmetry = editing.Symmetry }
            : new ComponentPattern { Kind = kind, Reference = a.Model.RefTo(null, kind == PatternKind.Circular ? "Top" : "Right")!, Count = kind == PatternKind.Circular ? 4 : 3, Spacing = kind == PatternKind.Circular ? 90 : 50 };
        if (editing == null && a.Selected is { } sel && a.Model.Doc.Find(sel.Split('/')[0]) is { Pattern: null } sc) p.Seeds.Add(sc.Id);
        var st = new Patterning { Pattern = p, Editing = editing };
        _patterning = st;
        var title = kind switch { PatternKind.Linear => "Linear Component Pattern", PatternKind.Circular => "Circular Component Pattern", _ => "Mirror Components" };
        var page = new PmPage(editing != null ? $"Edit {editing.Name}" : title, kind switch { PatternKind.Linear => "lpattern", PatternKind.Circular => "cpattern", _ => "mirror" });
        var dirSec = page.Section(kind switch { PatternKind.Linear => "Direction", PatternKind.Circular => "Axis", _ => "Mirror Plane" });
        var refLbl = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Body };
        dirSec.Children.Add(new Border { Classes = { "pmsection" }, Child = refLbl, Padding = new Thickness(6, 4) });
        var planes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var pl in AssemblyModel.OriginPlanes)
        {
            var b = new Button { Content = pl, Padding = new Thickness(6, 2), MinHeight = 0 };
            ToolTip.SetTip(b, kind == PatternKind.Mirror ? $"Mirror across the assembly's {pl} plane" : $"Along the assembly's {pl} plane's normal");
            b.Click += (_, _) => { p.Reference = a.Model.RefTo(null, pl)!; st.Update?.Invoke(); };
            planes.Children.Add(b);
        }
        dirSec.Children.Add(planes);
        page.Info(dirSec, kind switch
        {
            PatternKind.Linear => "Click a planar face (its normal) or a round face (its axis), or pick an origin plane's normal.",
            PatternKind.Circular => "Click a round face (its axis), or pick an origin plane (its normal through the origin).",
            _ => "Click a planar face, or pick an origin plane.",
        });
        if (kind != PatternKind.Mirror) page.Check(dirSec, "Reverse direction", () => p.Reverse, v => p.Reverse = v);
        var opts = page.Section(kind == PatternKind.Mirror ? "Copies" : "Spacing and Instances");
        if (kind != PatternKind.Mirror)
        {
            page.Number(opts, kind == PatternKind.Linear ? "Spacing" : "Angle", () => p.Spacing.ToString(CultureInfo.InvariantCulture),
                v => { if (Core.Expr.TryEval(v, _ => null, out var x, out _)) p.Spacing = x; }, null, kind == PatternKind.Linear ? "mm" : "°");
            page.Number(opts, "Instances", () => p.Count.ToString(CultureInfo.InvariantCulture),
                v => { if (int.TryParse(v, out var n) && n >= 2 && n <= 500) p.Count = n; });
        }
        else
        {
            page.Combo(opts, "Part symmetric about", new List<(int, string)> { (-1, "Worked out from its shape"), (0, "Its YZ plane"), (1, "Its ZX plane"), (2, "Its XY plane") },
                () => p.Symmetry, v => p.Symmetry = v);
            page.Info(opts, "A mirrored copy is the same part turned over: right for parts symmetric about one of their planes (no opposite-hand part is made).");
        }
        var seeds = page.Section("Components to Pattern");
        var list = new StackPanel { Spacing = 2 };
        foreach (var c in a.Model.Doc.Components.Where(c => c.Pattern == null))
        {
            var cb = new CheckBox { Content = c.Name, IsChecked = p.Seeds.Contains(c.Id) };
            cb.IsCheckedChanged += (_, _) => { p.Seeds.Remove(c.Id); if (cb.IsChecked == true) p.Seeds.Add(c.Id); };
            list.Children.Add(cb);
        }
        seeds.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        st.Update = () =>
        {
            var r = p.Reference;
            refLbl.Text = r.Component == null ? $"{r.Face} Plane{(kind == PatternKind.Mirror ? "" : " (its normal)")}" : $"{a.Model.NameAt(r.Component)}: {(r.Kind == MateGeomKind.Axis ? "axis of" : "")} {r.Face}";
            RefreshAssemblyScene();
        };
        void Close()
        {
            _patterning = null; _hoverFace = null;
            ShowPage(null); HideConfirmCorner();
            RefreshAssemblyScene();
        }
        void Ok()
        {
            if (p.Seeds.Count == 0) { Status("Check the components to pattern."); return; }
            if (kind == PatternKind.Mirror && p.Reference.Kind != MateGeomKind.Plane) { Status("Mirror across a plane: a planar face or an origin plane."); return; }
            if (kind == PatternKind.Circular && p.Reference.Kind == MateGeomKind.Point) { Status("Pick an axis: a round face, or an origin plane."); return; }
            Close();
            a.BeginChange();
            try
            {
                if (editing != null)
                {
                    (editing.Seeds, editing.Reference, editing.Count, editing.Spacing, editing.Reverse, editing.Symmetry) = (p.Seeds, p.Reference, p.Count, p.Spacing, p.Reverse, p.Symmetry);
                    a.Model.UpdatePattern(editing);
                }
                else editing = a.Model.AddPattern(kind, p.Seeds.Select(a.Model.Doc.Find).OfType<Component>(), p.Reference, p.Count, p.Spacing, p.Reverse, p.Symmetry);
                if (a.Model.HasMates) a.Model.SolveMates();
                a.EndChange();
                Status($"{editing.Name}: {editing.Instances.Count} cop{(editing.Instances.Count == 1 ? "y" : "ies")}, placed by the pattern as their seeds move.");
            }
            catch (Exception ex) { App.Log(ex, "Pattern"); a.AbandonChange(); _ = Dialogs.Message(this, title, ex.Message); }
        }
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK (Enter)", "Cancel (Esc)");
        st.Update();
    }

    /// <summary>A face clicked while the pattern page is open: its direction, axis or plane.</summary>
    void PatternPick(string component, string face)
    {
        if (_patterning is not { } st || Asm is not { } a) return;
        if (a.Model.RefTo(component, face) is not { } r || r.Kind == MateGeomKind.Point) { Status("Pick a planar or round face."); return; }
        st.Pattern.Reference = r;
        st.Update?.Invoke();
    }

    void OnPatternCommand(string id, string cmd)
    {
        if (Asm is not { } a || a.Model.Doc.Patterns?.FirstOrDefault(p => p.Id == id) is not { } p) return;
        switch (cmd)
        {
            case "edit": PatternPage(p.Kind, p); break;
            case "delete" or "keep":
                a.BeginChange();
                a.Model.RemovePattern(p, keepCopies: cmd == "keep");
                a.EndChange();
                Status(cmd == "keep" ? $"{p.Name} deleted: its copies kept as components of their own (fixed)." : $"{p.Name} deleted with its copies.");
                break;
        }
    }

    // ------------------------------------------------------------------------------------------
    // exploded view

    /// <summary>
    /// Exploded View: steps that each move components along a direction (the selected components, X/Y/Z), or made at
    /// once from where the components are; played with a slider; shown or collapsed.
    /// </summary>
    void ExplodeCommand()
    {
        if (Asm is not { } a) return;
        if (_mating != null || _motor != null || _patterning != null) { Status("Finish the open page first (OK or Cancel)."); return; }
        a.BeginChange();
        // (Cancel puts the views back as they were)
        var before = System.Text.Json.JsonSerializer.Serialize(a.Model.Doc.Explodes, Core.Model.Serialization.Compact);
        var v = a.Model.Doc.Explodes is [var first, ..] ? first : null;
        if (v == null) { v = a.Model.AutoExplode(); (a.Model.Doc.Explodes ??= new()).Add(v); }
        _exploded = true; _explodeT = 1;
        var page = new PmPage("Exploded View", "explode");
        var steps = page.Section("Explode Steps");
        var stepList = new StackPanel { Spacing = 2 };
        steps.Children.Add(new ScrollViewer { Content = stepList, MaxHeight = 240 });
        void Fill()
        {
            stepList.Children.Clear();
            for (int i = 0; i < v.Steps.Count; i++)
            {
                var step = v.Steps[i];
                var del = Dialogs.DeleteButton("Delete this step");
                del.Click += (_, _) => { v.Steps.Remove(step); Fill(); RefreshAssemblyScene(); };
                var names = string.Join(", ", step.Components.Select(a.Model.NameAt));
                stepList.Children.Add(new DockPanel
                {
                    Children =
                    {
                        del,
                        new TextBlock { Text = $"{i + 1}. {names}: {step.Distance:0.#} mm", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) },
                    },
                });
                DockPanel.SetDock(del, Dock.Right);
            }
            if (v.Steps.Count == 0) stepList.Children.Add(new TextBlock { Text = "(no steps)", Foreground = Palette.TextMuted });
        }
        page.ButtonRow(steps, "Auto Explode (from where they are)", () => { v.Steps = a.Model.AutoExplode(v.Name).Steps; Fill(); RefreshAssemblyScene(); }, "explode");
        var add = page.Section("Add a Step");
        var dir = "Z"; double dist = 50;
        page.Combo(add, "Direction", new List<(string, string)> { ("X", "+X"), ("-X", "-X"), ("Y", "+Y"), ("-Y", "-Y"), ("Z", "+Z"), ("-Z", "-Z") }, () => dir, x => dir = x);
        page.Number(add, "Distance", () => dist.ToString(CultureInfo.InvariantCulture), x => { if (Core.Expr.TryEval(x, _ => null, out var d, out _)) dist = d; }, null, "mm");
        page.ButtonRow(add, "Add Step for the Selected Component", () =>
        {
            if (a.Selected is not { } sel) { Status("Select a component (click it, or its row in the tree) first."); return; }
            var d = dir.TrimStart('-') switch { "X" => Vec3.UnitX, "Y" => Vec3.UnitY, _ => Vec3.UnitZ } * (dir.StartsWith('-') ? -1 : 1);
            v.Steps.Add(new ExplodeStep { Components = new() { sel }, Direction = d, Distance = dist });
            Fill(); RefreshAssemblyScene();
        }, "explode");
        var play = page.Section("Show");
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = 1, SmallChange = 0.05, LargeChange = 0.25 };
        slider.ValueChanged += (_, e) => { _explodeT = e.NewValue; RefreshAssemblyScene(); };
        play.Children.Add(new TextBlock { Text = "Collapsed to exploded:", FontSize = TypeScale.Small, Foreground = Palette.TextMuted });
        play.Children.Add(slider);
        page.Check(play, "Show exploded after closing", () => _exploded, x => { _exploded = x; RefreshAssemblyScene(); });
        void Close() { ShowPage(null); HideConfirmCorner(); RefreshAssemblyScene(); }
        page.OnOk = () => { Close(); _explodeT = 1; a.EndChange(); Status(_exploded ? "Exploded (Exploded View again, or the tree's menu, to collapse; components cannot be dragged while exploded)." : "Collapsed."); RefreshAssemblyScene(); };
        page.OnCancel = () =>
        {
            a.Model.Doc.Explodes = System.Text.Json.JsonSerializer.Deserialize<List<ExplodeView>>(before, Core.Model.Serialization.Compact);
            _exploded = false;
            a.AbandonChange();
            Close();
        };
        ShowPage(page);
        ShowConfirmCorner(page.OnOk, page.OnCancel, "OK (Enter)", "Cancel (Esc)");
        Fill();
        RefreshAssemblyScene();
    }

    /// <summary>Explode / Collapse on the Assembly tab: the exploded view shown or not (made first if there is none).</summary>
    void ToggleExplode()
    {
        if (Asm is not { } a) return;
        if (a.Model.Doc.Explodes is not [_, ..]) { ExplodeCommand(); return; }
        _exploded = !_exploded; _explodeT = 1;
        RefreshAssemblyScene();
        Status(_exploded ? "Exploded (components cannot be dragged while exploded)." : "Collapsed.");
    }

    // ------------------------------------------------------------------------------------------
    // bill of materials, mass, interference

    Window ToolWindow(string title, Control content, double width = 560, double height = 480)
    {
        var w = new Window { Title = title, Width = width, Height = height, Content = new Border { Padding = new Thickness(12), Child = content }, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Dialogs.Compact(w);
        return w;
    }

    void BomCommand()
    {
        if (Asm is not { } a) return;
        var kind = BomKind.Indented;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        List<BomRow> rows = new();
        void Fill()
        {
            rows = Bom.Build(a.Model, kind);
            grid.Children.Clear(); grid.RowDefinitions.Clear();
            void Cell(int r, int c, string text, bool head = false, double indent = 0, bool right = false)
            {
                var tb = new SelectableTextBlock { Text = text, Margin = new Thickness(6 + indent, 2, 6, 2), FontWeight = head ? FontWeight.SemiBold : FontWeight.Normal, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
                Grid.SetRow(tb, r); Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] heads = { "Item", "Part", "Configuration", "Qty", "Material" };
            for (int c = 0; c < heads.Length; c++) Cell(0, c, heads[c], head: true, right: c == 3);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Cell(i + 1, 0, r.Item);
                Cell(i + 1, 1, (r.IsAssembly ? "▸ " : "") + r.Name, indent: 14 * r.Level);
                Cell(i + 1, 2, r.Configuration);
                Cell(i + 1, 3, r.Quantity.ToString(CultureInfo.InvariantCulture), right: true);
                Cell(i + 1, 4, r.Material ?? "");
            }
        }
        var kindBox = new ComboBox { ItemsSource = new[] { "Indented", "Top level only", "Parts only" }, SelectedIndex = 0, Width = 160 };
        kindBox.SelectionChanged += (_, _) => { kind = kindBox.SelectedIndex switch { 1 => BomKind.TopLevel, 2 => BomKind.PartsOnly, _ => BomKind.Indented }; Fill(); };
        var csv = new Button { Content = "Export CSV..." };
        var copy = new Button { Content = "Copy" };
        var close = new Button { Content = "Close", IsCancel = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center }, kindBox } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { csv, copy, close } };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(bar); dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = grid, Margin = new Thickness(0, 8, 0, 0) });
        var w = ToolWindow($"Bill of Materials: {a.Title.TrimEnd(' ', '*')}", dock, 640, 520);
        csv.Click += async (_, _) =>
        {
            var f = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Bill of Materials", DefaultExtension = "csv", SuggestedFileName = a.Title.TrimEnd(' ', '*') + " BOM.csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
            });
            if (f?.TryGetLocalPath() is { } p) { await File.WriteAllTextAsync(p, Bom.ToCsv(rows)); Status("Saved " + p); }
        };
        copy.Click += (_, _) => _ = Clipboard?.SetTextAsync(Bom.ToCsv(rows).Replace(',', '\t'));
        close.Click += (_, _) => w.Close();
        Fill();
        w.Show(this);
    }

    void AssemblyMassCommand()
    {
        if (Asm is not { } a) return;
        var text = new SelectableTextBlock { FontFamily = new FontFamily("Consolas, Menlo, monospace"), TextWrapping = TextWrapping.Wrap };
        string report = "";
        void Fill()
        {
            var r = AssemblyMass.Of(a.Model);
            var I = r.Inertia;
            report = $"Mass properties of {a.Title.TrimEnd(' ', '*')} ({r.Parts} parts, each with its own material; suppressed ones left out)\n\n" +
                     $"Mass = {r.Mass:0.###} g ({r.Mass / 1000:0.######} kg)\nVolume = {r.Volume:0.###} mm³\nSurface area = {r.Area:0.###} mm²\n\n" +
                     $"Center of mass (mm): X = {r.Center.X:0.###}, Y = {r.Center.Y:0.###}, Z = {r.Center.Z:0.###}\n\n" +
                     "Principal moments of inertia about the center of mass (g·mm²):\n" +
                     string.Concat(Enumerable.Range(0, 3).Select(i => $"  P{i + 1} = {r.Principal[i]:0.###}  along ({r.Axes[i].X:0.####}, {r.Axes[i].Y:0.####}, {r.Axes[i].Z:0.####})\n")) +
                     "\nMoments of inertia about the center of mass, assembly axes (g·mm²):\n" +
                     $"  Lxx = {I[0, 0]:0.###}  Lxy = {-I[0, 1]:0.###}  Lxz = {-I[0, 2]:0.###}\n  Lyx = {-I[1, 0]:0.###}  Lyy = {I[1, 1]:0.###}  Lyz = {-I[1, 2]:0.###}\n  Lzx = {-I[2, 0]:0.###}  Lzy = {-I[2, 1]:0.###}  Lzz = {I[2, 2]:0.###}\n" +
                     (r.Approximate.Count > 0 ? $"\nFrom their display meshes (shown from a copy, so not exact): {string.Join(", ", r.Approximate)}\n" : "");
            text.Text = report;
            Viewport.CenterOfMass = r.Mass > 0 ? (r.Center, r.Axes, Math.Max(5, a.Model.WorldBounds(a.Model.Doc.Components.FirstOrDefault()?.Id ?? "").Diagonal * 0.05)) : null;
            Viewport.Redraw();
        }
        var copy = new Button { Content = "Copy to Clipboard" };
        var recalc = new Button { Content = "Recalculate" };
        var close = new Button { Content = "Close", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { recalc, copy, close } };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = text });
        var w = ToolWindow("Mass Properties", dock, 600, 460);
        copy.Click += (_, _) => _ = Clipboard?.SetTextAsync(report);
        recalc.Click += (_, _) => Fill();
        close.Click += (_, _) => w.Close();
        w.Closed += (_, _) => { Viewport.CenterOfMass = null; Viewport.Redraw(); };
        try { Fill(); }
        catch (Exception ex) { App.Log(ex, "Assembly mass"); text.Text = "Could not be worked out: " + ex.Message; }
        w.Show(this);
    }

    void InterferenceCommand()
    {
        if (Asm is not { } a) return;
        var within = true;
        var list = new ListBox { MinHeight = 120 };
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted };
        var calc = new Button { Content = "Calculate", IsDefault = true };
        var withinBox = new CheckBox { Content = "Check parts of a sub-assembly against each other", IsChecked = true };
        withinBox.IsCheckedChanged += (_, _) => within = withinBox.IsChecked == true;
        var close = new Button { Content = "Close", IsCancel = true };
        CancellationTokenSource? cts = null;
        List<Clash> found = new();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= found.Count) { _clashes = null; RefreshAssemblyScene(); return; }
            var c = found[list.SelectedIndex];
            _clashes = new HashSet<string> { c.A, c.B };
            RefreshAssemblyScene();
            Status($"{a.Model.NameAt(c.A)} and {a.Model.NameAt(c.B)} overlap by {c.Volume:0.###} mm³ (shown red).");
        };
        calc.Click += async (_, _) =>
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            calc.IsEnabled = false;
            list.ItemsSource = null; _clashes = null; RefreshAssemblyScene();
            var items = Interference.Items(a.Model);
            var notes = new List<string>();
            try
            {
                found = await Task.Run(() => Interference.Find(items, notes, within,
                    (k, n) => Avalonia.Threading.Dispatcher.UIThread.Post(() => info.Text = $"Checking {k} of {n} pairs whose boxes overlap…"), token));
                list.ItemsSource = found.Select(c => $"{a.Model.NameAt(c.A)}  ↔  {a.Model.NameAt(c.B)}:  {c.Volume:0.###} mm³").ToList();
                info.Text = (found.Count == 0 ? $"No interference among {items.Count} parts." : $"{found.Count} interference{(found.Count == 1 ? "" : "s")}: pick one to see it.") +
                            (notes.Count > 0 ? "\n" + string.Join("\n", notes.Take(6)) + (notes.Count > 6 ? $"\n… and {notes.Count - 6} more" : "") : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Log(ex, "Interference"); info.Text = "Could not be checked: " + ex.Message; }
            finally { calc.IsEnabled = true; }
        };
        var top = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Finds parts that share volume (touching is not interference).", TextWrapping = TextWrapping.Wrap }, withinBox, calc } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { close } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom); DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(buttons); dock.Children.Add(info);
        dock.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(0, 8) });
        var w = ToolWindow("Interference Detection", dock, 560, 460);
        close.Click += (_, _) => w.Close();
        w.Closed += (_, _) => { cts?.Cancel(); _clashes = null; RefreshAssemblyScene(); };
        w.Show(this);
    }

    // ------------------------------------------------------------------------------------------
    // Pack and Go

    async Task PackAndGoCommand()
    {
        if (Asm is not { } a) return;
        if (a.FilePath == null || a.Dirty)
        {
            if (!await Dialogs.YesNo(this, "Pack and Go", "The assembly is copied as saved: save it first?", "Save")) return;
            if (!await SaveAssembly(false)) return;
        }
        string dest = Path.Combine(Path.GetDirectoryName(a.FilePath!)!, Path.GetFileNameWithoutExtension(a.FilePath!) + " Pack");
        string prefix = "", suffix = "";
        bool flatten = false, zip = false;
        var destBox = new TextBox { Text = dest };
        var browse = new Button { Content = "Browse…" };
        var prefixBox = new TextBox { Watermark = "Prefix" };
        var suffixBox = new TextBox { Watermark = "Suffix" };
        var flattenBox = new CheckBox { Content = "All files in one folder (else the folders as they are)" };
        var zipBox = new CheckBox { Content = "Also make a .zip of it" };
        var files = new TextBlock { FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap };
        void Preview()
        {
            try
            {
                var plan = PackAndGo.Plan(a.Model, destBox.Text ?? dest, prefixBox.Text ?? "", suffixBox.Text ?? "", flattenBox.IsChecked == true);
                files.Text = $"{plan.Count} files:\n" + string.Join("\n", plan.Select(i => Path.GetRelativePath(destBox.Text ?? dest, i.To)));
            }
            catch (Exception ex) { files.Text = ex.Message; }
        }
        foreach (var tb in new[] { destBox, prefixBox, suffixBox }) tb.TextChanged += (_, _) => Preview();
        flattenBox.IsCheckedChanged += (_, _) => Preview();
        var ok = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = ToolWindow("Pack and Go", new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    Spacing = 6, [DockPanel.DockProperty] = Dock.Top,
                    Children =
                    {
                        new TextBlock { Text = "Copies the assembly and every file it uses (parts and sub-assemblies) to a folder; the copies refer to each other.", TextWrapping = TextWrapping.Wrap },
                        new DockPanel { Children = { browse, destBox } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "Rename:", VerticalAlignment = VerticalAlignment.Center }, prefixBox, new TextBlock { Text = "name", VerticalAlignment = VerticalAlignment.Center }, suffixBox } },
                        flattenBox, zipBox,
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), [DockPanel.DockProperty] = Dock.Bottom, Children = { ok, cancel } },
                new ScrollViewer { Content = files, Margin = new Thickness(0, 8) },
            },
        }, 600, 520);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += async (_, _) =>
        {
            var folders = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pack and Go to" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } f) destBox.Text = f;
        };
        cancel.Click += (_, _) => w.Close();
        ok.Click += async (_, _) =>
        {
            dest = destBox.Text ?? dest; prefix = prefixBox.Text ?? ""; suffix = suffixBox.Text ?? ""; flatten = flattenBox.IsChecked == true; zip = zipBox.IsChecked == true;
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any() &&
                !await Dialogs.YesNo(w, "Pack and Go", $"{dest} is not empty: files of the same names are overwritten.", "Go on")) return;
            ok.IsEnabled = false;
            try
            {
                var plan = PackAndGo.Plan(a.Model, dest, prefix, suffix, flatten);
                await Task.Run(() => { PackAndGo.Run(plan); if (zip) PackAndGo.Zip(dest, dest.TrimEnd('\\', '/') + ".zip"); });
                w.Close();
                Status($"Packed {plan.Count} files into {dest}{(zip ? " (and its .zip)" : "")}.");
            }
            catch (Exception ex) { App.Log(ex, "Pack and Go"); await Dialogs.Message(w, "Pack and Go", ex.Message); ok.IsEnabled = true; }
        };
        Preview();
        await w.ShowDialog(this);
    }
}
