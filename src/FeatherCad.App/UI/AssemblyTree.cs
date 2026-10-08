using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FeatherCad.App.Session;
using FeatherCad.Core.Assembly;

namespace FeatherCad.App.UI;

/// <summary>
/// The FeatureManager tree of an assembly: the assembly at the top, then its components as SolidWorks shows them —
/// (f) fixed or (-) floating, the name, and what is special about its part (embedded, lightweight, missing: in red,
/// shown from the assembly's copy). Notes about the parts (found in new places, missing) underneath. Clicking selects;
/// double-clicking opens the part; right-click offers the component's commands.
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
            if (!_syncing) Selected?.Invoke((_list.SelectedItem as ListBoxItem)?.Tag as string);
        };
        _list.DoubleTapped += (_, _) => { if ((_list.SelectedItem as ListBoxItem)?.Tag is string id) Opened?.Invoke(id); };
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
        }
        finally { _syncing = false; }
        _notes.Children.Clear();
        foreach (var n in s.Model.Notes.TakeLast(12))
            _notes.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim });
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
