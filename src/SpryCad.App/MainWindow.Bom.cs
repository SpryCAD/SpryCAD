using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Assembly;

namespace SpryCad.App;

/// <summary>
/// The Bill of Materials pane of the task pane: the assembly's parts, every level counted together (suppressed ones
/// left out), each with a preview in the color the graphics area shows it in (with Color by Part on, the same color as
/// there) and how many there are. A row clicked selects the part's first instance.
/// </summary>
public partial class MainWindow
{
    TaskPane _bomPane = null!;
    DockPanel? _bomView;
    StackPanel _bomRows = null!;
    TextBlock _bomNote = null!;
    object? _bomKey;
    // previews made, by part display and color (kept as long as the display is)
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<PartDisplay, Dictionary<string, Bitmap>> _bomThumbs = new();

    bool BomShown => _taskMode != TaskMode.Hidden && _taskCurrent == _bomPane;

    Control BomControl()
    {
        if (_bomView == null)
        {
            _bomRows = new StackPanel();
            _bomNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted, FontSize = TypeScale.Small, Margin = new Thickness(10, 8) };
            DockPanel.SetDock(_bomNote, Dock.Top);
            _bomView = new DockPanel { Children = { _bomNote, new ScrollViewer { Content = _bomRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } };
        }
        _bomKey = null; // (made again: the assembly may have changed while it was hidden)
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshBom);
        return _bomView;
    }

    /// <summary>
    /// Color by Part's colors: each part, in each configuration, its own, in the order the parts first appear (hidden
    /// ones too), so hiding or adding a component leaves the others' colors as they were. Indices into
    /// <see cref="FeatureColors"/>, by <see cref="PartKey"/>.
    /// </summary>
    static Dictionary<string, int> PartColorIndex(AssemblySession a)
    {
        var index = new Dictionary<string, int>();
        foreach (var (o, _) in a.Model.PlacedParts(hidden: true))
            index.TryAdd(PartKey(o.Component), index.Count);
        return index;
    }

    /// <summary>Fills the pane again when what it shows has changed (another document, the assembly, its parts' displays, Color by Part).</summary>
    void RefreshBom()
    {
        if (_bomView == null || !BomShown) return;
        var key = (Asm != null ? (object)Asm : Session, Asm?.Revision, _colorByFeature);
        if (Equals(key, _bomKey)) return;
        _bomKey = key;
        _bomRows.Children.Clear();
        if (Asm is not { } a) { _bomNote.Text = "Open an assembly to see its bill of materials."; return; }
        var colors = _colorByFeature ? PartColorIndex(a) : null;
        // each part in each configuration once (as the Bill of Materials counts them), in the order first met
        var groups = a.Model.Occurrences().Where(o => !o.Component.IsAssembly).GroupBy(o => AssemblyModel.DisplayKey(o.Component)).ToList();
        var total = groups.Sum(g => g.Count());
        _bomNote.Text = groups.Count == 0 ? "The assembly has no parts." : $"{groups.Count} part{(groups.Count == 1 ? "" : "s")}, {total} in all.";
        foreach (var g in groups) _bomRows.Children.Add(BomRowControl(g.First(), g.Count(), colors));
    }

    Control BomRowControl(Occurrence o, int count, Dictionary<string, int>? colors)
    {
        var c = o.Component;
        var r = c.Part;
        var name = Path.GetFileNameWithoutExtension(r.Path ?? r.AbsolutePath ?? r.Embedded ?? "");
        if (string.IsNullOrEmpty(name) || r.Embedded != null) name = c.Name.Split('<')[0];
        var config = c.Parameters != null ? o.Owner.ConfigLabel(c) : c.Configuration ?? "";
        var pd = o.Display;
        var color = colors != null && colors.TryGetValue(PartKey(c), out var ci) ? Core.Model.FeatureColors.Color(ci) : o.Color ?? pd?.Color ?? new[] { 0.7, 0.7, 0.7 };

        var img = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform };
        var thumb = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(3), Background = Palette.Surface, Child = img,
            BorderBrush = Palette.Border, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
        };
        if (pd != null && pd.Mesh.Indices.Length > 0) LoadBomThumb(img, pd, color);
        else img.Source = null; // (still being built: shown when it is)

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        text.Children.Add(new TextBlock { Text = name, FontSize = TypeScale.Body, TextTrimming = TextTrimming.CharacterEllipsis });
        if (config.Length > 0) text.Children.Add(new TextBlock { Text = config, FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis });
        var qty = new TextBlock { Text = "×" + count, FontSize = TypeScale.Body, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(text, 1); Grid.SetColumn(qty, 2);
        grid.Children.Add(thumb); grid.Children.Add(text); grid.Children.Add(qty);
        var row = new Border { Classes = { "treerow" }, Child = grid, Padding = new Thickness(6, 3), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(row, $"{name}{(config.Length > 0 ? $" ({config})" : "")}: {count} in the assembly" + (r.Embedded != null ? ", embedded" : r.AbsolutePath is { } f ? "\n" + f : "") + "\nClick to select the first one.");
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            SelectComponent(o.Path, exact: true);
            e.Handled = true;
        };
        return row;
    }

    /// <summary>A part's preview in a color (made in the background, kept for the next time).</summary>
    async void LoadBomThumb(Image img, PartDisplay pd, double[] color)
    {
        var ck = string.Join(",", color.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        var made = _bomThumbs.GetOrCreateValue(pd);
        if (made.TryGetValue(ck, out var had)) { img.Source = had; return; }
        try
        {
            var png = await Task.Run(() => Kernel.Thumbnail.Render(new[] { (pd.Mesh, color) }, 96));
            var bmp = new Bitmap(new MemoryStream(png));
            made[ck] = bmp;
            img.Source = bmp;
        }
        catch (Exception ex) { App.Log(ex, "Bill of Materials preview"); } // (a preview is never worth more)
    }
}
