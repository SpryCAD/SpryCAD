using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.Core.Assembly;

namespace SpryCad.App.UI;

/// <summary>
/// The FeatureManager tree of an assembly: the assembly at the top, then its components as SolidWorks shows them —
/// (f) fixed or (-) floating, the name, and what is special about its part (embedded, lightweight, missing: in red,
/// shown from the assembly's copy), then the Mates folder (a mate the components could not be placed to satisfy in
/// red). Notes about the parts (found in new places, missing) underneath. Clicking selects; double-clicking opens the
/// part; right-click offers the component's or the mate's commands.
/// </summary>
public sealed class AssemblyTree : DockPanel
{
    readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly StackPanel _notes = new() { Spacing = 4, Margin = new Thickness(8, 6) };
    bool _syncing;

    /// <summary>A component (its id) was selected in the tree (null: the assembly row or nothing).</summary>
    public event Action<string?>? Selected;
    public event Action<string>? Opened;
    /// <summary>A command from a component's context menu: "fix", "float", "lightweight", "open", "delete".</summary>
    public event Action<string, string>? Command;
    /// <summary>A command from a mate's context menu: "flip", "suppress", "delete".</summary>
    public event Action<string, string>? MateCommand;

    static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FFC62828"));
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#FF5A6B85"));

    public AssemblyTree()
    {
        var notes = new ScrollViewer { Content = _notes, MaxHeight = 200 };
        SetDock(notes, Dock.Bottom);
        Children.Add(notes);
        Children.Add(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var id = (_list.SelectedItem as ListBoxItem)?.Tag is string t && !t.StartsWith("mate:") ? t : null;
            // after the list has finished changing its selection: a listener may make the rows again, which the list
            // cannot take in the middle of it (index out of range)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Selected?.Invoke(id));
        };
        _list.DoubleTapped += (_, _) =>
        {
            if ((_list.SelectedItem as ListBoxItem)?.Tag is not string id) return;
            // a component opens its part; a mate opens for editing
            if (id.StartsWith("mate:")) MateCommand?.Invoke(id["mate:".Length..], "edit");
            else Opened?.Invoke(id);
        };
    }

    /// <summary>Highlights a component's row (none: no row), the rows kept.</summary>
    public void Select(string? id)
    {
        var item = id == null ? null : _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == id);
        // a mate's row stays selected when no component is
        if (item == null && (_list.SelectedItem as ListBoxItem)?.Tag is string t && t.StartsWith("mate:")) return;
        if (_list.SelectedItem == item) return;
        _syncing = true;
        try { _list.SelectedItem = item; }
        finally { _syncing = false; }
    }

    /// <summary>Shows an assembly (its rows made again: assemblies have tens of components, not thousands).</summary>
    public void Show(AssemblySession s)
    {
        _syncing = true;
        try
        {
            _list.Items.Clear();
            var m = s.Model;
            _list.Items.Add(new ListBoxItem
            {
                Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { Icons.Make("assembly", 16), new TextBlock { Text = s.Title.TrimEnd(' ', '*'), FontWeight = FontWeight.SemiBold } } },
                Padding = new Thickness(6, 3),
            });
            foreach (var c in m.Doc.Components)
            {
                var pd = m.DisplayOf(c);
                var tags = new List<string>();
                if (c.Part.Embedded != null) tags.Add("embedded");
                if (pd?.Source == PartSource.Cache) tags.Add(pd.Problem != null ? "missing: the assembly's copy" : "lightweight");
                if (pd?.Source == PartSource.None) tags.Add("missing");
                if (c.Suppressed) tags.Add("suppressed");
                var name = new TextBlock { Text = $"{(c.Fixed ? "(f)" : "(-)")} {c.Name}" };
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 0, 0, 0), Children = { Icons.Make("part", 16), name } };
                if (tags.Count > 0) row.Children.Add(new TextBlock { Text = string.Join(", ", tags), Foreground = pd?.Problem != null ? Red : Dim, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                if (pd?.Problem != null) name.Foreground = Red;
                var item = new ListBoxItem { Content = row, Tag = c.Id, Padding = new Thickness(6, 2), ContextMenu = MenuFor(c) };
                var res = m.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(c.Part));
                ToolTip.SetTip(item, c.Part.Embedded != null ? $"Embedded in the assembly ({c.Part.Embedded})"
                    : res?.Path ?? $"Not found (last at {c.Part.AbsolutePath ?? c.Part.Path})");
                _list.Items.Add(item);
                if (c.Id == s.Selected) _list.SelectedItem = item;
            }
            // the mates
            if (m.Doc.Mates.Count > 0)
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { Icons.Make("relations", 16), new TextBlock { Text = "Mates" } } },
                    Padding = new Thickness(6, 4, 6, 2), Focusable = false, IsHitTestVisible = true,
                });
                string NameOf(MateRef r) => r.Component == null ? r.Face + " Plane" : m.Doc.Find(r.Component)?.Name ?? "?";
                foreach (var mate in m.Doc.Mates)
                {
                    var err = m.LastSolve?.Errors.GetValueOrDefault(mate.Id) ?? 0;
                    var bad = !mate.Suppressed && err > 1e-6;
                    var text = new TextBlock
                    {
                        Text = $"{mate.Name} ({NameOf(mate.A)}, {NameOf(mate.B)})" + ValueOf(mate),
                        Foreground = bad ? Red : mate.Suppressed ? Dim : null,
                    };
                    if (text.Foreground == null) text.ClearValue(TextBlock.ForegroundProperty);
                    var item = new ListBoxItem { Content = new Border { Child = text, Margin = new Thickness(14, 0, 0, 0) }, Tag = "mate:" + mate.Id, Padding = new Thickness(6, 2), ContextMenu = MenuFor(mate) };
                    ToolTip.SetTip(item, bad ? $"Not satisfied: off by {err:0.######} (it conflicts with other mates, or cannot be reached)"
                        : mate.Suppressed ? "Suppressed" : mate.Aligned ? "Aligned" : "Anti-aligned");
                    _list.Items.Add(item);
                }
            }
        }
        finally { _syncing = false; }
        _notes.Children.Clear();
        foreach (var n in s.Model.Notes.TakeLast(12))
            _notes.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
    }

    /// <summary>A mate's values as the tree shows them (distance, angle or their limits; ratio; lead).</summary>
    static string ValueOf(Mate m) => m.Type switch
    {
        MateType.Distance when m.Min is { } lo && m.Max is { } hi => $" {lo:0.###} to {hi:0.###} mm",
        MateType.Distance => $" {m.Value:0.###} mm",
        MateType.Angle when m.Min is { } lo && m.Max is { } hi => $" {lo:0.###} to {hi:0.###}\u00b0",
        MateType.Angle => $" {m.Value:0.###}\u00b0",
        MateType.Gear => $" {m.RatioA:0.###}:{m.RatioB:0.###}" + (m.Reverse ? " reversed" : ""),
        MateType.Screw => $" {m.Value:0.###} mm/turn" + (m.Reverse ? " left hand" : ""),
        _ => "",
    };

    ContextMenu MenuFor(Mate mate)
    {
        MenuItem I(string header, string cmd)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => MateCommand?.Invoke(mate.Id, cmd);
            return mi;
        }
        return new ContextMenu { Items = { I("_Edit Mate...", "edit"), new Separator(), I("_Flip Alignment", "flip"), I(mate.Suppressed ? "_Unsuppress" : "_Suppress", "suppress"), new Separator(), I("_Delete", "delete") } };
    }

    ContextMenu MenuFor(Component c)
    {
        MenuItem I(string header, string cmd, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => Command?.Invoke(c.Id, cmd);
            return mi;
        }
        return new ContextMenu
        {
            Items =
            {
                I("_Open Part", "open", c.Part.Embedded == null),
                new Separator(),
                I("_Fix", "fix", !c.Fixed), I("F_loat", "float", c.Fixed),
                I(c.Lightweight ? "Set to _Resolved" : "Set to _Lightweight", "lightweight"),
                new Separator(),
                I("_Delete", "delete"),
            },
        };
    }
}
