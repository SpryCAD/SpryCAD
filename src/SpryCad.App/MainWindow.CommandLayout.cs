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

    /// <summary>A button's content: its icon and its name, below it (large) or beside it (compact).</summary>
    StackPanel RowContent(string icon, string text)
    {
        var label = new TextBlock { Text = text };
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
        var iconHost = new ContentControl();
        // (the chevron beside the icon: a two-line name keeps the button the others' height)
        var chevron = Chevron(7);
        chevron.Margin = new Thickness(0, 0, -11, 0);
        if (LargeBar)
        {
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { iconHost, chevron } };
            main.Content = new StackPanel { Spacing = 4, Children = { top, text } };
        }
        else
        {
            // compact: the icon, the name and the chevron in a line
            chevron.Margin = new Thickness(1, 0, 0, 0);
            main.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { iconHost, text, chevron } };
        }
        void Show(int i)
        {
            // (no tooltip of its own: it would cover the grid, whose buttons have theirs)
            iconHost.Content = Icons.Make(family[i].Icon, LargeBar ? 32 : 22);
            text.Text = family[i].Name;
        }

        // the grid: the family's name over its commands, each its icon above its name, the one on the button marked
        var panel = new Border { Classes = { "cmdgrid" } };
        // (not light-dismissed: its layer over the window would take the pointer off the button, which closed the grid,
        // and the pointer back on the button opened it again, over and over; it closes as below instead)
        var popup = new Popup { PlacementTarget = main, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 2, Child = panel };
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
                var item = new Button { Classes = { "cmd", "big" }, Content = new StackPanel { Spacing = 4, Children = { Icons.Make(c.Icon, 32), new TextBlock { Text = c.Name } } } };
                item.Classes.Set("active", i == Current());
                ToolTip.SetTip(item, TipOf(c));
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

        // hovering the button a moment opens the grid; leaving both (a moment, to cross the gap) closes it
        var opening = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var closing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        opening.Tick += (_, _) => { opening.Stop(); if (main.IsPointerOver && main.IsEffectivelyEnabled) Open(); };
        closing.Tick += (_, _) => { closing.Stop(); if (!main.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; };
        main.PointerEntered += (_, _) => { closing.Stop(); if (!popup.IsOpen) opening.Start(); };
        main.PointerExited += (_, _) => { opening.Stop(); closing.Start(); };
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
            w.AddHandler(PointerPressedEvent, (_, _) => { if (popup.IsOpen && !main.IsPointerOver && !panel.IsPointerOver) popup.IsOpen = false; },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        };

        // the family's sketch tools light the button (in use, it shows that kind: UpdateDropGroups)
        foreach (var c in family) if (c.Tool is { } t) _toolButtons[t] = main;
        CommandBar.Children.Add(new Panel { Children = { main, popup } });
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

    /// <summary>The row's names shown when the row fits the window, else its icons only (the names in the tooltips).</summary>
    bool _fitHooked;

    void FitCommandRow()
    {
        // (again when the window is resized)
        if (!_fitHooked && CommandBar.Parent is Control h) { h.SizeChanged += (_, _) => FitCommandRow(); _fitHooked = true; }
        CommandBar.Height = LargeBar ? double.NaN : 34;
        foreach (var l in _cmdLabels) l.IsVisible = true;
        foreach (var b in CommandBar.GetLogicalDescendants().OfType<Button>()) b.Classes.Set("iconsonly", false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CommandBar.Parent is not Control host) return;
            CommandBar.Measure(Size.Infinity);
            var fits = CommandBar.DesiredSize.Width <= host.Bounds.Width - 8;
            foreach (var l in _cmdLabels) l.IsVisible = fits;
            foreach (var b in CommandBar.GetLogicalDescendants().OfType<Button>()) b.Classes.Set("iconsonly", !fits);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
