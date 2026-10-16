using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// The CommandManager's buttons, one row: each its icon above its name, and families of a command (the boss features,
/// the cuts, fillet and chamfer, the rectangle's kinds) on one button: a click runs the one used last (shown on it);
/// hovering it (or a right-click) drops a grid of the whole family below it, buttons the same size, and the one picked
/// there runs and becomes the button's. A row too long for the window shows its icons only (the names in the tooltips).
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
    // the commands each of the row's buttons stands for (a family's all): the overflow lists those that do not fit
    readonly Dictionary<Control, Command[]> _barCommands = new();

    string TipOf(Command c)
    {
        var key = c.Tool is { } st && SketchToolKeys.TryGetValue(st, out var k) ? $" ({k})" : "";
        return CommandHelp.TryGetValue(c.Name, out var help) ? $"{c.Name}{key}\n{help}" : c.Name + key;
    }

    /// <summary>The bar's layout (<see cref="UserSettings.CommandBar"/>): large buttons, else compact.</summary>
    static bool LargeBar => UserSettings.CommandBar == CommandBarLayout.Large;

    /// <summary>Lays the bar out anew as <paramref name="layout"/> (remembered).</summary>
    public void SetCommandBarLayout(CommandBarLayout layout)
    {
        if (UserSettings.CommandBar == layout) return;
        UserSettings.CommandBar = layout;
        UserSettings.Save();
        ShowTab(_activeTab);
    }

    /// <summary>
    /// A name as a large button shows it (the bar's when large, a family grid's always, <paramref name="large"/>): two
    /// words or more over two lines, broken where the lines come out most even ("Extruded Cut" as "Extruded / Cut", as
    /// "Linear / Pattern" is), one word on one. Every label is two lines high (Styles.axaml), so no tab's row is
    /// shorter than another's.
    /// </summary>
    static string BarLabel(string name, bool large = false)
    {
        if (!(large || LargeBar)) return name;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return name;
        var best = Enumerable.Range(1, words.Length - 1)
            .MinBy(k => Math.Max(string.Join(' ', words[..k]).Length, string.Join(' ', words[k..]).Length));
        return string.Join(' ', words[..best]) + "\n" + string.Join(' ', words[best..]);
    }

    /// <summary>A button's content: its icon and its name, below it (large) or beside it (compact).</summary>
    StackPanel RowContent(string icon, string text)
    {
        var label = new TextBlock { Text = BarLabel(text) };
        _cmdLabels.Add(label);
        return LargeBar
            ? new StackPanel { Spacing = 4, Children = { Icons.Make(icon, 32), label } }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icons.Make(icon, 22), label } };
    }

    /// <summary>
    /// A family on one button: the icon and name of the one used last, and a chevron: hovered, the family's grid
    /// drops below it, under <paramref name="label"/> (the family's name) when there is one.
    /// </summary>
    void Family(string? label, params Command[] family)
    {
        var main = new Button { Classes = { "cmd", LargeBar ? "big" : "compact" } };
        var key = string.Join("|", family.Select(f => f.Name));
        int Current() => Math.Clamp(_dropChoice.GetValueOrDefault(key), 0, family.Length - 1);
        var text = new TextBlock();
        _cmdLabels.Add(text);
        var iconHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center };
        main.Content = LargeBar
            ? new StackPanel { Spacing = 4, Children = { iconHost, text } }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { iconHost, text } };
        // the arrow at its right, centred in its height: clicked, it drops the family's grid (as hovering the button
        // does, for those who do not know to hover); the two outlined as one, a line between them, under the pointer
        var arrow = new Button { Classes = { "cmd", "drop" }, Content = Chevron(7) };
        var divider = new Border { Classes = { "splitdivider" } };
        var row = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        DockPanel.SetDock(divider, Dock.Right);
        row.Children.Add(arrow); row.Children.Add(divider); row.Children.Add(main);
        var box = new Border { Classes = { "splitbox" }, Child = row };
        void Show(int i)
        {
            // (no tooltip of its own: it would cover the grid, whose buttons have theirs)
            iconHost.Content = Icons.Make(family[i].Icon, LargeBar ? 32 : 22);
            text.Text = BarLabel(family[i].Name);
        }

        // the grid: the family's name over its commands, each its icon above its name, the one on the button marked
        var panel = new Border { Classes = { "cmdgrid" } };
        // (not light-dismissed: its layer over the window would take the pointer off the button, which closed the grid,
        // and the pointer back on the button opened it again, over and over; it closes as below instead)
        var popup = new Popup { PlacementTarget = box, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 2, Child = panel };
        void Pick(int i)
        {
            popup.IsOpen = false;
            _dropChoice[key] = i;
            Show(i);
            FitCommandRow();
            NoteCommand(family[i].Name, family[i].Icon, family[i].Run);
            family[i].Run();
            UpdateToolButtons();
        }
        // made anew each time it opens (no button left looking pressed or hovered from the time before)
        void Open()
        {
            if (popup.IsOpen) return;
            var grid = new UniformGrid { Columns = Math.Min(family.Length, 5) };
            for (int i = 0; i < family.Length; i++)
            {
                var idx = i;
                var c = family[i];
                var item = new Button { Classes = { "cmd", "big" }, Content = new StackPanel { Spacing = 4, Children = { Icons.Make(c.Icon, 32), new TextBlock { Text = BarLabel(c.Name, large: true) } } } };
                item.Classes.Set("active", i == Current());
                TipBeside(item, panel, TipOf(c));
                item.Click += (_, _) => Pick(idx);
                grid.Children.Add(item);
            }
            var content = new StackPanel { Spacing = 2 };
            if (label != null) content.Children.Add(new TextBlock { Text = label, Classes = { "cmdgridhead" } });
            content.Children.Add(grid);
            panel.Child = content;
            popup.IsOpen = true;
        }
        _dropGroups.Add(new DropGroup { Key = key, Commands = family, Show = Show });
        Show(Current());
        main.Click += (_, _) => { popup.IsOpen = false; var c = family[Current()]; NoteCommand(c.Name, c.Icon, c.Run); c.Run(); };
        arrow.Click += (_, _) => Open();

        // hovering the button a moment opens the grid; leaving both (a moment, to cross the gap) closes it
        var opening = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var closing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        opening.Tick += (_, _) => { opening.Stop(); if (box.IsPointerOver && main.IsEffectivelyEnabled) Open(); };
        closing.Tick += (_, _) => { closing.Stop(); if (!box.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; };
        box.PointerEntered += (_, _) => { closing.Stop(); if (!popup.IsOpen) opening.Start(); };
        box.PointerExited += (_, _) => { opening.Stop(); closing.Start(); };
        panel.PointerEntered += (_, _) => closing.Stop();
        panel.PointerExited += (_, _) => closing.Start();
        // (pressed: the click runs the button's command, the grid not wanted)
        main.AddHandler(PointerPressedEvent, (_, _) => opening.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        main.ContextRequested += (_, e) => { Open(); e.Handled = true; };
        // a click anywhere else in the window closes it (a click in the grid reaches the window too, through the
        // popup's owner: not that one, or the grid closed before its button got the click; nor the window losing
        // focus, which a click in the grid, a window of its own, can do)
        main.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(main) is not Window w || main.Tag != null) return;
            main.Tag = w;
            w.AddHandler(PointerPressedEvent, (_, _) => { if (popup.IsOpen && !box.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        };

        // the family's sketch tools light the button (in use, it shows that kind: UpdateDropGroups)
        foreach (var c in family) if (c.Tool is { } t) _toolButtons[t] = main;
        var host = new Panel { Children = { box, popup } };
        _barCommands[host] = family;
        CommandBar.Children.Add(host);
    }

    /// <summary>
    /// A drop-down's button's tooltip beside the drop-down (to its right, level with the button), never under the
    /// pointer: moving onto a tooltip took the pointer off the drop-down, which closed it.
    /// </summary>
    static void TipBeside(Control item, Control panel, string tip)
    {
        ToolTip.SetTip(item, tip);
        ToolTip.SetPlacement(item, PlacementMode.Right);
        item.PointerEntered += (_, _) =>
        {
            var right = item.TranslatePoint(new Point(item.Bounds.Width, 0), panel);
            ToolTip.SetHorizontalOffset(item, (right is { } r ? panel.Bounds.Width - r.X : 0) + 6);
        };
    }

    /// <summary>A drop-down chevron in the text's colour, <paramref name="w"/> wide.</summary>
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

    /// <summary>
    /// A row too wide for the window: compact buttons show their icons only (the names in the tooltips); what still
    /// does not fit (large buttons keep their names) goes behind the overflow button at the row's end.
    /// </summary>
    bool _fitHooked;

    void FitCommandRow()
    {
        // (again when the window is made wider or narrower: only the width decides. Its height follows the names shown
        // or not, so refitting on any size change went round and round, the window hung in layout)
        if (!_fitHooked && CommandBar.Parent is Control h) { h.SizeChanged += (_, e) => { if (e.WidthChanged) FitCommandRow(); }; _fitHooked = true; }
        // (one fit at a time: asked again while one waits, that one does)
        if (_fitPending) return;
        _fitPending = true;
        CommandBar.Height = LargeBar ? double.NaN : 34;
        foreach (var l in _cmdLabels) l.IsVisible = true;
        foreach (var b in CommandBar.GetLogicalDescendants().OfType<Button>()) b.Classes.Set("iconsonly", false);
        // the overflow button last on the row (the row is made anew for each tab)
        var more = OverflowHost();
        CommandBar.Children.Remove(more);
        CommandBar.Children.Add(more);
        var items = CommandBar.Children.Where(c => c != more).ToList();
        foreach (var c in items) c.IsVisible = true;
        more.IsVisible = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _fitPending = false;
            if (CommandBar.Parent is not Control host) return;
            var room = host.Bounds.Width - 8;
            double Wide() { CommandBar.Measure(Size.Infinity); return items.Sum(c => c.DesiredSize.Width); }
            if (Wide() > room && !LargeBar)
            {
                foreach (var l in _cmdLabels) l.IsVisible = false;
                foreach (var b in CommandBar.GetLogicalDescendants().OfType<Button>()) b.Classes.Set("iconsonly", true);
            }
            var hidden = new List<Control>();
            if (Wide() > room)
            {
                // from the end, until the rest and the overflow button fit (a separator never left last)
                double x = 0, limit = room - 36;
                foreach (var c in items) { x += c.DesiredSize.Width; if (x > limit) hidden.Add(c); }
                while (items.LastOrDefault(c => !hidden.Contains(c)) is { } last && last.Classes.Contains("barsep")) hidden.Insert(0, last);
                foreach (var c in hidden) c.IsVisible = false;
            }
            _overflowItems = hidden;
            more.IsVisible = hidden.Count > 0;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // the overflow: its button (with its list's popup), and the row's items behind it
    Panel? _overflowHost;
    List<Control> _overflowItems = new();

    /// <summary>
    /// The overflow button at the row's end: hovered (or clicked), a list of the commands that do not fit drops below
    /// it, each its icon and name; one clicked runs (a family's also goes on its button).
    /// </summary>
    Panel OverflowHost()
    {
        if (_overflowHost != null) return _overflowHost;
        var button = new Button { Classes = { "cmd", "overflow" }, Content = new TextBlock { Text = "»", FontSize = 18 } };
        ToolTip.SetTip(button, "More commands");
        var list = new StackPanel { Spacing = 0 };
        var panel = new Border { Classes = { "cmdgrid" }, Child = list };
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 2, Child = panel };
        void Run(Command c)
        {
            popup.IsOpen = false;
            // a family's: it goes on its button too, as picked from its grid
            foreach (var g in _dropGroups)
                if (Array.IndexOf(g.Commands, c) is var i and >= 0) { _dropChoice[g.Key] = i; g.Show(i); }
            NoteCommand(c.Name, c.Icon, c.Run);
            c.Run();
            UpdateToolButtons();
        }
        void Open()
        {
            if (popup.IsOpen) return;
            list.Children.Clear();
            foreach (var item in _overflowItems)
            {
                if (item.Classes.Contains("barsep")) { if (list.Children.Count > 0) list.Children.Add(new Border { Classes = { "menusep" } }); continue; }
                if (!_barCommands.TryGetValue(item, out var cmds)) continue;
                foreach (var c in cmds)
                {
                    var row = new Button
                    {
                        Classes = { "cmd", "menurow" },
                        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Make(c.Icon, 20), new TextBlock { Text = c.Name.Replace('\n', ' ') } } },
                    };
                    TipBeside(row, panel, TipOf(c));
                    row.Click += (_, _) => Run(c);
                    list.Children.Add(row);
                }
            }
            popup.IsOpen = list.Children.Count > 0;
        }
        button.Click += (_, _) => Open();
        // as a family's grid: hovering a moment opens it, leaving both closes it, a click elsewhere too
        var opening = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var closing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        opening.Tick += (_, _) => { opening.Stop(); if (button.IsPointerOver) Open(); };
        closing.Tick += (_, _) => { closing.Stop(); if (!button.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; };
        button.PointerEntered += (_, _) => { closing.Stop(); if (!popup.IsOpen) opening.Start(); };
        button.PointerExited += (_, _) => { opening.Stop(); closing.Start(); };
        panel.PointerEntered += (_, _) => closing.Stop();
        panel.PointerExited += (_, _) => closing.Start();
        button.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is not Window w || button.Tag != null) return;
            button.Tag = w;
            w.AddHandler(PointerPressedEvent, (_, _) => { if (popup.IsOpen && !button.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        };
        return _overflowHost = new Panel { Children = { button, popup } };
    }

    bool _fitPending;
}
