using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using FeatherCad.App.Viewport;
using FeatherCad.Kernel;

namespace FeatherCad.App.UI;

/// <summary>
/// SolidWorks' View Selector (Spacebar): a see-through box around the model (its bounding box), drawn where the
/// model is and turned with the view, with the model faded behind it. Clicking one of its 6 faces, 12 edges or
/// 8 corners looks at the model from that direction, zoomed to fit. A strip below it holds the views the box does
/// not offer. Empty space passes clicks through to the graphics area.
/// </summary>
public sealed class ViewSelector : Grid
{
    readonly CubeLayer _cube;
    readonly ViewportControl _vp;

    public ViewSelector(ViewportControl vp, Action normalTo)
    {
        _vp = vp;
        IsVisible = false;
        _cube = new CubeLayer(vp, () => Close());
        Children.Add(_cube);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4) };
        void B(string text, Action a)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 3) };
            b.Click += (_, _) => { a(); Close(); };
            strip.Children.Add(b);
        }
        foreach (var v in new[] { "Isometric", "Trimetric", "Dimetric" }) B(v, () => vp.StandardView(v, fit: true));
        B("Normal To", normalTo);
        Children.Add(new Border { Classes = { "hud" }, Child = strip, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20) });
        vp.CameraChanged += () => { if (IsVisible) _cube.InvalidateVisual(); };
    }

    public bool IsOpen => IsVisible;

    // the view before opening, restored when the selector is dismissed without picking a view
    (Quaternion rotation, Vector3 target, float halfHeight)? _before;

    /// <summary>Shows the cube, zooming out so all of it fits in the graphics area (as SolidWorks).</summary>
    public void Open()
    {
        IsVisible = true;
        _vp.Ghosted = true;
        var cam = _vp.Camera;
        _before = (cam.Rotation, cam.Target, cam.HalfHeight);
        if (CubeLayer.ModelPlacement(_vp) is { } place)
        {
            var (c, h) = place;
            // the chamfered cube reaches 1.26 half-sizes from its center; a box around that sphere is fitted
            var r = Camera.V(new Vector3(h * 1.3f / MathF.Sqrt(3)));
            var box = Box3.Empty; box.Add(Camera.V(c) - r); box.Add(Camera.V(c) + r);
            _vp.ZoomToFit(true, box);
        }
        _cube.InvalidateVisual();
    }

    /// <summary>Puts the cube away; <paramref name="restore"/> returns to the view from before it opened.</summary>
    public void Close(bool restore = false)
    {
        if (!IsVisible) return;
        IsVisible = false; _vp.Ghosted = false; _cube.Hover = null;
        if (restore && _before is { } b) _vp.AnimateTo(b.rotation, b.target, b.halfHeight);
        _before = null;
    }

    /// <summary>Spacebar: open, or dismiss (back to the earlier view).</summary>
    public void Toggle() { if (IsOpen) Close(restore: true); else Open(); }

    sealed class CubeLayer(ViewportControl vp, Action close) : Control, ICustomHitTest
    {
        static readonly Dictionary<Vector3, string> FaceNames = new()
        {
            [Vector3.UnitZ] = "Front", [-Vector3.UnitZ] = "Back", [Vector3.UnitX] = "Right",
            [-Vector3.UnitX] = "Left", [Vector3.UnitY] = "Top", [-Vector3.UnitY] = "Bottom",
        };

        /// <summary>
        /// The chamfered cube (as SolidWorks'), on a cube spanning -1..1: 6 octagonal faces, 12 edge bevels and 8
        /// corner facets. Each is a facet of the convex solid bounded by the planes dir . p &lt;= d (dir = a face,
        /// edge or corner direction), found by clipping a large square on its plane by all the other planes. Its
        /// dir is also the view direction it stands for.
        /// </summary>
        static readonly (Vector3 dir, Vector3[] poly)[] Shape = BuildShape();

        static (Vector3, Vector3[])[] BuildShape()
        {
            const float Bevel = 0.3f;  // how far the edge bevels cut into the faces
            const float Corner = 1.0f; // the corner cut (its plane: |x|+|y|+|z| = 3 - Corner)
            var planes = new List<(Vector3 dir, float d)>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        int k = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
                        if (k > 0) planes.Add((new Vector3(x, y, z), k == 1 ? 1 : k == 2 ? 2 - Bevel : 3 - Corner));
                    }
            var shape = new List<(Vector3, Vector3[])>();
            foreach (var (dir, d) in planes)
            {
                var n = Vector3.Normalize(dir);
                var p0 = dir * (d / dir.LengthSquared());
                var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n));
                var v = Vector3.Cross(n, u);
                const float Big = 4;
                var poly = new List<Vector3> { p0 - u * Big - v * Big, p0 + u * Big - v * Big, p0 + u * Big + v * Big, p0 - u * Big + v * Big };
                foreach (var (od, odd) in planes)
                    if (od != dir) poly = Clip(poly, od, odd);
                if (poly.Count >= 3) shape.Add((dir, poly.ToArray()));
            }
            return shape.ToArray();
        }

        /// <summary>Sutherland-Hodgman: the part of a convex polygon where dir . p &lt;= d.</summary>
        static List<Vector3> Clip(List<Vector3> poly, Vector3 dir, float d)
        {
            var res = new List<Vector3>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                float da = Vector3.Dot(dir, a) - d, db = Vector3.Dot(dir, b) - d;
                if (da <= 1e-6f) res.Add(a);
                if ((da < -1e-6f && db > 1e-6f) || (da > 1e-6f && db < -1e-6f)) res.Add(a + (b - a) * (da / (da - db)));
            }
            return res;
        }

        /// <summary>The view direction under the cursor (a face, edge or corner direction), highlighted.</summary>
        public Vector3? Hover;

        record struct Tile(Vector3 Dir, Point[] Poly, bool Face, string? Name);

        /// <summary>
        /// Where the cube goes: centered on the model, big enough that the model sits well inside it whatever its
        /// proportions (as SolidWorks: always a cube, larger than the part). Without a model, at the view center.
        /// </summary>
        (Vector3 center, float half) Placement()
        {
            if (ModelPlacement(vp) is not { } p) return (vp.Camera.Target, vp.Camera.PixelSize * 110);
            return (p.center, MathF.Max(p.half, vp.Camera.PixelSize * 70)); // never smaller than this on screen
        }

        /// <summary>The cube around the model (null without one): its center and half size.</summary>
        public static (Vector3 center, float half)? ModelPlacement(ViewportControl vp)
        {
            var b = vp.SceneBounds();
            if (b.IsEmpty) return null;
            return (Camera.F(b.Center), (float)b.Diagonal * 0.5f * 1.25f);
        }

        /// <summary>The facets turned toward the viewer, in screen coordinates.</summary>
        List<Tile> Tiles()
        {
            var cam = vp.Camera;
            var (center, half) = Placement();
            Point P(Vector3 p) { var s = cam.Project(Camera.V(center + p * half)); return new Point(s.X, s.Y); }
            var tiles = new List<Tile>();
            foreach (var (dir, poly) in Shape)
            {
                // facing the viewer (a convex solid: the visible facets never overlap)
                var mid = center + poly.Aggregate(Vector3.Zero, (s, p) => s + p) / poly.Length * half;
                var toEye = cam.Perspective ? cam.Eye - mid : cam.Back;
                if (Vector3.Dot(dir, toEye) <= 1e-6f * toEye.Length()) continue;
                var face = FaceNames.TryGetValue(dir, out var name);
                tiles.Add(new Tile(dir, poly.Select(P).ToArray(), face, name));
            }
            return tiles;
        }

        Tile? TileAt(Point p)
        {
            foreach (var t in Tiles()) if (Inside(t.Poly, p)) return t;
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

        // only the box itself takes the pointer: the model stays usable around it
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
                    g.BeginFigure(t.Poly[0], true);
                    for (int k = 1; k < t.Poly.Length; k++) g.LineTo(t.Poly[k]);
                    g.EndFigure(true);
                }
                var fill = Hover is { } h && Vector3.DistanceSquared(h, t.Dir) < 1e-6f ? HoverFill : t.Face ? MiddleFill : BorderFill;
                ctx.DrawGeometry(fill, Outline, geo);
                if (t.Name != null)
                {
                    var c = new Point(t.Poly.Average(q => q.X), t.Poly.Average(q => q.Y));
                    var ft = new FormattedText(t.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.MidnightBlue);
                    // only where the face is big enough on screen to hold its name
                    var wPx = t.Poly.Max(q => q.X) - t.Poly.Min(q => q.X);
                    var hPx = t.Poly.Max(q => q.Y) - t.Poly.Min(q => q.Y);
                    if (wPx > ft.Width + 4 && hPx > ft.Height) ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
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
            vp.ViewFrom(t.Dir, fit: true);
            close();
            e.Handled = true;
        }
    }
}
