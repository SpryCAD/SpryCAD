using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace SpryCad.App.Sketcher;

/// <summary>
/// The sketch editor's dimension values and relation marks, drawn by one control. A large sketch has thousands of
/// them, and as a control each, every frame of the view under them redrew each one (a 20 × 20 grid of rectangles:
/// 12 frames a second instead of 60). Relation marks take clicks and show their names by position; dimension
/// values take none (the editor picks them by their boxes).
/// </summary>
sealed class SketchMarks : Control, ICustomHitTest
{
    /// <summary>A dimension value, a relation mark (clickable), or another mark (On Edge, anchor).</summary>
    public enum Kind { Dimension, Relation, Mark }

    /// <summary>A mark: its box on screen, its text and colors, and for a relation its id and name.</summary>
    public sealed record Mark(Kind Kind, long Id, Rect Box, FormattedText Text, IBrush? Back, IBrush? Border, string? Tip);

    readonly List<Mark> _marks = new();
    Mark? _under;

    /// <summary>A relation mark clicked: its constraint id, and whether Ctrl was held.</summary>
    public event Action<long, bool>? RelationClicked;

    public SketchMarks() { ClipToBounds = false; }

    public void Set(IEnumerable<Mark> marks)
    {
        _marks.Clear(); _marks.AddRange(marks);
        if (_under != null && !_marks.Any(m => m.Kind == Kind.Relation && m.Id == _under.Id)) SetUnder(null);
        InvalidateVisual();
    }

    public void Clear() { _marks.Clear(); SetUnder(null); InvalidateVisual(); }

    public FormattedText Text(string text, double size, FontWeight weight, IBrush fore) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight), size, fore);

    public override void Render(DrawingContext ctx)
    {
        foreach (var m in _marks)
        {
            var pen = m.Border != null ? new Pen(m.Border, 1) : null;
            if (m.Back != null || pen != null) ctx.DrawRectangle(m.Back, pen, m.Box.Deflate(pen != null ? 0.5 : 0), 2, 2);
            ctx.DrawText(m.Text, new Point(m.Box.X + (m.Box.Width - m.Text.Width) / 2, m.Box.Y + (m.Box.Height - m.Text.Height) / 2));
        }
    }

    Mark? RelationAt(Point p)
    {
        for (int i = _marks.Count - 1; i >= 0; i--)
            if (_marks[i].Kind == Kind.Relation && _marks[i].Box.Contains(p)) return _marks[i];
        return null;
    }

    // only relation marks take the pointer: anywhere else it goes to the view below
    public bool HitTest(Point p) => RelationAt(p) != null;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetUnder(RelationAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetUnder(null);
    }

    void SetUnder(Mark? m)
    {
        if (m?.Id == _under?.Id && m?.Kind == _under?.Kind) return;
        _under = m;
        ToolTip.SetTip(this, m?.Tip);
        ToolTip.SetIsOpen(this, false);
        Cursor = m != null ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (RelationAt(e.GetPosition(this)) is not { } m) return;
        RelationClicked?.Invoke(m.Id, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        e.Handled = true;
    }
}

/// <summary>Boxes placed so none is mostly covered by another (more than a third of the smaller one's area).</summary>
sealed class SpacedBoxes(double cell)
{
    readonly Dictionary<(int, int), List<Rect>> _cells = new();

    public bool TryAdd(Rect b)
    {
        int x0 = (int)Math.Floor(b.Left / cell), x1 = (int)Math.Floor(b.Right / cell);
        int y0 = (int)Math.Floor(b.Top / cell), y1 = (int)Math.Floor(b.Bottom / cell);
        for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int y = y0 - 1; y <= y1 + 1; y++)
                if (_cells.TryGetValue((x, y), out var list))
                    foreach (var o in list)
                    {
                        var i = b.Intersect(o);
                        if (i.Width > 0 && i.Height > 0 && i.Width * i.Height > Math.Min(b.Width * b.Height, o.Width * o.Height) / 3) return false;
                    }
        // (in the cell of its top left corner: a neighbour's search reaches it from one cell around)
        var key = (x0, y0);
        if (!_cells.TryGetValue(key, out var l)) _cells[key] = l = new List<Rect>(2);
        l.Add(b);
        return true;
    }
}
