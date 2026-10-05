using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using FeatherCad.App.Session;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;
using Silk.NET.OpenGL;

namespace FeatherCad.App.Viewport;

public enum DisplayStyle { ShadedWithEdges, Shaded, HiddenLinesVisible, Wireframe }

public readonly record struct PointerInfo(double X, double Y, KeyModifiers Mods, int ClickCount)
{
    public bool Ctrl => (Mods & KeyModifiers.Control) != 0;
    public bool Shift => (Mods & KeyModifiers.Shift) != 0;
}

/// <summary>Interaction mode (model selection, sketching...).</summary>
public interface IViewportMode
{
    void PointerMove(ViewportControl vp, PointerInfo p);
    void PointerDown(ViewportControl vp, PointerInfo p);
    void PointerUp(ViewportControl vp, PointerInfo p);
    bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods);
    void Draw(ViewportControl vp, DynamicGeometry g);
    void UpdateOverlay(ViewportControl vp);
    Cursor? Cursor { get; }
}

/// <summary>A reference plane shown in the viewport.</summary>
public sealed record PlaneDisplay(string Name, string? FeatureId, Frame Frame, double HalfSize);

/// <summary>A sketch shown (not being edited) in the viewport.</summary>
public sealed record SketchDisplay(string FeatureId, SketchResult Result);

public sealed class ViewportControl : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>The GL surface draws via composition, so tell Avalonia the whole area is hit-testable.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public readonly Camera Camera = new();
    public IViewportMode? Mode { get; set; }
    public Canvas? Overlay { get; set; }

    // scene
    public IReadOnlyList<Body> Bodies { get; set; } = Array.Empty<Body>();
    /// <summary>Feature preview drawn as a translucent overlay (fillet, chamfer, shell...).</summary>
    public IReadOnlyList<Body> PreviewBodies { get; set; } = Array.Empty<Body>();
    public IReadOnlyList<PlaneDisplay> Planes { get; set; } = Array.Empty<PlaneDisplay>();
    public IReadOnlyList<SketchDisplay> Sketches { get; set; } = Array.Empty<SketchDisplay>();
    public Rgba PartColor = new(0.74f, 0.76f, 0.80f);
    public DisplayStyle Style = DisplayStyle.ShadedWithEdges;
    public bool ShowPlanes = true;
    public bool ShowOrigin = true;
    public bool ReverseWheel;

    // highlight state
    public SelItem? Hover { get; set; }
    public SelectionSet? Selection { get; set; }
    /// <summary>Extra highlighted items (e.g. preview of what a PM selection box holds).</summary>
    public IReadOnlyList<SelItem> Emphasis { get; set; } = Array.Empty<SelItem>();

    public event Action<PointerInfo>? ContextRequested;
    public event Action? CameraChanged;

    public static readonly Rgba HoverColor = new(1.0f, 0.50f, 0.05f);
    public static readonly Rgba SelectColor = new(0.20f, 0.55f, 1.0f);
    public static readonly Rgba EdgeColor = new(0.08f, 0.08f, 0.10f);

    GlRenderer? _r;
    readonly Dictionary<DisplayMesh, GpuMesh> _gpu = new(ReferenceEqualityComparer.Instance);
    readonly DynamicGeometry _dyn = new();

    // navigation state
    enum Nav { None, Rotate, Pan, Zoom, Roll }
    Nav _nav; Point _navLast; Point _rightDown; bool _rightDragged; Vector3 _pivot;
    bool _leftDown;

    // view animation
    Quaternion _animFromQ, _animToQ; Vector3 _animFromT, _animToT; float _animFromH, _animToH;
    readonly Stopwatch _animClock = new(); bool _animating;
    const double AnimMs = 280;

    public ViewportControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Camera.Rotation = Camera.StandardViews["Trimetric"];
    }

    // ---- GL lifecycle ----

    protected override void OnOpenGlInit(GlInterface gl)
    {
        var silk = GL.GetApi(gl.GetProcAddress);
        _r = new GlRenderer(silk, GlVersion.Type == GlProfileType.OpenGLES);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        foreach (var m in _gpu.Values) m.Dispose();
        _gpu.Clear();
        _r?.Dispose();
        _r = null;
    }

    public double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_r == null) return;
        var w = (int)Math.Max(1, Bounds.Width * Scaling); var h = (int)Math.Max(1, Bounds.Height * Scaling);
        Camera.Width = (float)Bounds.Width; Camera.Height = (float)Bounds.Height;
        StepAnimation();
        UpdateSceneRadius();

        _r.BeginFrame(w, h);
        _r.Background(new Rgba(0.80f, 0.86f, 0.93f), new Rgba(1f, 1f, 1f));
        var vp = Camera.ViewProj; var view = Camera.View;

        // sync GPU meshes
        var live = new HashSet<DisplayMesh>(ReferenceEqualityComparer.Instance);
        foreach (var b in Bodies) live.Add(b.Mesh);
        foreach (var b in PreviewBodies) live.Add(b.Mesh);
        foreach (var k in _gpu.Keys.Where(k => !live.Contains(k)).ToList()) { _gpu[k].Dispose(); _gpu.Remove(k); }
        foreach (var m in live) if (!_gpu.ContainsKey(m)) _gpu[m] = GpuMesh.Create(_r.Gl, m);

        var hl = CollectHighlights();

        // solids
        _r.SetupDepth(true, true);
        if (Style != DisplayStyle.Wireframe)
        {
            _r.PolygonOffset(true, 1, 1);
            foreach (var b in Bodies)
            {
                var g = _gpu[b.Mesh];
                if (Style == DisplayStyle.HiddenLinesVisible)
                {
                    _r.Gl.ColorMask(false, false, false, false);
                    _r.DrawMesh(g, PartColor, vp, view, Camera.Perspective);
                    _r.Gl.ColorMask(true, true, true, true);
                    continue;
                }
                _r.DrawMesh(g, PartColor, vp, view, Camera.Perspective);
                foreach (var (fr, color) in hl.Faces(b.Id, b.Mesh))
                    _r.DrawMesh(g, color, vp, view, Camera.Perspective, fr.IndexStart, fr.IndexCount);
            }
            _r.PolygonOffset(false);
        }
        // edges
        foreach (var b in Bodies)
        {
            var g = _gpu[b.Mesh];
            if (Style != DisplayStyle.Shaded) _r.DrawEdges(g, EdgeColor, vp);
            foreach (var (er, color) in hl.Edges(b.Id, b.Mesh))
            {
                _r.SetupDepth(false, false);
                _r.DrawEdges(g, color, vp, er.VertexStart, er.VertexCount);
                _r.SetupDepth(true, true);
            }
        }
        if (Style == DisplayStyle.HiddenLinesVisible)
        {
            // hidden edges, faint, on top
            _r.SetupDepth(true, false);
            _r.Gl.DepthFunc(DepthFunction.Greater);
            foreach (var b in Bodies) _r.DrawEdges(_gpu[b.Mesh], new Rgba(0.55f, 0.55f, 0.6f), vp);
            _r.Gl.DepthFunc(DepthFunction.Lequal);
        }

        // translucent preview
        if (PreviewBodies.Count > 0)
        {
            _r.Blend(true);
            _r.SetupDepth(true, false);
            foreach (var b in PreviewBodies)
            {
                var g = _gpu[b.Mesh];
                _r.DrawMesh(g, new Rgba(1f, 0.82f, 0.25f, 0.55f), vp, view, Camera.Perspective);
                _r.DrawEdges(g, new Rgba(0.55f, 0.4f, 0.0f, 0.9f), vp);
            }
            _r.Blend(false);
            _r.SetupDepth(true, true);
        }

        // dynamic geometry
        _dyn.Clear();
        DrawPlanes(_dyn, hl);
        DrawSketches(_dyn, hl);
        if (ShowOrigin) DrawOrigin(_dyn);
        Mode?.Draw(this, _dyn);

        _r.SetupDepth(true, true);
        _r.DrawDynamic(_dyn.Tris, PrimitiveType.Triangles, vp);
        _r.DrawDynamic(_dyn.Lines, PrimitiveType.Lines, vp);
        _r.Blend(true);
        _r.SetupDepth(true, false);
        _r.DrawDynamic(_dyn.TransTris, PrimitiveType.Triangles, vp);
        _r.SetupDepth(false, false);
        _r.DrawDynamic(_dyn.TopTris, PrimitiveType.Triangles, vp);
        _r.DrawDynamic(_dyn.TopLines, PrimitiveType.Lines, vp);
        _r.Blend(false);

        DrawTriad(w, h);
        _r.EndFrame(fb);

        if (_animating) RequestNextFrameRendering();
    }

    void UpdateSceneRadius()
    {
        var b = Box3.Empty;
        foreach (var body in Bodies) b.Add(body.Mesh.Bounds);
        var r = b.IsEmpty ? 100f : (float)Math.Max(100, b.Diagonal + (Camera.V(Camera.Target) - b.Center).Length);
        Camera.SceneRadius = r;
    }

    // ---- highlights ----

    sealed class Highlights
    {
        public readonly List<(SelItem item, Rgba color)> Items = new();

        public IEnumerable<(FaceRange, Rgba)> Faces(string bodyId, DisplayMesh m)
        {
            foreach (var (it, c) in Items)
            {
                if (it.Kind == SelKind.Face && it.BodyId == bodyId)
                {
                    foreach (var fr in m.Faces) if (fr.Name == it.Name) yield return (fr, c);
                }
                else if (it.Kind == SelKind.Feature && it.FeatureId != null)
                {
                    var prefix = it.FeatureId + ".";
                    foreach (var fr in m.Faces) if (fr.Name.StartsWith(prefix, StringComparison.Ordinal) || fr.Name.Contains("." + prefix, StringComparison.Ordinal)) yield return (fr, c);
                }
                else if (it.Kind == SelKind.Body && it.BodyId == bodyId)
                    foreach (var fr in m.Faces) yield return (fr, c);
            }
        }

        public IEnumerable<(EdgeRange, Rgba)> Edges(string bodyId, DisplayMesh m)
        {
            foreach (var (it, c) in Items)
                if (it.Kind == SelKind.Edge && it.BodyId == bodyId && it.EdgeIndex >= 0 && it.EdgeIndex < m.Edges.Count)
                    yield return (m.Edges[it.EdgeIndex], c);
        }

        public Rgba? Of(string key)
        {
            foreach (var (it, c) in Items) if (it.Key == key) return c;
            return null;
        }
    }

    Highlights CollectHighlights()
    {
        var h = new Highlights();
        foreach (var e in Emphasis) h.Items.Add((e, SelectColor.WithAlpha(1)));
        if (Selection != null) foreach (var s in Selection.Items) h.Items.Add((s, SelectColor));
        if (Hover != null) h.Items.Add((Hover, HoverColor));
        // faces are drawn tinted rather than solid
        for (int i = 0; i < h.Items.Count; i++)
        {
            var (it, c) = h.Items[i];
            if (it.Kind is SelKind.Face or SelKind.Feature or SelKind.Body)
                h.Items[i] = (it, Mix(PartColor, c, it == Hover ? 0.38f : 0.55f));
        }
        return h;
    }

    static Rgba Mix(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    // ---- scene decorations ----

    void DrawPlanes(DynamicGeometry g, Highlights hl)
    {
        foreach (var p in Planes)
        {
            var key = new SelItem { Kind = SelKind.Plane, Name = p.Name, FeatureId = p.FeatureId }.Key;
            var hc = hl.Of(key);
            if (!ShowPlanes && hc == null) continue;
            var f = p.Frame; var s = p.HalfSize;
            var a = f.ToWorld(-s, -s); var b = f.ToWorld(s, -s); var c = f.ToWorld(s, s); var d = f.ToWorld(-s, s);
            var edge = hc ?? new Rgba(0.35f, 0.45f, 0.6f, 0.9f);
            var fill = (hc ?? new Rgba(0.55f, 0.68f, 0.85f)).WithAlpha(hc != null ? 0.25f : 0.10f);
            g.Quad(a, b, c, d, fill, transparent: true);
            g.Line(a, b, edge); g.Line(b, c, edge); g.Line(c, d, edge); g.Line(d, a, edge);
        }
    }

    void DrawSketches(DynamicGeometry g, Highlights hl)
    {
        foreach (var s in Sketches)
        {
            var key = new SelItem { Kind = SelKind.Sketch, FeatureId = s.FeatureId }.Key;
            var c = hl.Of(key) ?? new Rgba(0.15f, 0.2f, 0.45f);
            SketchDrawing.DrawStatic(g, s.Result, c, Camera.PixelSize);
        }
    }

    void DrawOrigin(DynamicGeometry g)
    {
        var s = Camera.PixelSize * 28;
        var c = new Rgba(0.2f, 0.25f, 0.65f, 0.9f);
        g.Line(Vec3.Zero, new Vec3(s, 0, 0), c);
        g.Line(Vec3.Zero, new Vec3(0, s, 0), c);
        g.Line(Vec3.Zero, new Vec3(0, 0, s), c);
    }

    void DrawTriad(int w, int h)
    {
        if (_r == null) return;
        var size = (int)(90 * Scaling);
        _r.Viewport(0, 0, size, size);
        _r.ClearDepth();
        var rot = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(Camera.Rotation));
        var proj = Matrix4x4.CreateOrthographic(2.6f, 2.6f, -5, 5);
        var vp = rot * proj;
        var d = new List<float>();
        void L(Vector3 a, Vector3 b, Rgba c)
        {
            d.AddRange(new[] { a.X, a.Y, a.Z, c.R, c.G, c.B, c.A, b.X, b.Y, b.Z, c.R, c.G, c.B, c.A });
        }
        L(Vector3.Zero, Vector3.UnitX, new Rgba(0.85f, 0.1f, 0.1f));
        L(Vector3.Zero, Vector3.UnitY, new Rgba(0.1f, 0.65f, 0.1f));
        L(Vector3.Zero, Vector3.UnitZ, new Rgba(0.1f, 0.2f, 0.9f));
        _r.SetupDepth(false, false);
        _r.DrawDynamic(d, PrimitiveType.Lines, vp);
        _r.Viewport(0, 0, w, h);
    }

    /// <summary>Screen position (DIPs) of a triad axis tip, used for X/Y/Z labels.</summary>
    public Point TriadLabel(Vector3 axis)
    {
        var v = Vector3.Transform(axis * 1.25f, Quaternion.Conjugate(Camera.Rotation));
        double half = 45;
        return new Point(half + v.X / 1.3 * half, Bounds.Height - half - v.Y / 1.3 * half);
    }

    // ---- public helpers ----

    public void Redraw()
    {
        RequestNextFrameRendering();
        Mode?.UpdateOverlay(this);
        CameraChanged?.Invoke();
    }

    public void AnimateTo(Quaternion q, Vector3? target = null, float? halfHeight = null)
    {
        _animFromQ = Camera.Rotation; _animToQ = q;
        _animFromT = Camera.Target; _animToT = target ?? Camera.Target;
        _animFromH = Camera.HalfHeight; _animToH = halfHeight ?? Camera.HalfHeight;
        _animClock.Restart(); _animating = true;
        RequestNextFrameRendering();
    }

    void StepAnimation()
    {
        if (!_animating) return;
        var t = (float)Math.Min(1, _animClock.Elapsed.TotalMilliseconds / AnimMs);
        var e = t < 0.5f ? 2 * t * t : 1 - MathF.Pow(-2 * t + 2, 2) / 2;
        Camera.Rotation = Quaternion.Slerp(_animFromQ, _animToQ, e);
        Camera.Target = Vector3.Lerp(_animFromT, _animToT, e);
        Camera.HalfHeight = _animFromH + (_animToH - _animFromH) * e;
        if (t >= 1) _animating = false;
        Dispatcher.UIThread.Post(() => { Mode?.UpdateOverlay(this); CameraChanged?.Invoke(); });
    }

    public Box3 SceneBounds()
    {
        var b = Box3.Empty;
        foreach (var body in Bodies) b.Add(body.Mesh.Bounds);
        foreach (var s in Sketches)
            foreach (var p in s.Result.Sketch.Points) b.Add(s.Result.Frame.ToWorld(p.P));
        return b;
    }

    public void ZoomToFit(bool animate = true, Box3? box = null)
    {
        var b = box ?? SceneBounds();
        if (b.IsEmpty) { b.Add(new Vec3(-50, -50, -50)); b.Add(new Vec3(50, 50, 50)); }
        var tmp = new Camera { Width = Camera.Width, Height = Camera.Height, Rotation = Camera.Rotation };
        tmp.Fit(b);
        if (animate) AnimateTo(Camera.Rotation, tmp.Target, tmp.HalfHeight);
        else { Camera.Target = tmp.Target; Camera.HalfHeight = tmp.HalfHeight; Redraw(); }
    }

    public void StandardView(string name, bool fit = false)
    {
        var q = Camera.StandardViews[name];
        if (fit)
        {
            var tmp = new Camera { Width = Camera.Width, Height = Camera.Height, Rotation = q };
            var b = SceneBounds();
            if (b.IsEmpty) { b.Add(new Vec3(-50, -50, -50)); b.Add(new Vec3(50, 50, 50)); }
            tmp.Fit(b);
            AnimateTo(q, tmp.Target, tmp.HalfHeight);
        }
        else AnimateTo(q);
    }

    /// <summary>Look straight at a frame (Normal To).</summary>
    public void NormalTo(Frame f)
    {
        var q = Camera.LookRotation(Camera.F(f.Normal), Camera.F(f.YDir));
        // keep the target in the plane's vicinity
        var t = Camera.Target;
        var tv = Camera.V(t);
        var proj = f.Plane.Project(tv);
        AnimateTo(q, Camera.F(proj));
    }

    public void Rotate(float yawDeg, float pitchDeg)
    {
        var pivot = Camera.F(SceneBounds() is { IsEmpty: false } b ? b.Center : Vec3.Zero);
        var q0 = Camera.Rotation; var t0 = Camera.Target;
        var qy = Quaternion.CreateFromAxisAngle(Camera.Up, yawDeg * MathF.PI / 180);
        var qx = Quaternion.CreateFromAxisAngle(Camera.Right, pitchDeg * MathF.PI / 180);
        var q = Quaternion.Normalize(qy * qx);
        AnimateTo(Quaternion.Normalize(q * q0), pivot + Vector3.Transform(t0 - pivot, q));
    }

    // ---- input ----

    PointerInfo Info(PointerEventArgs e, int clicks = 0)
    {
        var p = e.GetPosition(this);
        return new PointerInfo(p.X, p.Y, e.KeyModifiers, clicks);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        _navLast = pt.Position;
        if (props.IsMiddleButtonPressed)
        {
            _nav = (e.KeyModifiers & KeyModifiers.Control) != 0 ? Nav.Pan
                 : (e.KeyModifiers & KeyModifiers.Shift) != 0 ? Nav.Zoom
                 : (e.KeyModifiers & KeyModifiers.Alt) != 0 ? Nav.Roll
                 : Nav.Rotate;
            _pivot = ComputePivot(pt.Position);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed)
        {
            _rightDown = pt.Position; _rightDragged = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed)
        {
            if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                _nav = Nav.Rotate; _pivot = ComputePivot(pt.Position);
                e.Pointer.Capture(this);
                return;
            }
            _leftDown = true;
            e.Pointer.Capture(this);
            Mode?.PointerDown(this, Info(e, e.ClickCount));
            e.Handled = true;
        }
    }

    Vector3 ComputePivot(Point p)
    {
        var b = SceneBounds();
        return b.IsEmpty ? Camera.Target : Camera.F(b.Center);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        var dx = (float)(pos.X - _navLast.X); var dy = (float)(pos.Y - _navLast.Y);
        if (pt.Properties.IsRightButtonPressed && _nav == Nav.None)
        {
            if (!_rightDragged && Math.Abs(pos.X - _rightDown.X) + Math.Abs(pos.Y - _rightDown.Y) > 4) _rightDragged = true;
            if (_rightDragged) { Camera.Pan(dx, dy); _navLast = pos; Redraw(); }
            return;
        }
        if (_nav != Nav.None)
        {
            _animating = false;
            switch (_nav)
            {
                case Nav.Rotate: Camera.Orbit(dx, dy, _pivot); break;
                case Nav.Pan: Camera.Pan(dx, dy); break;
                case Nav.Zoom: Camera.ZoomAt(MathF.Exp(dy * 0.01f), (float)Bounds.Width / 2, (float)Bounds.Height / 2); break;
                case Nav.Roll: Camera.Roll(dx * 0.01f); break;
            }
            _navLast = pos;
            Redraw();
            return;
        }
        _navLast = pos;
        Mode?.PointerMove(this, Info(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_nav != Nav.None) { _nav = Nav.None; e.Pointer.Capture(null); return; }
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            e.Pointer.Capture(null);
            if (!_rightDragged) ContextRequested?.Invoke(Info(e));
            _rightDragged = false;
            return;
        }
        if (e.InitialPressMouseButton == MouseButton.Left && _leftDown)
        {
            _leftDown = false;
            e.Pointer.Capture(null);
            Mode?.PointerUp(this, Info(e));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _animating = false;
        var p = e.GetPosition(this);
        var dir = ReverseWheel ? -1 : 1;
        // SolidWorks default: wheel forward (away from you) zooms out
        var f = MathF.Pow(1.15f, (float)(e.Delta.Y * dir));
        Camera.ZoomAt(f, (float)p.X, (float)p.Y);
        Redraw();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (Hover != null) { Hover = null; Redraw(); }
    }

    // ---- picking ----

    public SelItem? Pick(double x, double y, SelFilter filter)
    {
        var (ro, rd) = Camera.Ray(x, y);
        double bestT = double.MaxValue; SelItem? best = null;
        const double edgePx = 6, sketchPx = 6;

        // faces
        if (Style != DisplayStyle.Wireframe)
            foreach (var b in Bodies)
            {
                var m = b.Mesh;
                foreach (var fr in m.Faces)
                {
                    if (!RayBox(ro, rd, fr.Bounds)) continue;
                    for (int i = fr.IndexStart; i < fr.IndexStart + fr.IndexCount; i += 3)
                    {
                        var t = RayTri(ro, rd, P(m, m.Indices[i]), P(m, m.Indices[i + 1]), P(m, m.Indices[i + 2]));
                        if (t > 0 && t < bestT)
                        {
                            bestT = t;
                            var planar = !fr.Surface.IsCurved;
                            best = new SelItem
                            {
                                Kind = SelKind.Face, BodyId = b.Id, Name = fr.Name, Point = ro + rd * t,
                                Normal = planar ? fr.FirstPlane.Normal : Vec3.Zero, Planar = planar, FacePlane = planar ? fr.FirstPlane : null,
                            };
                        }
                    }
                }
            }
        var faceHit = best;
        var faceDepth = bestT;
        if ((filter & (SelFilter.Face | SelFilter.PlanarFace | SelFilter.Body | SelFilter.Feature)) == 0) best = null;
        else if ((filter & SelFilter.PlanarFace) != 0 && (filter & SelFilter.Face) == 0 && best is { Planar: false }) best = null;

        // planes (translucent: picked when nothing nearer)
        if ((filter & SelFilter.Plane) != 0)
            foreach (var p in Planes)
            {
                if (!ShowPlanes) continue;
                var t = p.Frame.Plane.IntersectRay(ro, rd);
                if (double.IsNaN(t) || t <= 0 || t >= faceDepth - 1e-6) continue;
                var local = p.Frame.ToLocal(ro + rd * t);
                if (Math.Abs(local.X) > p.HalfSize || Math.Abs(local.Y) > p.HalfSize) continue;
                // prefer plane borders slightly: always accept but faces nearer win
                if (best == null || t < bestT)
                {
                    bestT = t;
                    best = new SelItem { Kind = SelKind.Plane, Name = p.Name, FeatureId = p.FeatureId, Frame = p.Frame, Point = ro + rd * t, Normal = p.Frame.Normal };
                }
            }

        // edges (screen distance), must be visible
        if ((filter & SelFilter.Edge) != 0)
        {
            double bestPx = edgePx; SelItem? bestEdge = null;
            foreach (var b in Bodies)
            {
                var m = b.Mesh;
                var topo = b.Solid.Topology;
                foreach (var er in m.Edges)
                {
                    for (int i = er.VertexStart; i < er.VertexStart + er.VertexCount; i += 2)
                    {
                        var a = E(m, i); var c = E(m, i + 1);
                        var (d, wp) = ScreenDistance(a, c, x, y);
                        if (d >= bestPx) continue;
                        if (faceHit != null && Style != DisplayStyle.Wireframe)
                        {
                            var depth = Vec3.Dot(wp - ro, rd);
                            if (depth > faceDepth + Camera.PixelSize * 3 + 1e-4) continue;
                        }
                        bestPx = d;
                        var te = topo.Edges[er.EdgeIndex];
                        bestEdge = new SelItem
                        {
                            Kind = SelKind.Edge, BodyId = b.Id, EdgeIndex = er.EdgeIndex, Point = te.Midpoint,
                            Edge = new Core.Model.EdgeRef { A = te.FaceA, B = te.FaceB, Body = b.Id, Hint = te.Midpoint },
                            EdgeLinear = te.IsLinear, EdgeCircular = te.Closed,
                        };
                    }
                }
            }
            if (bestEdge != null) best = bestEdge;
        }

        // sketches (screen distance)
        if ((filter & SelFilter.Sketch) != 0)
        {
            double bestPx = sketchPx; SelItem? bestSk = null;
            foreach (var s in Sketches)
            {
                foreach (var (a, c) in SketchDrawing.Segments(s.Result))
                {
                    var (d, _) = ScreenDistance(a, c, x, y);
                    if (d < bestPx) { bestPx = d; bestSk = new SelItem { Kind = SelKind.Sketch, FeatureId = s.FeatureId, Frame = s.Result.Frame }; }
                }
            }
            if (bestSk != null) best = bestSk;
        }

        if (best != null && (filter & SelFilter.Body) != 0 && best.Kind == SelKind.Face)
            best = new SelItem { Kind = SelKind.Body, BodyId = best.BodyId, Point = best.Point };
        return best;
    }

    /// <summary>Face under the cursor (any filter), used e.g. for sketch-on-face inference.</summary>
    public (double t, SelItem? hit) RayFaces(double x, double y)
    {
        var item = Pick(x, y, SelFilter.Face);
        if (item == null) return (double.NaN, null);
        var (ro, rd) = Camera.Ray(x, y);
        return (Vec3.Dot(item.Point - ro, rd), item);
    }

    (double dist, Vec3 world) ScreenDistance(Vec3 a, Vec3 b, double x, double y)
    {
        var pa = Camera.Project(a); var pb = Camera.Project(b);
        var ax = pa.X; var ay = pa.Y; var bx = pb.X; var by = pb.Y;
        double dx = bx - ax, dy = by - ay; var l2 = dx * dx + dy * dy;
        double t = l2 > 1e-12 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
        var px = ax + dx * t - x; var py = ay + dy * t - y;
        return (Math.Sqrt(px * px + py * py), Vec3.Lerp(a, b, t));
    }

    static Vec3 P(DisplayMesh m, int i) => new(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
    static Vec3 E(DisplayMesh m, int i) => new(m.EdgeLines[i * 3], m.EdgeLines[i * 3 + 1], m.EdgeLines[i * 3 + 2]);

    static bool RayBox(Vec3 o, Vec3 d, Box3 b)
    {
        double tmin = double.NegativeInfinity, tmax = double.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            var oi = o[i]; var di = d[i]; var mn = b.Min[i] - 1e-6; var mx = b.Max[i] + 1e-6;
            if (Math.Abs(di) < 1e-15) { if (oi < mn || oi > mx) return false; continue; }
            var t1 = (mn - oi) / di; var t2 = (mx - oi) / di;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        return tmax > 0;
    }

    static double RayTri(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vec3.Cross(d, e2);
        var det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return -1;
        var inv = 1 / det;
        var s = o - a;
        var u = Vec3.Dot(s, p) * inv;
        if (u < -1e-9 || u > 1 + 1e-9) return -1;
        var q = Vec3.Cross(s, e1);
        var v = Vec3.Dot(d, q) * inv;
        if (v < -1e-9 || u + v > 1 + 1e-9) return -1;
        return Vec3.Dot(e2, q) * inv;
    }

    /// <summary>Intersects the cursor ray with a frame's plane; returns sketch coordinates.</summary>
    public Vec2? PlanePoint(Frame f, double x, double y)
    {
        var (o, d) = Camera.Ray(x, y);
        var t = f.Plane.IntersectRay(o, d);
        if (double.IsNaN(t)) return null;
        return f.ToLocal(o + d * t);
    }
}
