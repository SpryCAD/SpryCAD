using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpryCad.App.UI;

/// <summary>
/// The pieces the part's and the assembly's trees build their rows from, so the two look and behave alike: the row's
/// panel (as wide as the tree, the text trimmed rather than scrolled sideways) and a folder's open / close arrow.
/// </summary>
public static class TreeRow
{
    /// <summary>
    /// A folder's arrow (▸ closed, ▾ open) in an 18x18 square that takes the click (not the row's), with a hover cue;
    /// rows without one take an empty 18 wide spacer instead (<see cref="Spacer"/>), so the icons line up.
    /// </summary>
    public static Control Expander(bool open, Action toggle)
    {
        var exp = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = open ? "▾" : "▸", FontSize = 14, Foreground = Palette.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(exp, open ? "Collapse" : "Expand");
        exp.PointerEntered += (_, _) => exp.Background = Palette.SelectedBg;
        exp.PointerExited += (_, _) => exp.Background = Brushes.Transparent;
        exp.PointerPressed += (_, e) => { e.Handled = true; toggle(); };
        return exp;
    }

    public static Control Spacer() => new Control { Width = 18 };
}

/// <summary>
/// A tree row's contents left to right, as wide as the tree: when they do not fit, the children named in
/// <see cref="Shrink"/> give up room, the first first (a grey note before the name), trimmed with an ellipsis;
/// the others (arrow, icon, markers after the name) keep their size. Rows never push the tree into scrolling sideways.
/// </summary>
public sealed class TreeRowPanel : Panel
{
    /// <summary>The children that may be narrowed, in the order they give up room (TextBlocks, set to trim).</summary>
    public List<Control> Shrink { get; } = new();

    public double Spacing { get; set; } = 4;

    readonly Dictionary<Control, double> _widths = new();

    protected override Size MeasureOverride(Size available)
    {
        double total = 0, height = 0;
        int shown = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            if (!c.IsVisible) continue;
            total += c.DesiredSize.Width; height = Math.Max(height, c.DesiredSize.Height); shown++;
        }
        total += Spacing * Math.Max(0, shown - 1);
        _widths.Clear();
        var excess = double.IsInfinity(available.Width) ? 0 : total - available.Width;
        foreach (var c in Shrink)
        {
            if (excess <= 0) break;
            if (!c.IsVisible || !Children.Contains(c)) continue;
            var cut = Math.Min(excess, c.DesiredSize.Width);
            _widths[c] = c.DesiredSize.Width - cut;
            excess -= cut;
            c.Measure(new Size(_widths[c], available.Height));
        }
        return new Size(double.IsInfinity(available.Width) ? total : Math.Min(total, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            var w = _widths.TryGetValue(c, out var nw) ? nw : c.DesiredSize.Width;
            c.Arrange(new Rect(x, 0, w, final.Height));
            x += w + Spacing;
        }
        return final;
    }
}
