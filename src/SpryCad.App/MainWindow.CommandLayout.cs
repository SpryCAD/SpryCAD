using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// The CommandManager's buttons, one compact row: each an icon and its name beside it, and families of a command (the
/// boss features, the cuts, fillet and chamfer, the rectangle's kinds) on one split button: the button runs the one
/// used last (shown on it), the arrow lists them all, and the one picked becomes the button's. A row too long for the
/// window shows its icons only (the names in the tooltips).
/// </summary>
public partial class MainWindow
{
    /// <summary>A command: its icon, its name, what it does, and the sketch tool it starts.</summary>
    sealed record Command(string Icon, string Name, Action Run, SketchTool? Tool = null);

    /// <summary>A family's split button: its commands and how the button shows one of them.</summary>
    sealed class DropGroup
    {
        public required string Key { get; init; }
        public required Command[] Commands { get; init; }
        public required Action<int> Show { get; init; }
    }

    // the one last picked in each family (shown and run by its button), kept while the app runs
    readonly Dictionary<string, int> _dropChoice = new();
    readonly List<DropGroup> _dropGroups = new();
    // the names on the row's buttons (hidden when the row does not fit)
    readonly List<TextBlock> _cmdLabels = new();

    string TipOf(Command c)
    {
        var key = c.Tool is { } st && SketchToolKeys.TryGetValue(st, out var k) ? $" ({k})" : "";
        return CommandHelp.TryGetValue(c.Name, out var help) ? $"{c.Name}{key}\n{help}" : c.Name + key;
    }

    /// <summary>A button's content: its icon and, beside it, its name.</summary>
    StackPanel RowContent(string icon, string text)
    {
        var label = new TextBlock { Text = text };
        _cmdLabels.Add(label);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icons.Make(icon, 22), label } };
    }

    /// <summary>
    /// A family on one split button: <paramref name="label"/> (else the name of the one shown) beside the icon of the
    /// one used last; its arrow lists the family.
    /// </summary>
    void Family(string? label, params Command[] family)
    {
        var main = new Button { Classes = { "cmd", "split" } };
        var arrow = new Button { Classes = { "cmd", "drop" }, Content = Chevron(8) };
        var key = string.Join("|", family.Select(f => f.Name));
        int Current() => Math.Clamp(_dropChoice.GetValueOrDefault(key), 0, family.Length - 1);
        // the button: the icon of the one shown, and the family's name (or the one's)
        var text = new TextBlock();
        _cmdLabels.Add(text);
        var iconHost = new ContentControl();
        main.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { iconHost, text } };
        void Show(int i)
        {
            var c = family[i];
            iconHost.Content = Icons.Make(c.Icon, 22);
            text.Text = label ?? c.Name;
            ToolTip.SetTip(main, TipOf(c));
        }
        _dropGroups.Add(new DropGroup { Key = key, Commands = family, Show = Show });
        Show(Current());
        main.Click += (_, _) => { var c = family[Current()]; NoteCommand(c.Name, c.Icon, c.Run); c.Run(); };
        ToolTip.SetTip(arrow, (label ?? "More") + ": " + string.Join(", ", family.Select(f => f.Name)));
        arrow.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            for (int i = 0; i < family.Length; i++)
            {
                var idx = i;
                var mi = new MenuItem { Header = family[i].Name, Icon = Icons.Make(family[i].Icon, 18) };
                mi.Click += (_, _) => { _dropChoice[key] = idx; Show(idx); FitCommandRow(); NoteCommand(family[idx].Name, family[idx].Icon, family[idx].Run); family[idx].Run(); UpdateToolButtons(); };
                menu.Items.Add(mi);
            }
            menu.ShowAt(main);
        };
        // the family's sketch tools light the button (in use, it shows that kind: UpdateDropGroups)
        foreach (var c in family) if (c.Tool is { } t) _toolButtons[t] = main;
        var divider = new Border { Width = 1, Classes = { "splitdivider" }, Margin = new Thickness(0, 5) };
        var parts = new DockPanel { Children = { arrow, divider, main } };
        DockPanel.SetDock(arrow, Dock.Right); DockPanel.SetDock(divider, Dock.Right);
        CommandBar.Children.Add(new Border { Classes = { "splitbox" }, Child = parts });
    }

    /// <summary>A drop-down arrow: a chevron in the text's colour, <paramref name="w"/> wide.</summary>
    static Control Chevron(double w) => new Avalonia.Controls.Shapes.Path
    {
        Data = Avalonia.Media.Geometry.Parse("M0 0L4 4 8 0"), Stroke = Palette.Text, StrokeThickness = 1.5,
        StrokeLineCap = Avalonia.Media.PenLineCap.Round, StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        Stretch = Avalonia.Media.Stretch.Uniform, Width = w, Height = w / 2,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A family whose sketch tool is in use (picked from the keyboard, say) shows that kind on its button.</summary>
    void UpdateDropGroups()
    {
        if (_sketch == null) return;
        foreach (var g in _dropGroups)
        {
            var i = Array.FindIndex(g.Commands, c => c.Tool == _sketch.Tool);
            if (i < 0 || i == _dropChoice.GetValueOrDefault(g.Key)) continue;
            _dropChoice[g.Key] = i;
            g.Show(i);
            FitCommandRow();
        }
    }

    /// <summary>The row's names shown when the row fits the window, else its icons only (the names in the tooltips).</summary>
    bool _fitHooked;

    void FitCommandRow()
    {
        // (again when the window is resized)
        if (!_fitHooked && CommandBar.Parent is Control h) { h.SizeChanged += (_, _) => FitCommandRow(); _fitHooked = true; }
        foreach (var l in _cmdLabels) l.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CommandBar.Parent is not Control host) return;
            CommandBar.Measure(Size.Infinity);
            var fits = CommandBar.DesiredSize.Width <= host.Bounds.Width - 8;
            foreach (var l in _cmdLabels) l.IsVisible = fits;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
