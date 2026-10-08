using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpryCad.App.UI;

/// <summary>
/// SolidWorks' mouse gestures: hold the right button and drag, and a ring of commands appears around the press
/// point, one segment per direction (right, up, left, down). Sweeping toward one shades it (its name shows in the
/// middle); sweeping on through the ring runs it at once, releasing while it is shaded runs it too; releasing in
/// the middle runs nothing. Pointer input stays with the graphics area (this only draws).
/// </summary>
public sealed class GestureWheel : Canvas
{
    /// <summary>A command on the wheel, by direction: 0 right, 1 up, 2 left, 3 down.</summary>
    public sealed record Command(string Icon, string Label, Action Run);

    const double Outer = 92, Inner = 52, Dead = 18;
    static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(235, 240, 241, 243));
    static readonly IBrush ActiveFill = new SolidColorBrush(Color.FromArgb(240, 214, 217, 222));
    static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromRgb(160, 166, 175)), 1);

    Command?[] _commands = new Command?[4];
    Point _origin;
    int _active = -1;
    bool _done;
    readonly TextBlock _name = new() { FontSize = TypeScale.Small, Foreground = new SolidColorBrush(Color.FromRgb(60, 66, 76)), TextAlignment = TextAlignment.Center, Width = 2 * Inner - 8, TextWrapping = TextWrapping.Wrap };

    public GestureWheel()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        ClipToBounds = false;
        _ring = new Ring(this) { Width = 2 * Outer, Height = 2 * Outer };
    }

    public bool IsOpen => IsVisible;

    /// <summary>Shows the wheel at a point with these commands (right, up, left, down; null for none).</summary>
    public void Open(Point origin, Command?[] commands)
    {
        Children.Clear();
        _origin = origin; _commands = commands; _active = -1; _done = false;
        SetLeft(_ring, origin.X - Outer); SetTop(_ring, origin.Y - Outer);
        Children.Add(_ring);
        for (int i = 0; i < 4; i++)
        {
            if (commands[i] is not { } c) continue;
            var icon = Icons.Make(c.Icon, 22);
            var (dx, dy) = Direction(i);
            var mid = (Outer + Inner) / 2;
            SetLeft(icon, origin.X + dx * mid - 11); SetTop(icon, origin.Y + dy * mid - 11);
            Children.Add(icon);
        }
        _name.Text = "";
        Children.Add(_name);
        IsVisible = true;
        _ring.InvalidateVisual();
    }

    static (double dx, double dy) Direction(int i) => i switch { 0 => (1, 0), 1 => (0, -1), 2 => (-1, 0), _ => (0, 1) };

    /// <summary>The ring itself: four segments, each centered on its direction, the shaded one darker.</summary>
    sealed class Ring(GestureWheel wheel) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            var o = new Point(Outer, Outer); // the control spans the ring, its center in the middle
            for (int i = 0; i < 4; i++)
            {
                var center = -i * Math.PI / 2; // screen angles, y down: right 0, up -90°
                var geo = Segment(o, center - Math.PI / 4, center + Math.PI / 4);
                ctx.DrawGeometry(i == wheel._active && wheel._commands[i] != null ? ActiveFill : Fill, Outline, geo);
            }
        }
    }

    readonly Ring _ring;

    /// <summary>A ring segment around a center, between two screen angles (radians, y down).</summary>
    static StreamGeometry Segment(Point origin, double a0, double a1)
    {
        Point At(double r, double a) => new(origin.X + r * Math.Cos(a), origin.Y + r * Math.Sin(a));
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(At(Outer, a0), true);
        g.ArcTo(At(Outer, a1), new Size(Outer, Outer), 0, false, SweepDirection.Clockwise);
        g.LineTo(At(Inner, a1));
        g.ArcTo(At(Inner, a0), new Size(Inner, Inner), 0, false, SweepDirection.CounterClockwise);
        g.EndFigure(true);
        return geo;
    }

    /// <summary>The pointer moved: shades the command it points to; sweeping through the ring runs it.</summary>
    public void Move(Point p)
    {
        if (!IsOpen || _done) return;
        var v = p - _origin;
        var dist = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        int active = -1;
        if (dist > Dead)
        {
            var ang = Math.Atan2(-v.Y, v.X); // up is positive
            var i = ((int)Math.Round(ang / (Math.PI / 2)) % 4 + 4) % 4;
            if (_commands[i] != null) active = i;
        }
        SetActive(active);
        if (active >= 0 && dist >= Outer) Run(active);
    }

    /// <summary>The right button was released: runs the shaded command, if any, and closes.</summary>
    public void End(Point p)
    {
        if (!IsOpen) return;
        if (!_done) { Move(p); if (!_done && _active >= 0) Run(_active); }
        Close();
    }

    public void Close() { IsVisible = false; Children.Clear(); _active = -1; }

    void Run(int i)
    {
        _done = true;
        var c = _commands[i];
        Close();
        c?.Run();
    }

    void SetActive(int i)
    {
        if (i == _active) return;
        _active = i;
        // the shaded command's name, in the middle of the ring
        _name.Text = i >= 0 ? _commands[i]?.Label ?? "" : "";
        _name.Measure(Size.Infinity);
        SetLeft(_name, _origin.X - _name.Width / 2);
        SetTop(_name, _origin.Y - _name.DesiredSize.Height / 2);
        _ring.InvalidateVisual();
    }
}
