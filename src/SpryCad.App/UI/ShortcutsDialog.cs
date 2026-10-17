using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Sketcher;

namespace SpryCad.App.UI;

/// <summary>
/// Help > Keyboard Shortcuts: the mouse and keyboard reference, by section, each shortcut's keys drawn as key caps
/// beside what it does, and a search box that narrows the list as you type (by key or by action). The sketch tools'
/// letters are read from the app's own table (<see cref="MainWindow.SketchToolKeys"/>), so they stay right.
/// </summary>
public static class ShortcutsDialog
{
    /// <summary>A shortcut: its keys ("Ctrl+Shift+S", "Middle drag"; "+" joins keys pressed together) and what it does.</summary>
    sealed record Shortcut(string Keys, string Does);

    static readonly Dictionary<SketchTool, string> ToolNames = new()
    {
        [SketchTool.Line] = "Line", [SketchTool.Rectangle] = "Corner Rectangle", [SketchTool.Circle] = "Circle",
        [SketchTool.CenterArc] = "Centerpoint Arc", [SketchTool.Point] = "Point", [SketchTool.SmartDimension] = "Smart Dimension",
        [SketchTool.Trim] = "Trim Entities", [SketchTool.Spline] = "Spline",
    };

    static (string title, Shortcut[] items)[] Sections() => new[]
    {
        ("View with the Mouse", new Shortcut[]
        {
            new("Middle drag", "Rotate"),
            new("Middle click", "Set the rotation center at this point; in empty space, reset it"),
            new("Ctrl+Middle drag", "Pan"),
            new("Shift+Middle drag", "Zoom"),
            new("Alt+Middle drag", "Roll"),
            new("Wheel", "Zoom at the cursor"),
            new("Right drag", "Mouse gestures. Part: right Measure, up Normal To, left Zoom to Fit, down Sketch. Sketch: right Smart Dimension, up Circle, left Line, down Rectangle. Assembly: right Mate, down Insert Components."),
        }),
        ("View with the Keyboard", new Shortcut[]
        {
            new("F", "Zoom to Fit"),
            new("Space", "Open the View Selector"),
            new("Ctrl+1 … 6", "Front, Back, Left, Right, Top, Bottom"),
            new("Ctrl+7", "Isometric"),
            new("N or Ctrl+8", "Normal To (again: from the other side). With a second planar face or plane selected, that one at the top; with nothing selected, the nearest standard plane"),
            new("Arrows", "Rotate 15° (Shift: 90°)"),
            new("Ctrl+Arrows", "Pan"),
        }),
        ("Sketch", MainWindow.SketchToolKeys.OrderBy(k => k.Value)
            .Select(k => new Shortcut(k.Value, ToolNames.GetValueOrDefault(k.Key, k.Key.ToString())))
            .Concat(new Shortcut[]
            {
                new("Esc", "End the active tool, or return to Select"),
                new("Double-click", "Edit a dimension's value"),
                new("Delete", "Delete the selected entities or relations"),
            }).ToArray()),
        ("Files and Tabs", new Shortcut[]
        {
            new("Ctrl+N", "New part"),
            new("Ctrl+O", "Open"),
            new("Ctrl+S", "Save"),
            new("Ctrl+Shift+S", "Save As"),
            new("Ctrl+I", "Import STEP"),
            new("Ctrl+W", "Close the active document"),
            new("Ctrl+Tab", "Next tab (Shift: previous tab)"),
        }),
        ("Editing", new Shortcut[]
        {
            new("Ctrl+Z", "Undo"),
            new("Ctrl+Y", "Redo"),
            new("Delete", "Delete the selection"),
            new("F2", "Rename the selected feature or mate"),
            new("Ctrl+B", "Rebuild"),
            new("Ctrl+Q", "Force rebuild of all features"),
        }),
        ("PropertyManager", new Shortcut[]
        {
            new("Enter", "OK"),
            new("Esc", "Cancel"),
        }),
    };

    public static async Task Show(Window owner)
    {
        var sections = Sections();
        var search = new TextBox { Watermark = "Search by key or action", Margin = new Thickness(14, 12, 14, 8) };
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 14, 12) };
        var none = new TextBlock { Text = "No shortcut matches.", Foreground = Palette.TextMuted, Margin = new Thickness(0, 8, 0, 0) };

        void Fill()
        {
            var q = (search.Text ?? "").Trim();
            list.Children.Clear();
            foreach (var (title, items) in sections)
            {
                var shown = q.Length == 0 ? items
                    : items.Where(s => s.Keys.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Does.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (shown.Length == 0) continue;
                list.Children.Add(new TextBlock
                {
                    Text = title, FontSize = TypeScale.Header, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 4),
                });
                foreach (var s in shown) list.Children.Add(Row(s));
            }
            if (list.Children.Count == 0) list.Children.Add(none);
        }
        search.TextChanged += (_, _) => Fill();
        Fill();

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var bottom = Dialogs.FooterBar(null, close);
        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(search); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var w = new Window
        {
            Title = "Keyboard Shortcuts", Width = 640, Height = 600, MinWidth = 480, MinHeight = 360,
            CanResize = true, CanMinimize = false, CanMaximize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root,
        };
        Dialogs.Compact(w);
        w.Opened += (_, _) => search.Focus();
        close.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>A shortcut's row: its keys as caps in a column of their own, then what it does.</summary>
    static Control Row(Shortcut s)
    {
        var keys = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var parts = s.Keys.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) keys.Children.Add(new TextBlock { Text = "+", Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) });
            keys.Children.Add(Cap(parts[i].Trim()));
        }
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 1) };
        var does = new TextBlock { Text = s.Does, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(does, 1);
        g.Children.Add(keys); g.Children.Add(does);
        return g;
    }

    /// <summary>A key cap: the key's name on a light key with a slightly heavier bottom edge.</summary>
    static Control Cap(string key) => new Border
    {
        Child = new TextBlock { Text = key, FontSize = TypeScale.Small, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text },
        Background = Palette.SurfaceAlt, BorderBrush = Palette.BorderStrong, BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Margin = new Thickness(0, 1), VerticalAlignment = VerticalAlignment.Center,
    };
}
