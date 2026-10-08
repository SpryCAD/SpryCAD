using Avalonia;
using Avalonia.Controls;

namespace SpryCad.App.UI;

/// <summary>
/// Lays tabs out left to right, as many as fit; the rest are hidden (the list of all parts reaches them). The
/// <see cref="Active"/> tab is always shown: when it would not fit, it takes the place of the last ones that do.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TabStripPanel, double>(nameof(Spacing), 2);
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    Control? _active;
    /// <summary>The tab that must be visible.</summary>
    public Control? Active { get => _active; set { _active = value; InvalidateArrange(); } }

    /// <summary>How many tabs are hidden (changes after a layout pass).</summary>
    public int HiddenCount { get; private set; }
    public event Action? HiddenCountChanged;

    protected override Size MeasureOverride(Size available)
    {
        double h = 0, w = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
            w += c.DesiredSize.Width + Spacing;
        }
        // as wide as the room there is (tabs past it are hidden, never scrolled)
        return new Size(double.IsInfinity(available.Width) ? w : Math.Min(w, available.Width), h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var kids = Children.ToList();
        var shown = new bool[kids.Count];
        double x = 0;
        int last = -1;
        for (int i = 0; i < kids.Count; i++)
        {
            var w = kids[i].DesiredSize.Width;
            if (x + w > final.Width + 0.5) break;
            shown[i] = true; last = i; x += w + Spacing;
        }
        // keep the active tab: drop shown tabs from the end until it fits after them
        var ai = _active == null ? -1 : kids.IndexOf(_active);
        if (ai > last)
        {
            var need = kids[ai].DesiredSize.Width;
            while (last >= 0 && x + need > final.Width + 0.5) { shown[last] = false; x -= kids[last].DesiredSize.Width + Spacing; last--; }
            shown[ai] = true;
        }
        x = 0;
        int hidden = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (!shown[i]) { c.Arrange(new Rect(0, 0, 0, 0)); c.Opacity = 0; c.IsHitTestVisible = false; hidden++; continue; }
            c.Opacity = 1; c.IsHitTestVisible = true;
            var w = Math.Min(c.DesiredSize.Width, Math.Max(0, final.Width - x));
            c.Arrange(new Rect(x, 0, w, final.Height));
            x += w + Spacing;
        }
        if (hidden != HiddenCount) { HiddenCount = hidden; HiddenCountChanged?.Invoke(); }
        return final;
    }
}
