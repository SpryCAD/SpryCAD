using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using FeatherCad.App.Viewport;

namespace FeatherCad.App.UI;

/// <summary>
/// SolidWorks' View Selector (Spacebar): a see-through cube over the model, turned with the view. Clicking one of
/// its 6 faces, 12 edges or 8 corners looks at the model from that direction. A strip below it holds the views the
/// cube does not offer. Empty space passes clicks through to the graphics area.
/// </summary>
public sealed class ViewSelector : Grid
{
    readonly CubeLayer _cube;

    public ViewSelector(ViewportControl vp, Action normalTo)
    {
        IsVisible = false;
        _cube = new CubeLayer(vp, Close);
        Children.Add(_cube);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4) };
        void B(string text, Action a)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 3) };
            b.Click += (_, _) => { a(); Close(); };
            strip.Children.Add(b);
        }
        foreach (var v in new[] { "Isometric", "Trimetric", "Dimetric" }) B(v, () => vp.StandardView(v));
        B("Normal To", normalTo);
        Children.Add(new Border { Classes = { "hud" }, Child = strip, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20) });
        vp.CameraChanged += () => { if (IsVisible) _cube.InvalidateVisual(); };
    }

    public bool IsOpen => IsVisible;

    public void Open() { IsVisible = true; _cube.InvalidateVisual(); }

    public void Close() { IsVisible = false; _cube.Hover = null; }

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    sealed class CubeLayer(ViewportControl vp, Action close) : Control, ICustomHitTest
    {
        /// <summary>Half size of a face's middle square (the cube spans -1..1); the strips around it are edges and corners.</summary>
        const float Inner = 0.6f;

        static readonly (Vector3 n, string name)[] Faces =
        {
            (Vector3.UnitZ, "Front"), (-Vector3.UnitZ, "Back"), (Vector3.UnitX, "Right"),
            (-Vector3.UnitX, "Left"), (Vector3.UnitY, "Top"), (-Vector3.UnitY, "Bottom"),
        };

        /// <summary>The view direction under the cursor (a face, edge or corner direction), highlighted.</summary>
        public Vector3? Hover;

        record struct Tile(Vector3 Dir, Point[] Quad, bool Middle, string? Name);

        /// <summary>The tiles of the faces turned toward the viewer, in screen coordinates.</summary>
        List<Tile> Tiles()
        {
            var cam = vp.Camera;
            var right = cam.Right; var up = cam.Up; var back = cam.Back;
            var s = (float)Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) * 0.17, 60, 150);
            var cx = Bounds.Width / 2; var cy = Bounds.Height / 2;
            Point P(Vector3 p) => new(cx + Vector3.Dot(p, right) * s, cy - Vector3.Dot(p, up) * s);
            var tiles = new List<Tile>();
            float[] cuts = { -1, -Inner, Inner, 1 };
            foreach (var (n, name) in Faces)
            {
                if (Vector3.Dot(n, back) < 0.02f) continue; // facing away (a convex cube: the rest never overlap)
                // the face's two in-plane axes
                var u = MathF.Abs(n.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX;
                var v = Vector3.Cross(n, u);
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        Vector3 C(float a, float b) => n + u * a + v * b;
                        var quad = new[] { P(C(cuts[i], cuts[j])), P(C(cuts[i + 1], cuts[j])), P(C(cuts[i + 1], cuts[j + 1])), P(C(cuts[i], cuts[j + 1])) };
                        var dir = n + u * (i - 1) + v * (j - 1);
                        tiles.Add(new Tile(dir, quad, i == 1 && j == 1, i == 1 && j == 1 ? name : null));
                    }
            }
            return tiles;
        }

        Tile? TileAt(Point p)
        {
            foreach (var t in Tiles()) if (Inside(t.Quad, p)) return t;
            return null;
        }

        static bool Inside(Point[] q, Point p)
        {
            bool inside = false;
            for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
                if ((q[i].Y > p.Y) != (q[j].Y > p.Y) && p.X < (q[j].X - q[i].X) * (p.Y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X)
                    inside = !inside;
            return inside;
        }

        // only the cube itself takes the pointer: the model stays usable around it
        public bool HitTest(Point point) => TileAt(point) != null;

        static readonly IBrush MiddleFill = new SolidColorBrush(Color.FromArgb(70, 120, 160, 230));
        static readonly IBrush BorderFill = new SolidColorBrush(Color.FromArgb(45, 120, 160, 230));
        static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(150, 255, 150, 40));
        static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(150, 40, 70, 140)), 1);

        public override void Render(DrawingContext ctx)
        {
            foreach (var t in Tiles())
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(t.Quad[0], true);
                    for (int k = 1; k < 4; k++) g.LineTo(t.Quad[k]);
                    g.EndFigure(true);
                }
                var fill = Hover is { } h && Vector3.DistanceSquared(h, t.Dir) < 1e-6f ? HoverFill : t.Middle ? MiddleFill : BorderFill;
                ctx.DrawGeometry(fill, Outline, geo);
                if (t.Name != null)
                {
                    var c = new Point(t.Quad.Average(q => q.X), t.Quad.Average(q => q.Y));
                    var ft = new FormattedText(t.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.MidnightBlue);
                    ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var dir = TileAt(e.GetPosition(this))?.Dir;
            if (dir != Hover) { Hover = dir; InvalidateVisual(); }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (Hover != null) { Hover = null; InvalidateVisual(); }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (TileAt(e.GetPosition(this)) is not { } t) return;
            vp.ViewFrom(t.Dir);
            close();
            e.Handled = true;
        }
    }
}
