using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;

namespace SpryCad.App.UI;

/// <summary>
/// The FeatureManager tree of an assembly, drawn as the part's tree is: the assembly at the top (its active
/// configuration beside it), then its components as SolidWorks shows them — (f) fixed or (-) floating, the name, and
/// what is special about it (embedded, lightweight, hidden, suppressed, missing: in red, shown from the assembly's
/// copy), then the Mates folder (opened and closed by its arrow), each mate with its type's icon, its name and what it
/// joins (one the components could not be placed to satisfy, or whose reference is gone, in red), then the component
/// patterns. Sub-assemblies show what they hold under them (opened and closed by their arrow), marked rigid or flexible.
/// Notes about the parts (found in new places, missing) underneath. Clicking selects; double-clicking opens a
/// component's part (or sub-assembly) or edits a mate or pattern; right-click offers the commands of what it is on.
/// </summary>
public sealed class AssemblyTree : DockPanel
{
    readonly StackPanel _rows = new() { Spacing = 0, Margin = new Thickness(2, 4) };
    readonly StackPanel _notes = new() { Spacing = 4, Margin = new Thickness(8, 6) };
    readonly Dictionary<string, Border> _rowById = new();
    string? _selected;        // a component's path, "mate:" + a mate's id, or "pattern:" + a pattern's
    bool _matesOpen = true, _patternsOpen = true, _originOpen;
    /// <summary>The sub-assemblies shown open (by path).</summary>
    readonly HashSet<string> _open = new();
    string? _doubleClicked;
    AssemblySession? _shown;

    /// <summary>A component (its path) was selected in the tree (null: the assembly row, a mate or nothing).</summary>
    public event Action<string?>? Selected;
    /// <summary>A mate's row was selected (its id).</summary>
    public event Action<string?>? MateSelected;
    public event Action<string>? Opened;
    /// <summary>
    /// A command from a component's context menu (its path): "fix", "float", "flexible", "rigid", "lightweight", "open",
    /// "delete", "hide", "show", "suppress", "unsuppress", "isolate", "appearance", "replace", "form", "dissolve",
    /// "moveout", "moveinto:&lt;sub-assembly id&gt;", "subconfig:&lt;name&gt;" (empty: as saved).
    /// </summary>
    public event Action<string, string>? Command;
    /// <summary>A command for a mate: "edit", "rename", "flip", "suppress", "delete", "repair".</summary>
    public event Action<string, string>? MateCommand;
    /// <summary>A command for a component pattern: "edit", "delete", "keep" (delete it, its copies kept).</summary>
    public event Action<string, string>? PatternCommand;
    /// <summary>
    /// A command for the assembly (its row's menu): "config:&lt;name&gt;", "config-add", "config-rename", "config-delete",
    /// "bom", "mass", "interference", "explode", "packgo", "showall".
    /// </summary>
    public event Action<string>? AssemblyCommand;
    /// <summary>An origin plane's eye clicked ("Front", "Top" or "Right").</summary>
    public event Action<string>? PlaneVisibilityToggled;
    /// <summary>An origin row clicked: "Front"/"Top"/"Right", "AxisX"/"AxisY"/"AxisZ" or "Point".</summary>
    public event Action<string>? OriginPicked;

    static IBrush Red => Palette.Danger;
    static IBrush Dim => Palette.TextMuted;

    public AssemblyTree()
    {
        var notes = new ScrollViewer { Content = _notes, MaxHeight = 200 };
        SetDock(notes, Dock.Bottom);
        Children.Add(notes);
        Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }

    /// <summary>The mate selected in the tree (its id), if a mate's row is.</summary>
    public string? SelectedMate => _selected is { } s && s.StartsWith("mate:") ? s["mate:".Length..] : null;

    /// <summary>Highlights a component's row (null: no row), the rows kept; a sub-assembly's part shown by opening it.</summary>
    public void Select(string? id)
    {
        if (id != null && !_rowById.ContainsKey(id) && id.Contains('/') && _shown != null)
        {
            for (var i = id.IndexOf('/'); i > 0; i = id.IndexOf('/', i + 1)) _open.Add(id[..i]);
            Show(_shown);
        }
        Highlight(id);
    }

    /// <summary>Highlights a mate's row (null: no row).</summary>
    public void SelectMateRow(string? id) => Highlight(id != null ? "mate:" + id : null);

    void Highlight(string? key)
    {
        if (_selected != null && _rowById.TryGetValue(_selected, out var old)) old.Classes.Remove("selected");
        _selected = key;
        if (key != null && _rowById.TryGetValue(key, out var b)) b.Classes.Add("selected");
    }

    /// <summary>A row clicked: highlighted, and the component or mate it stands for reported.</summary>
    void Pick(string key)
    {
        Highlight(key);
        if (key.StartsWith("origin:")) { OriginPicked?.Invoke(key["origin:".Length..]); return; }
        if (SelectedMate is { } mate) MateSelected?.Invoke(mate);
        else Selected?.Invoke(key != "__asm" && !key.StartsWith("pattern:") ? key : null);
    }

    /// <summary>Shows an assembly (its rows made again: assemblies have tens of components, not thousands).</summary>
    public void Show(AssemblySession s)
    {
        _shown = s;
        _rows.Children.Clear();
        _rowById.Clear();
        var m = s.Model;
        var configs = m.Doc.ConfigurationNames;
        AddRow("__asm", 0, "assembly", s.Title.TrimEnd(' ', '*'), note: configs.Count > 1 ? m.Doc.ActiveConfigurationName : null, header: true, menu: AssemblyMenu(m),
            tip: configs.Count > 1 ? $"Configuration: {m.Doc.ActiveConfigurationName} (right-click to change)" : "Right-click for configurations, the bill of materials and more");
        // the origin, as the part's tree has it: a folder of the three planes (each with an eye), the axes and the point
        AddRow("__origin", 1, "folder", "Origin", folder: true, expander: (_originOpen, () => { _originOpen = !_originOpen; Show(s); }));
        if (_originOpen)
        {
            var empty = m.Doc.Components.Count == 0;
            foreach (var (std, label) in new[] { ("Front", "Front Plane"), ("Top", "Top Plane"), ("Right", "Right Plane") })
                AddRow("origin:" + std, 2, "plane", label, eye: m.Doc.PlaneVisibility?.TryGetValue(std, out var v) == true ? v : empty);
            foreach (var ax in new[] { "X", "Y", "Z" }) AddRow("origin:Axis" + ax, 2, "axis", ax + " Axis");
            AddRow("origin:Point", 2, "origin", "Origin Point");
        }
        foreach (var c in m.Doc.Components) ComponentRow(m, m.Find(c.Id)!, 1);
        // the mates, in their folder
        if (m.Doc.Mates.Count > 0)
        {
            var broken = m.BrokenMates().ToDictionary(x => x.mate, x => x.why);
            var anyOff = broken.Count > 0 || m.Doc.Mates.Any(x => !x.Suppressed && (m.LastSolve?.Errors.GetValueOrDefault(x.Id) ?? 0) > 1e-6);
            AddRow("__mates", 1, "mates", "Mates", folder: true, bad: anyOff && !_matesOpen, expander: (_matesOpen, () => { _matesOpen = !_matesOpen; Show(s); }),
                tip: anyOff && !_matesOpen ? "A mate is not satisfied (open the folder)" : null);
            if (_matesOpen)
            {
                string NameOf(MateRef r) => r.Component == null ? r.Face + " Plane" : m.NameAt(r.Component) + (m.Find(r.Component)?.Component.IsAssembly == true ? $" {r.Face} Plane" : "");
                foreach (var mate in m.Doc.Mates)
                {
                    var err = m.LastSolve?.Errors.GetValueOrDefault(mate.Id) ?? 0;
                    var why = broken.GetValueOrDefault(mate);
                    var off = why != null || !mate.Suppressed && err > 1e-6;
                    AddRow("mate:" + mate.Id, 2, IconFor(mate.Type), mate.Name + ValueOf(mate, m.Doc.Units), note: why != null ? $"reference missing: {why}" : $"{NameOf(mate.A)}, {NameOf(mate.B)}",
                        bad: off, dim: mate.Suppressed, menu: MenuFor(mate, why != null),
                        tip: why != null ? $"A reference is missing ({why}): right-click, Repair, to pick it again.\n{NameOf(mate.A)}, {NameOf(mate.B)}"
                            : off ? $"Not satisfied: off by {err:0.######} (it conflicts with other mates, or cannot be reached)"
                            : $"{mate.Type}{(mate.Suppressed ? ", suppressed" : "")}, {(mate.Aligned ? "aligned" : "anti-aligned")}. Double-click to edit, F2 to rename.");
                }
            }
        }
        // the component patterns
        if (m.Doc.Patterns is { Count: > 0 } patterns)
        {
            AddRow("__patterns", 1, "folder", "Patterns", folder: true, expander: (_patternsOpen, () => { _patternsOpen = !_patternsOpen; Show(s); }));
            if (_patternsOpen)
                foreach (var p in patterns)
                {
                    var seeds = string.Join(", ", p.Seeds.Select(id => m.Doc.Find(id)?.Name ?? id));
                    var what = p.Kind switch
                    {
                        PatternKind.Linear => $"{p.Count} × {m.Doc.Units.Length(p.Spacing)}",
                        PatternKind.Circular => $"{p.Count} × {p.Spacing:0.###}°",
                        _ => "mirrored",
                    };
                    AddRow("pattern:" + p.Id, 2, p.Kind switch { PatternKind.Linear => "lpattern", PatternKind.Circular => "cpattern", _ => "mirror" }, p.Name,
                        note: $"{seeds}: {what}", menu: MenuFor(p), tip: "Double-click to edit");
                }
        }
        // the selection survives the rows being made again (unless what it was is gone)
        Highlight(s.SelectedMate is { } sm && _rowById.ContainsKey("mate:" + sm) ? "mate:" + sm : s.Selected);
        _notes.Children.Clear();
        foreach (var n in s.Model.Notes.TakeLast(12))
            _notes.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Dim });
    }

    /// <summary>
    /// A component's row (the assembly's own, or one of a sub-assembly's: <paramref name="o"/>), and under an open
    /// sub-assembly what it holds.
    /// </summary>
    void ComponentRow(AssemblyModel m, Occurrence o, int indent)
    {
        var c = o.Component;
        var owner = o.Owner;
        var path = o.Path;
        var tags = new List<string>();
        string tip;
        bool bad;
        SubAssembly? sub = null;
        var res = owner.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part));
        var config = "";
        if (c.IsAssembly)
        {
            sub = owner.SubOf(c);
            tags.Add(c.Flexible ? "flexible" : "rigid");
            if (c.Configuration != null) tags.Add(c.Configuration);
            if (c.Part.Embedded != null) tags.Add("embedded");
            if (sub?.Source == SubSource.Cache) tags.Add("missing: the assembly's copy");
            else if (sub?.Model == null) tags.Add(sub?.Problem == "not found" || sub == null ? "missing" : "could not be shown");
            tip = (c.Part.Embedded != null ? $"Embedded in the assembly ({c.Part.Embedded})" : res?.Path ?? $"Not found (last at {c.Part.AbsolutePath ?? c.Part.Path})")
                + (sub?.Problem is { } why ? $"\nNot shown as its file is: {why}" : "")
                + (c.Flexible ? "\nFlexible: its own mates are solved in this assembly, so its parts move here." : "\nRigid: moves as one body, its parts where its own file has them.");
            // one of its mates (solved here) not satisfied
            bad = sub?.Model == null || sub.Source == SubSource.Cache || c.Flexible && m.LastSolve?.Errors.Any(e => e.Key.StartsWith(path + "/", StringComparison.Ordinal) && e.Value > 1e-6) == true;
        }
        else
        {
            var pd = owner.DisplayOf(c);
            if (c.Part.Embedded != null) tags.Add("embedded");
            if (c.Part.PinTag != null) tags.Add("pinned to " + c.Part.PinTag);
            else if (c.Part.Pin != null) tags.Add("pinned");
            if (pd?.Source == PartSource.Cache) tags.Add(pd.Problem != null ? "missing: the assembly's copy" : "lightweight");
            if (pd?.Source == PartSource.None) tags.Add("missing");
            // being built after the assembly opened (shown meanwhile as it last was, if the assembly has a copy)
            if (pd?.Building == true) tags.Add(pd.Mesh.Indices.Length > 0 ? "updating" : "loading");
            tip = c.Part.Embedded != null ? $"Embedded in the assembly ({c.Part.Embedded})" : res?.Path ?? $"Not found (last at {c.Part.AbsolutePath ?? c.Part.Path})";
            // a configured part: its configuration after the name ("Screw<2> (M4, Length 12)")
            config = c.Parameters != null ? owner.ConfigLabel(c) : "";
            if (config.Length > 0) tip += "\nConfigured: " + config;
            bad = pd?.Problem != null;
        }
        if (o.Suppressed) tags.Add("suppressed");
        else if (o.Hidden) tags.Add("hidden");
        if (o.Rel != null && o.Top.Overrides?.ContainsKey(o.Rel) == true) { tags.Add("changed here"); tip += "\nShown otherwise here than in its sub-assembly (hidden, suppressed or colored for this instance)."; }
        if (o.Rel == null && c.Pattern != null && m.Doc.Patterns?.FirstOrDefault(p => p.Id == c.Pattern) is { } pat) { tags.Add("copy: " + pat.Name); tip += $"\nA copy made by {pat.Name}: placed by it."; }
        var open = sub?.Model != null && _open.Contains(path);
        AddRow(path, indent, c.IsAssembly ? "assembly" : "part", $"{(c.Fixed ? "(f)" : "(-)")} {c.Name}" + (config.Length > 0 ? $" ({config})" : ""),
            note: tags.Count > 0 ? string.Join(", ", tags) : null, bad: bad, dim: o.Suppressed || o.Hidden, tip: tip, menu: MenuFor(m, o),
            expander: sub?.Model != null ? (open, () => { if (!_open.Remove(path)) _open.Add(path); if (_shown != null) Show(_shown); }) : null);
        if (open)
            foreach (var cc in sub!.Model!.Doc.Components)
                if (m.Find(path + "/" + cc.Id) is { } co) ComponentRow(m, co, indent + 1);
    }

    /// <summary>A row as the part's tree draws them: an arrow (folders, sub-assemblies), the icon, the name, a note in grey.</summary>
    void AddRow(string key, int indent, string icon, string text, string? note = null, bool header = false, bool folder = false,
        bool bad = false, bool dim = false, string? tip = null, ContextMenu? menu = null, (bool open, Action toggle)? expander = null, bool? eye = null)
    {
        // as the part's tree (UI/TreeRow.cs): as wide as the tree, the grey note trimmed first when the row is too
        // long, then the name; the whole of it on the row's tip
        var sp = new TreeRowPanel { Margin = new Thickness(indent * 14, 0, 0, 0) };
        sp.Children.Add(expander is { } ex ? TreeRow.Expander(ex.open, ex.toggle) : TreeRow.Spacer());
        sp.Children.Add(Icons.Make(icon, 16));
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body, TextTrimming = TextTrimming.CharacterEllipsis };
        if (header) tb.FontWeight = FontWeight.SemiBold;
        if (bad) tb.Foreground = Red;
        else if (dim) tb.Foreground = Palette.TextDisabled;
        sp.Children.Add(tb);
        if (note != null)
        {
            var nb = new TextBlock { Text = note, Foreground = Dim, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            sp.Children.Add(nb);
            sp.Shrink.Add(nb);
        }
        sp.Shrink.Add(tb);
        if (eye is { } shown) sp.Children.Add(EyeButton(key, shown));
        var row = new Border { Classes = { "treerow" }, Child = sp, Tag = key, ContextMenu = menu };
        if (tip != null || note != null) ToolTip.SetTip(row, (note != null ? $"{text}  {note}" : text) + (tip != null ? "\n" + tip : ""));
        row.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(row).Properties;
            if (!pt.IsLeftButtonPressed && !pt.IsRightButtonPressed) return;
            if (folder)
            {
                if (pt.IsLeftButtonPressed && e.ClickCount >= 2) expander?.toggle();
                e.Handled = pt.IsLeftButtonPressed;
                return;
            }
            if (pt.IsLeftButtonPressed && e.ClickCount >= 2)
            {
                // acted on at the release: a page opened now would take the release (a button under the pointer)
                _doubleClicked = key;
                e.Handled = true;
                return;
            }
            Pick(key);
            // (a right-click goes on to open the row's menu)
            e.Handled = pt.IsLeftButtonPressed;
        };
        row.PointerReleased += (_, e) =>
        {
            if (_doubleClicked != key) return;
            _doubleClicked = null;
            e.Handled = true;
            if (key.StartsWith("mate:")) MateCommand?.Invoke(key["mate:".Length..], "edit");
            else if (key.StartsWith("pattern:")) PatternCommand?.Invoke(key["pattern:".Length..], "edit");
            else if (key != "__asm") Opened?.Invoke(key);
        };
        _rowById[key] = row;
        _rows.Children.Add(row);
    }

    /// <summary>An origin plane's eye: shown or hidden in the graphics area, a click the other way round.</summary>
    Control EyeButton(string key, bool shown)
    {
        var b = new Border
        {
            Child = Icons.Make(shown ? "show" : "hide", 14), Padding = new Thickness(2), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(2, 0, 0, 0),
            Opacity = shown ? 1 : 0.6,
        };
        ToolTip.SetTip(b, shown ? "Hide the plane" : "Show the plane");
        b.PointerEntered += (_, _) => b.Background = Palette.SelectedBg;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            e.Handled = true; // not a row press
            PlaneVisibilityToggled?.Invoke(key["origin:".Length..]);
        };
        return b;
    }

    static string IconFor(MateType t) => MainWindow.MateIcon(t);

    /// <summary>A mate's values as the tree shows them (distance, angle or their limits; ratio; lead).</summary>
    static string ValueOf(Mate m, UnitSystem u) => m.Type switch
    {
        MateType.Coincident when m.Min is { } lo && m.Max is { } hi => $" ({u.Number(lo)} to {u.Length(hi)})",
        MateType.Coincident when m.Value != 0 => $" ({u.Length(m.Value)})",
        MateType.Angle when m.Min is { } lo && m.Max is { } hi => $" ({lo:0.###} to {hi:0.###}°)",
        MateType.Angle => $" ({m.Value:0.###}°)",
        MateType.Gear => $" ({m.RatioA:0.###}:{m.RatioB:0.###}" + (m.Reverse ? " reversed)" : ")"),
        MateType.Screw => $" ({u.Length(m.Value)}/turn" + (m.Reverse ? " left hand)" : ")"),
        _ => "",
    };

    static MenuItem Item(string header, string icon, Action click, bool enabled = true, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, Icon = Icons.Make(icon, 16), IsEnabled = enabled, InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null };
        mi.Click += (_, _) => click();
        return mi;
    }

    ContextMenu MenuFor(Mate mate, bool broken)
    {
        MenuItem I(string header, string cmd, string icon, string? gesture = null) => Item(header, icon, () => MateCommand?.Invoke(mate.Id, cmd), gesture: gesture);
        var menu = new ContextMenu();
        if (broken) { menu.Items.Add(I("Re_pair (pick the missing reference again)...", "repair", "warning")); menu.Items.Add(new Separator()); }
        foreach (var i in new object[]
                 {
                     I("_Edit Mate...", "edit", IconFor(mate.Type)), I("_Rename...", "rename", "rename", "F2"), new Separator(),
                     I("_Flip Alignment", "flip", "align-opposite"), I(mate.Suppressed ? "_Unsuppress" : "_Suppress", "suppress", mate.Suppressed ? "mates" : "suppress"), new Separator(),
                     I("_Delete", "delete", "delete"),
                 })
            menu.Items.Add(i);
        return menu;
    }

    ContextMenu MenuFor(ComponentPattern p) => new()
    {
        Items =
        {
            Item("_Edit Pattern...", p.Kind switch { PatternKind.Linear => "lpattern", PatternKind.Circular => "cpattern", _ => "mirror" }, () => PatternCommand?.Invoke(p.Id, "edit")),
            new Separator(),
            Item("_Delete (and its copies)", "delete", () => PatternCommand?.Invoke(p.Id, "delete")),
            Item("Delete, _keeping the copies", "delete", () => PatternCommand?.Invoke(p.Id, "keep")),
        },
    };

    /// <summary>The assembly row's menu: configurations, then the assembly's tools.</summary>
    ContextMenu AssemblyMenu(AssemblyModel m)
    {
        MenuItem A(string header, string cmd, string icon, bool enabled = true) => Item(header, icon, () => AssemblyCommand?.Invoke(cmd), enabled);
        var configs = new MenuItem { Header = "_Configurations", Icon = Icons.Make("configure", 16) };
        foreach (var name in m.Doc.ConfigurationNames)
        {
            var mi = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = name == m.Doc.ActiveConfigurationName };
            mi.Click += (_, _) => AssemblyCommand?.Invoke("config:" + name);
            configs.Items.Add(mi);
        }
        configs.Items.Add(new Separator());
        configs.Items.Add(A("_Add Configuration...", "config-add", "configure"));
        configs.Items.Add(A("_Rename Active...", "config-rename", "rename"));
        var del = new MenuItem { Header = "_Delete", Icon = Icons.Make("delete", 16), IsEnabled = m.Doc.ConfigurationNames.Count > 1 };
        foreach (var name in m.Doc.ConfigurationNames.Where(n => n != m.Doc.ActiveConfigurationName))
            del.Items.Add(A(name, "config-delete:" + name, "delete"));
        configs.Items.Add(del);
        return new ContextMenu
        {
            Items =
            {
                configs, new Separator(),
                A("_Bill of Materials", "bom", "bom"), A("_Mass Properties", "mass", "mass"), A("_Interference Detection", "interference", "interference"),
                A("_Exploded View...", "explode", "explode"), new Separator(),
                A("_Show All Components", "showall", "show"), A("_Pack and Go...", "packgo", "packgo"),
            },
        };
    }

    /// <summary>
    /// A component's menu: the assembly's own components have every command; a sub-assembly's have those that change how
    /// this instance shows them (and moving out, for a sub-assembly's own components).
    /// </summary>
    ContextMenu MenuFor(AssemblyModel m, Occurrence o)
    {
        var c = o.Component;
        var path = o.Path;
        MenuItem I(string header, string cmd, string icon, bool enabled = true) => Item(header, icon, () => Command?.Invoke(path, cmd), enabled);
        var menu = new ContextMenu();
        void Add(params object[] items) { foreach (var i in items) menu.Items.Add(i); }
        var embedded = c.Part.Embedded != null;
        Add(I(c.IsAssembly ? (embedded ? "_Edit Sub-assembly" : "_Open Sub-assembly") : embedded ? "_Edit Part" : "_Open Part", "open", "open"), new Separator());
        // how it shows (any component)
        Add(o.Hidden ? I("_Show", "show", "show") : I("_Hide", "hide", "hide"),
            o.Suppressed ? I("_Unsuppress", "unsuppress", "suppress") : I("_Suppress", "suppress", "suppress"),
            I("_Isolate", "isolate", "isolate"), I("_Appearance...", "appearance", "appearance"));
        if (o.Rel != null)
        {
            // a sub-assembly's component: what this instance shows; moved out (one level down only)
            if (!o.Rel.Contains('/')) Add(new Separator(), I("_Move Out of Sub-assembly", "moveout", "dissolve"));
            return menu;
        }
        Add(new Separator(), I("_Fix", "fix", "fix", !c.Fixed && c.Pattern == null), I("F_loat", "float", "float", c.Fixed && c.Pattern == null));
        if (c.IsAssembly)
        {
            Add(I("Make Fle_xible", "flexible", "mates", !c.Flexible), I("Make _Rigid", "rigid", "mate-lock", c.Flexible));
            var configs = m.ConfigurationsOf(c);
            if (configs.Count > 1)
            {
                var cm = new MenuItem { Header = "Con_figuration", Icon = Icons.Make("configure", 16) };
                var saved = new MenuItem { Header = "As saved", ToggleType = MenuItemToggleType.Radio, IsChecked = c.Configuration == null };
                saved.Click += (_, _) => Command?.Invoke(path, "subconfig:");
                cm.Items.Add(saved);
                foreach (var name in configs)
                {
                    var mi = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = c.Configuration == name };
                    mi.Click += (_, _) => Command?.Invoke(path, "subconfig:" + name);
                    cm.Items.Add(mi);
                }
                Add(cm);
            }
            Add(I("_Dissolve Sub-assembly", "dissolve", "dissolve", m.SubOf(c)?.Model != null));
        }
        else
        {
            var configure = I("_Configure...", "configure", "configure");
            // Configure only for a part with something to configure (a design table, user-facing variables); asked when
            // the menu opens, not as the tree is made, since finding the part may read its file
            menu.Opening += (_, _) => configure.IsEnabled = _shown?.Model.PartDocument(c)?.UserParameters().Count > 0;
            Add(I(c.Lightweight ? "Set to _Resolved" : "Set to _Lightweight", "lightweight", c.Lightweight ? "part" : "lightweight"), configure);
            Add(I("_Pin to Revision...", "pin", "history"), I("Follow _Latest", "unpin", "history", c.Part.Pin != null || c.Part.PinTag != null));
        }
        Add(new Separator(), I("Re_place Component...", "replace", "replace"),
            embedded ? I("Make E_xternal...", "makeexternal", "save") : I("Make _Virtual...", "makevirtual", "import"),
            I("_Form New Sub-assembly...", "form", "formsub", c.Pattern == null));
        var subs = m.Doc.Components.Where(x => x != c && x.IsAssembly && m.SubOf(x) is { Model: not null, Source: not SubSource.Cache }).ToList();
        if (subs.Count > 0 && c.Pattern == null)
        {
            var mv = new MenuItem { Header = "_Move to Sub-assembly", Icon = Icons.Make("formsub", 16) };
            foreach (var sa in subs) mv.Items.Add(Item(sa.Name, "assembly", () => Command?.Invoke(path, "moveinto:" + sa.Id)));
            Add(mv);
        }
        Add(new Separator(), I("_Delete", "delete", "delete"));
        return menu;
    }
}
