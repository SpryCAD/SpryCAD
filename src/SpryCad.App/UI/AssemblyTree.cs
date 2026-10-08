using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.Core.Assembly;

namespace SpryCad.App.UI;

/// <summary>
/// The FeatureManager tree of an assembly, drawn as the part's tree is: the assembly at the top, then its components
/// as SolidWorks shows them — (f) fixed or (-) floating, the name, and what is special about its part (embedded,
/// lightweight, missing: in red, shown from the assembly's copy), then the Mates folder (opened and closed by its
/// arrow), each mate with its type's icon, its name and what it joins (one the components could not be placed to
/// satisfy in red). Notes about the parts (found in new places, missing) underneath. Clicking selects; double-clicking
/// opens a component's part or edits a mate; right-click offers the component's or the mate's commands.
/// </summary>
public sealed class AssemblyTree : DockPanel
{
    readonly StackPanel _rows = new() { Spacing = 0 };
    readonly StackPanel _notes = new() { Spacing = 4, Margin = new Thickness(8, 6) };
    readonly Dictionary<string, Border> _rowById = new();
    string? _selected;        // a component's id, or "mate:" + a mate's
    bool _matesOpen = true;
    string? _doubleClicked;
    AssemblySession? _shown;

    /// <summary>A component (its id) was selected in the tree (null: the assembly row, a mate or nothing).</summary>
    public event Action<string?>? Selected;
    /// <summary>A mate's row was selected (its id).</summary>
    public event Action<string?>? MateSelected;
    public event Action<string>? Opened;
    /// <summary>A command from a component's context menu: "fix", "float", "lightweight", "open", "delete".</summary>
    public event Action<string, string>? Command;
    /// <summary>A command for a mate: "edit", "rename", "flip", "suppress", "delete".</summary>
    public event Action<string, string>? MateCommand;

    static IBrush Red => Palette.Danger;
    static IBrush Dim => Palette.TextMuted;

    public AssemblyTree()
    {
        var notes = new ScrollViewer { Content = _notes, MaxHeight = 200 };
        SetDock(notes, Dock.Bottom);
        Children.Add(notes);
        Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(2, 4) });
    }

    /// <summary>The mate selected in the tree (its id), if a mate's row is.</summary>
    public string? SelectedMate => _selected is { } s && s.StartsWith("mate:") ? s["mate:".Length..] : null;

    /// <summary>Highlights a component's row (null: no row), the rows kept.</summary>
    public void Select(string? id) => Highlight(id);

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
        if (SelectedMate is { } mate) MateSelected?.Invoke(mate);
        else Selected?.Invoke(key != "__asm" ? key : null);
    }

    /// <summary>Shows an assembly (its rows made again: assemblies have tens of components, not thousands).</summary>
    public void Show(AssemblySession s)
    {
        _shown = s;
        _rows.Children.Clear();
        _rowById.Clear();
        var m = s.Model;
        AddRow("__asm", 0, "assembly", s.Title.TrimEnd(' ', '*'), header: true);
        foreach (var c in m.Doc.Components)
        {
            var pd = m.DisplayOf(c);
            var tags = new List<string>();
            if (c.Part.Embedded != null) tags.Add("embedded");
            if (pd?.Source == PartSource.Cache) tags.Add(pd.Problem != null ? "missing: the assembly's copy" : "lightweight");
            if (pd?.Source == PartSource.None) tags.Add("missing");
            // being built after the assembly opened (shown meanwhile as it last was, if the assembly has a copy)
            if (pd?.Building == true) tags.Add(pd.Mesh.Indices.Length > 0 ? "updating" : "loading");
            if (c.Suppressed) tags.Add("suppressed");
            var res = m.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part));
            var tip = c.Part.Embedded != null ? $"Embedded in the assembly ({c.Part.Embedded})" : res?.Path ?? $"Not found (last at {c.Part.AbsolutePath ?? c.Part.Path})";
            // a configured part: its configuration after the name ("Screw<2> (M4, Length 12)")
            var config = c.Parameters != null ? m.ConfigLabel(c) : "";
            if (config.Length > 0) tip += "\nConfigured: " + config;
            AddRow(c.Id, 1, "part", $"{(c.Fixed ? "(f)" : "(-)")} {c.Name}" + (config.Length > 0 ? $" ({config})" : ""), note: tags.Count > 0 ? string.Join(", ", tags) : null,
                bad: pd?.Problem != null, dim: c.Suppressed, tip: tip, menu: MenuFor(c));
        }
        // the mates, in their folder
        if (m.Doc.Mates.Count > 0)
        {
            var anyOff = m.Doc.Mates.Any(x => !x.Suppressed && (m.LastSolve?.Errors.GetValueOrDefault(x.Id) ?? 0) > 1e-6);
            AddRow("__mates", 1, "mates", "Mates", folder: true, bad: anyOff && !_matesOpen,
                tip: anyOff && !_matesOpen ? "A mate is not satisfied (open the folder)" : null);
            if (_matesOpen)
            {
                string NameOf(MateRef r) => r.Component == null ? r.Face + " Plane" : m.Doc.Find(r.Component)?.Name ?? "?";
                foreach (var mate in m.Doc.Mates)
                {
                    var err = m.LastSolve?.Errors.GetValueOrDefault(mate.Id) ?? 0;
                    var off = !mate.Suppressed && err > 1e-6;
                    AddRow("mate:" + mate.Id, 2, IconFor(mate.Type), mate.Name + ValueOf(mate), note: $"{NameOf(mate.A)}, {NameOf(mate.B)}",
                        bad: off, dim: mate.Suppressed, menu: MenuFor(mate),
                        tip: off ? $"Not satisfied: off by {err:0.######} (it conflicts with other mates, or cannot be reached)"
                            : $"{mate.Type}{(mate.Suppressed ? ", suppressed" : "")}, {(mate.Aligned ? "aligned" : "anti-aligned")}. Double-click to edit, F2 to rename.");
                }
            }
        }
        // the selection survives the rows being made again (unless what it was is gone)
        Highlight(s.SelectedMate is { } sm && _rowById.ContainsKey("mate:" + sm) ? "mate:" + sm : s.Selected);
        _notes.Children.Clear();
        foreach (var n in s.Model.Notes.TakeLast(12))
            _notes.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Dim });
    }

    void ToggleMates()
    {
        _matesOpen = !_matesOpen;
        if (_shown != null) Show(_shown);
    }

    /// <summary>A row as the part's tree draws them: an arrow (folders), the icon, the name, a note in grey.</summary>
    void AddRow(string key, int indent, string icon, string text, string? note = null, bool header = false, bool folder = false,
        bool bad = false, bool dim = false, string? tip = null, ContextMenu? menu = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(indent * 14, 0, 0, 0) };
        if (folder)
        {
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(_matesOpen ? "M4 6l4 4 4-4" : "M6 4l4 4-4 4"), Stroke = Dim, StrokeThickness = 1.6,
                Width = 16, Height = 16, Stretch = Stretch.None,
            };
            var exp = new Border { Child = arrow, Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(exp, _matesOpen ? "Close the folder" : "Open the folder");
            exp.PointerPressed += (_, e) => { e.Handled = true; ToggleMates(); };
            sp.Children.Add(exp);
        }
        else sp.Children.Add(new Control { Width = 18 });
        sp.Children.Add(Icons.Make(icon, 16));
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body };
        if (header) tb.FontWeight = FontWeight.SemiBold;
        if (bad) tb.Foreground = Red;
        else if (dim) tb.Foreground = Palette.TextDisabled;
        sp.Children.Add(tb);
        if (note != null) sp.Children.Add(new TextBlock { Text = note, Foreground = Dim, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center });
        var row = new Border { Classes = { "treerow" }, Child = sp, Tag = key, ContextMenu = menu };
        if (tip != null) ToolTip.SetTip(row, tip);
        row.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(row).Properties;
            if (!pt.IsLeftButtonPressed && !pt.IsRightButtonPressed) return;
            if (folder)
            {
                if (pt.IsLeftButtonPressed && e.ClickCount >= 2) ToggleMates();
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
            else if (key != "__asm") Opened?.Invoke(key);
        };
        _rowById[key] = row;
        _rows.Children.Add(row);
    }

    static string IconFor(MateType t) => MainWindow.MateIcon(t);

    /// <summary>A mate's values as the tree shows them (distance, angle or their limits; ratio; lead).</summary>
    static string ValueOf(Mate m) => m.Type switch
    {
        MateType.Coincident when m.Min is { } lo && m.Max is { } hi => $" ({lo:0.###} to {hi:0.###} mm)",
        MateType.Coincident when m.Value != 0 => $" ({m.Value:0.###} mm)",
        MateType.Angle when m.Min is { } lo && m.Max is { } hi => $" ({lo:0.###} to {hi:0.###}°)",
        MateType.Angle => $" ({m.Value:0.###}°)",
        MateType.Gear => $" ({m.RatioA:0.###}:{m.RatioB:0.###}" + (m.Reverse ? " reversed)" : ")"),
        MateType.Screw => $" ({m.Value:0.###} mm/turn" + (m.Reverse ? " left hand)" : ")"),
        _ => "",
    };

    ContextMenu MenuFor(Mate mate)
    {
        MenuItem I(string header, string cmd, string icon, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, Icon = Icons.Make(icon, 16), InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null };
            mi.Click += (_, _) => MateCommand?.Invoke(mate.Id, cmd);
            return mi;
        }
        return new ContextMenu
        {
            Items =
            {
                I("_Edit Mate...", "edit", IconFor(mate.Type)), I("_Rename...", "rename", "rename", "F2"), new Separator(),
                I("_Flip Alignment", "flip", "align-opposite"), I(mate.Suppressed ? "_Unsuppress" : "_Suppress", "suppress", mate.Suppressed ? "mates" : "suppress"), new Separator(),
                I("_Delete", "delete", "delete"),
            },
        };
    }

    ContextMenu MenuFor(Component c)
    {
        MenuItem I(string header, string cmd, string icon, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = Icons.Make(icon, 16), IsEnabled = enabled };
            mi.Click += (_, _) => Command?.Invoke(c.Id, cmd);
            return mi;
        }
        return new ContextMenu
        {
            Items =
            {
                I("_Open Part", "open", "open", c.Part.Embedded == null),
                new Separator(),
                I("_Fix", "fix", "fix", !c.Fixed), I("F_loat", "float", "float", c.Fixed),
                I(c.Lightweight ? "Set to _Resolved" : "Set to _Lightweight", "lightweight", c.Lightweight ? "part" : "lightweight"),
                I("_Configure...", "configure", "configure"),
                new Separator(),
                I("_Delete", "delete", "delete"),
            },
        };
    }
}
