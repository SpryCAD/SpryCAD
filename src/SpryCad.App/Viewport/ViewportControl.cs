using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using SpryCad.App.Session;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using Silk.NET.OpenGL;

namespace SpryCad.App.Viewport;

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

/// <summary>
/// A mesh drawn where a rigid placement puts it, in a color (an assembly's component: one GPU mesh for all the
/// components of a part). <c>Model</c> is the placement as a row-vector matrix (rotation, then translation).
/// <c>Faces</c>: faces drawn over it in other colors (hover, selection), by name.
/// </summary>
public readonly record struct MeshInstance(string Id, DisplayMesh Mesh, Matrix4x4 Model, Rgba Color, IReadOnlyList<(string face, Rgba color)>? Faces = null);

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
    /// <summary>Placed meshes (an assembly's components), drawn in the display style with their own colors.</summary>
    public IReadOnlyList<MeshInstance> Instances { get; set; } = Array.Empty<MeshInstance>();
    public Rgba PartColor = new(0.74f, 0.76f, 0.80f);
    /// <summary>
    /// Color by Feature: each feature's color, by id (see <see cref="Core.Model.FeatureColors"/>); the part's faces are
    /// shaded in the colors of the features that made them (others in the part color). Null: off.
    /// </summary>
    public IReadOnlyDictionary<string, Rgba>? ColorByFeature { get; set; }
    public DisplayStyle Style = DisplayStyle.ShadedWithEdges;
    public bool ShowPlanes = true;
    public bool ShowOrigin = true;
    public bool ReverseWheel;
    /// <summary>Draw the part faded (shaded styles), e.g. so the View Selector cube stands out over it.</summary>
    public bool Ghosted { get => _ghosted; set { if (_ghosted != value) { _ghosted = value; Redraw(); } } }
    bool _ghosted;
    const float GhostAlpha = 0.3f;
    /// <summary>What the view rotates about unless a rotation center was set by middle-clicking the model.</summary>
    public RotateCenter RotateAbout = RotateCenter.Selection;

    // highlight state
    public SelItem? Hover { get; set; }
    public SelectionSet? Selection { get; set; }
    /// <summary>Extra highlighted items (e.g. preview of what a PM selection box holds).</summary>
    public IReadOnlyList<SelItem> Emphasis { get; set; } = Array.Empty<SelItem>();
    /// <summary>Extra highlighted items in colors of their own (an item given twice: the first wins).</summary>
    public IReadOnlyList<(SelItem item, Rgba color)> Marked { get; set; } = Array.Empty<(SelItem, Rgba)>();
    /// <summary>Model edges (polylines) to show in red: where the feature being edited/selected failed.</summary>
    public IReadOnlyList<Vec3[]> ProblemEdges { get; set; } = Array.Empty<Vec3[]>();

    public event Action<PointerInfo>? ContextRequested;
    /// <summary>
    /// Mouse gestures (as SolidWorks): a right-button drag. Began (press point), Moved (pointer), Ended (pointer).
    /// Without a handler a right-drag pans.
    /// </summary>
    public event Action<Point>? GestureBegan, GestureMoved, GestureEnded;
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
    // rotation center set by middle-clicking the model (SolidWorks): kept until a middle-click on empty space
    Vector3? _pinnedPivot;
    Point _middleDown; bool _middleDragged;

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
        try { App.SetGlInfo($"{silk.GetStringS(StringName.Vendor)} / {silk.GetStringS(StringName.Renderer)} / {silk.GetStringS(StringName.Version)}"); } catch { }
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
        long t0 = RenderProfile ? Stopwatch.GetTimestamp() : 0, tDyn0 = 0, tDyn1 = 0;
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
        foreach (var i in Instances) live.Add(i.Mesh);
        foreach (var k in _gpu.Keys.Where(k => !live.Contains(k)).ToList()) { _gpu[k].Dispose(); _gpu.Remove(k); }
        foreach (var m in live) if (!_gpu.ContainsKey(m)) _gpu[m] = GpuMesh.Create(_r.Gl, m);

        var hl = CollectHighlights();

        // solids
        _r.SetupDepth(true, true);
        var ghost = Ghosted && Style is DisplayStyle.ShadedWithEdges or DisplayStyle.Shaded;
        if (ghost)
        {
            // faded: depth first so only the nearest surface shows, then its colour blended over the background
            _r.PolygonOffset(true, 1, 1);
            _r.Gl.ColorMask(false, false, false, false);
            foreach (var b in Bodies) _r.DrawMesh(_gpu[b.Mesh], PartColor, vp, view, Camera.Perspective);
            _r.Gl.ColorMask(true, true, true, true);
            _r.Blend(true);
            _r.SetupDepth(true, false);
            foreach (var b in Bodies)
            {
                var g = _gpu[b.Mesh];
                DrawBody(g, GhostAlpha, vp, view);
                foreach (var (fr, color) in hl.Faces(b.Id, b.Mesh))
                    _r.DrawMesh(g, color.WithAlpha(GhostAlpha), vp, view, Camera.Perspective, fr.IndexStart, fr.IndexCount);
            }
            _r.Blend(false);
            _r.SetupDepth(true, true);
            _r.PolygonOffset(false);
        }
        else if (Style != DisplayStyle.Wireframe)
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
                DrawBody(g, 1, vp, view);
                foreach (var (fr, color) in hl.Faces(b.Id, b.Mesh))
                    _r.DrawMesh(g, color, vp, view, Camera.Perspective, fr.IndexStart, fr.IndexCount);
            }
            _r.PolygonOffset(false);
        }
        // edges
        if (ghost) _r.Blend(true);
        foreach (var b in Bodies)
        {
            var g = _gpu[b.Mesh];
            if (Style != DisplayStyle.Shaded) _r.DrawEdges(g, ghost ? EdgeColor.WithAlpha(GhostAlpha) : EdgeColor, vp);
            foreach (var (er, color) in hl.Edges(b.Id, b.Mesh))
            {
                _r.SetupDepth(false, false);
                _r.DrawEdges(g, color, vp, er.VertexStart, er.VertexCount);
                _r.SetupDepth(true, true);
            }
        }
        if (ghost) _r.Blend(false);
        DrawInstances(vp, view);
        if (Style == DisplayStyle.HiddenLinesVisible)
        {
            // hidden edges, faint, on top
            _r.SetupDepth(true, false);
            _r.Gl.DepthFunc(DepthFunction.Greater);
            foreach (var b in Bodies) _r.DrawEdges(_gpu[b.Mesh], new Rgba(0.55f, 0.55f, 0.6f), vp);
            foreach (var i in Instances) _r.DrawEdges(_gpu[i.Mesh], new Rgba(0.55f, 0.55f, 0.6f), i.Model * vp);
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
        if (RenderProfile) tDyn0 = Stopwatch.GetTimestamp();
        _dyn.Clear();
        DrawPlanes(_dyn, hl);
        DrawSketches(_dyn, hl);
        if (ShowOrigin) DrawOrigin(_dyn);
        long tm0 = RenderProfile ? Stopwatch.GetTimestamp() : 0;
        Mode?.Draw(this, _dyn);
        if (RenderProfile) { var ms = Stopwatch.GetElapsedTime(tm0).TotalMilliseconds; _rnModeMs += ms; _rnModeMax = Math.Max(_rnModeMax, ms); }
        DrawPointAndSegmentHighlights(_dyn, hl);
        if (AxisPreview is { } ap) DrawAxisPreview(_dyn, ap.dir, ap.color);
        DrawMeasureLines(_dyn);
        if (CenterOfMass is { } com) DrawCenterOfMass(_dyn, com.center, com.axes, com.length);
        DrawBendLines(_dyn);
        DrawCosmeticThreads(_dyn);
        if (Hover is { Kind: SelKind.Vertex, FeatureId: null } hv) DrawPointHover(_dyn, hv);
        if (_nav == Nav.Rotate) DrawPivot(_dyn, _pivot);
        else if (Spinning) DrawPivot(_dyn, _spinPivot);
        else if (_pinnedPivot is { } pin) DrawPivot(_dyn, pin);
        // edges a feature failed on (e.g. a fillet that does not fit), drawn on top in red
        foreach (var pl in ProblemEdges)
            for (int i = 0; i + 1 < pl.Length; i++) _dyn.Line(pl[i], pl[i + 1], new Rgba(0.9f, 0.1f, 0.1f), top: true);
        if (RenderProfile) tDyn1 = Stopwatch.GetTimestamp();

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
        if (RenderProfile) NoteRender(t0, tDyn0, tDyn1);
        if (PickProfile) ProfilePick();

        if (_animating || Spinning || RenderContinuous || PickProfile) RequestNextFrameRendering();
    }

    // SPRYCAD_PROFILE_RENDER: the CPU time of frames (the whole render call, and building its overlay geometry), and
    // how much overlay it uploads, in the log every 2 s; "continuous" renders frame after frame to measure a still view;
    // "picks" also runs the hover pick the pointer would (a component, or the model's items with every filter) at a
    // point sweeping across the view each frame, and logs its time
    static readonly bool RenderProfile = Environment.GetEnvironmentVariable("SPRYCAD_PROFILE_RENDER") != null;
    static readonly bool RenderContinuous = Environment.GetEnvironmentVariable("SPRYCAD_PROFILE_RENDER") == "continuous";
    static readonly bool PickProfile = Environment.GetEnvironmentVariable("SPRYCAD_PROFILE_RENDER") == "picks";
    int _pkCalls, _pkHits; double _pkMs, _pkMax; long _pkSince = Stopwatch.GetTimestamp(); ulong _pkSum = 14695981039346656037;

    void ProfilePick()
    {
        double x = Bounds.Width * (0.1 + 0.8 * (_pkCalls % 240) / 240.0), y = Bounds.Height * (0.3 + 0.4 * (_pkCalls % 7) / 7.0);
        var t = Stopwatch.GetTimestamp();
        string? what = Instances.Count > 0 ? PickInstance(x, y) is { } pi ? $"{pi.id} {pi.face} {pi.t:R}" : null : Pick(x, y, SelFilter.Any)?.Key;
        var now = Stopwatch.GetTimestamp();
        var ms = Stopwatch.GetElapsedTime(t, now).TotalMilliseconds;
        _pkCalls++; _pkMs += ms; _pkMax = Math.Max(_pkMax, ms);
        // what was hit, summed up: two builds picking the same things at the same points log the same sum
        if (what != null) { _pkHits++; foreach (var ch in what) _pkSum = (_pkSum ^ ch) * 1099511628211; }
        if (_pkCalls < 240) return;
        var span = Stopwatch.GetElapsedTime(_pkSince, now).TotalSeconds;
        App.Note($"picks: {_pkCalls} in {span:0.0} s ({_pkHits} hits, sum {_pkSum:X16}), {_pkMs / _pkCalls:0.000} ms average ({_pkMax:0.000} worst) ({(Instances.Count > 0 ? "components" : "model items")})");
        _pkCalls = _pkHits = 0; _pkMs = _pkMax = 0; _pkSince = now; _pkSum = 14695981039346656037;
    }
    int _rnFrames; double _rnMs, _rnMax, _rnDynMs, _rnDynMax, _rnModeMs, _rnModeMax; long _rnFloats; long _rnSince = Stopwatch.GetTimestamp();

    void NoteRender(long t0, long tDyn0, long tDyn1)
    {
        var now = Stopwatch.GetTimestamp();
        double ms = Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds, dyn = Stopwatch.GetElapsedTime(tDyn0, tDyn1).TotalMilliseconds;
        _rnFrames++; _rnMs += ms; _rnMax = Math.Max(_rnMax, ms); _rnDynMs += dyn; _rnDynMax = Math.Max(_rnDynMax, dyn);
        _rnFloats += _dyn.Lines.Count + _dyn.Tris.Count + _dyn.TopLines.Count + _dyn.TopTris.Count + _dyn.TransTris.Count;
        var span = Stopwatch.GetElapsedTime(_rnSince, now).TotalSeconds;
        if (span < 2) return;
        App.Note($"render: {_rnFrames} frames in {span:0.0} s, {_rnMs / _rnFrames:0.00} ms average ({_rnMax:0.00} worst); " +
                 $"overlay built in {_rnDynMs / _rnFrames:0.00} ms ({_rnDynMax:0.00} worst), {_rnFloats / _rnFrames / 7 / 1000.0:0.0}k vertices a frame" +
                 (Mode != null ? $"; {Mode.GetType().Name} drew in {_rnModeMs / _rnFrames:0.00} ms ({_rnModeMax:0.00} worst)" : ""));
        _rnFrames = 0; _rnMs = _rnMax = _rnDynMs = _rnDynMax = _rnModeMs = _rnModeMax = 0; _rnFloats = 0; _rnSince = now;
    }

    /// <summary>A body shaded in the part color, or (Color by Feature) each run of one feature's faces in its color.</summary>
    void DrawBody(GpuMesh g, float alpha, Matrix4x4 vp, Matrix4x4 view)
    {
        if (ColorByFeature is not { } colors) { _r!.DrawMesh(g, PartColor.WithAlpha(alpha), vp, view, Camera.Perspective); return; }
        foreach (var (start, count, feature) in _runs.GetValue(g.Source, Core.Model.FeatureColors.Runs))
        {
            var c = feature != null && colors.TryGetValue(feature, out var fc) ? fc : PartColor;
            _r!.DrawMesh(g, c.WithAlpha(alpha), vp, view, Camera.Perspective, start, count);
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DisplayMesh, List<(int start, int count, string? feature)>> _runs = new();

    /// <summary>A face's color by its feature (Color by Feature), else the part color.</summary>
    Rgba FaceColor(string faceName) =>
        ColorByFeature is { } colors && Core.Model.FeatureColors.FeatureOf(faceName) is { } id && colors.TryGetValue(id, out var c) ? c : PartColor;

    static Rgba Vivid(Rgba c)
    {
        var v = Core.Model.FeatureColors.Vivid(new double[] { c.R, c.G, c.B });
        return new Rgba((float)v[0], (float)v[1], (float)v[2], c.A);
    }

    /// <summary>The placed meshes: shaded (or depth only for hidden lines) in their colors, then their edges.</summary>
    void DrawInstances(Matrix4x4 vp, Matrix4x4 view)
    {
        if (_r == null || Instances.Count == 0) return;
        if (Style != DisplayStyle.Wireframe)
        {
            _r.PolygonOffset(true, 1, 1);
            if (Style == DisplayStyle.HiddenLinesVisible) _r.Gl.ColorMask(false, false, false, false);
            foreach (var i in Instances)
            {
                // (an instance see-through by its color: its own faces solid now, the rest blended over after)
                if (i.Color.A >= 1) _r.DrawMesh(_gpu[i.Mesh], i.Color, i.Model * vp, i.Model * view, Camera.Perspective);
                if (i.Faces != null && Style != DisplayStyle.HiddenLinesVisible)
                    foreach (var (name, color) in i.Faces)
                        foreach (var fr in i.Mesh.Faces)
                            if (fr.Name == name) _r.DrawMesh(_gpu[i.Mesh], color, i.Model * vp, i.Model * view, Camera.Perspective, fr.IndexStart, fr.IndexCount);
            }
            if (Style == DisplayStyle.HiddenLinesVisible) _r.Gl.ColorMask(true, true, true, true);
            if (Style != DisplayStyle.HiddenLinesVisible && Instances.Any(i => i.Color.A is > 0 and < 1))
            {
                _r.Blend(true);
                foreach (var i in Instances)
                    if (i.Color.A is > 0 and < 1) _r.DrawMesh(_gpu[i.Mesh], i.Color, i.Model * vp, i.Model * view, Camera.Perspective);
                _r.Blend(false);
            }
            _r.PolygonOffset(false);
        }
        if (Style != DisplayStyle.Shaded)
            foreach (var i in Instances)
                if (i.Color.A >= 1) _r.DrawEdges(_gpu[i.Mesh], EdgeColor, i.Model * vp);
                else if (i.Color.A > 0) { _r.Blend(true); _r.DrawEdges(_gpu[i.Mesh], EdgeColor.WithAlpha(i.Color.A), i.Model * vp); _r.Blend(false); }
    }

    /// <summary>A mesh box placed by a row-vector matrix: the box of its eight corners.</summary>
    static Box3 Placed(Box3 b, Matrix4x4 m)
    {
        var r = Box3.Empty;
        if (b.IsEmpty) return r;
        for (int c = 0; c < 8; c++)
        {
            var v = Vector3.Transform(new Vector3((float)((c & 1) == 0 ? b.Min.X : b.Max.X), (float)((c & 2) == 0 ? b.Min.Y : b.Max.Y), (float)((c & 4) == 0 ? b.Min.Z : b.Max.Z)), m);
            r.Add(new Vec3(v.X, v.Y, v.Z));
        }
        return r;
    }

    /// <summary>
    /// The placed mesh under a screen point: its id, the distance along the view ray, the point hit (world) and the
    /// face hit (its name), or null. Each mesh is tested in its own coordinates (the ray carried back by the inverse
    /// placement).
    /// </summary>
    public (string id, double t, Vec3 point, string? face)? PickInstance(double x, double y)
    {
        var (ro, rd) = Camera.Ray(x, y);
        (string id, double t, Vec3 point, string? face)? best = null;
        DisplayMesh? bestMesh = null; int bestTri = -1;
        foreach (var inst in Instances)
        {
            if (!Matrix4x4.Invert(inst.Model, out var inv)) continue;
            var o = Vector3.Transform(new Vector3((float)ro.X, (float)ro.Y, (float)ro.Z), inv);
            var d = Vector3.TransformNormal(new Vector3((float)rd.X, (float)rd.Y, (float)rd.Z), inv);
            var m = inst.Mesh;
            if (!RayHitsBox(o, d, m.Bounds)) continue;
            var lo = new Vec3(o.X, o.Y, o.Z); var ld = new Vec3(d.X, d.Y, d.Z);
            // only the faces whose boxes the ray meets (in order: the same nearest triangle as testing them all; the
            // boxes are of the exact points, padded for their positions' rounding to float)
            if (FacesCoverAll(m))
                foreach (var fr in m.Faces)
                {
                    if (!RayHitsBox(o, d, fr.Bounds, FloatPad(fr.Bounds))) continue;
                    for (int k = fr.IndexStart; k + 2 < fr.IndexStart + fr.IndexCount; k += 3) Test(k);
                }
            else
                for (int k = 0; k + 2 < m.Indices.Length; k += 3) Test(k);
            void Test(int k)
            {
                var t = RayTriangle(lo, ld, P(m, m.Indices[k]), P(m, m.Indices[k + 1]), P(m, m.Indices[k + 2]));
                if (t > 0 && (best == null || t < best.Value.t)) { best = (inst.Id, t, ro + rd * t, null); bestMesh = m; bestTri = k; }
            }
        }
        if (best is { } b && bestMesh != null)
            foreach (var fr in bestMesh.Faces)
                if (bestTri >= fr.IndexStart && bestTri < fr.IndexStart + fr.IndexCount) return b with { face = fr.Name };
        return best;
    }

    // whether the faces' ranges run through every triangle in order, each one after the last (as DisplayMesh makes them)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DisplayMesh, object> _covered = new();
    static bool FacesCoverAll(DisplayMesh m) => (bool)_covered.GetValue(m, mesh =>
    {
        int at = 0;
        foreach (var fr in mesh.Faces) { if (fr.IndexStart != at || fr.IndexCount % 3 != 0) return false; at += fr.IndexCount; }
        return at == mesh.Indices.Length;
    });

    // more than a float's rounding of the box's coordinates (a face's box may be flat on one axis)
    static double FloatPad(Box3 b) => 1e-6 * (1 + Math.Max(Math.Max(Math.Abs(b.Min.X), Math.Abs(b.Max.X)),
        Math.Max(Math.Max(Math.Abs(b.Min.Y), Math.Abs(b.Max.Y)), Math.Max(Math.Abs(b.Min.Z), Math.Abs(b.Max.Z)))));

    static bool RayHitsBox(Vector3 o, Vector3 d, Box3 b, double extraPad = 0)
    {
        if (b.IsEmpty) return false;
        double t0 = double.MinValue, t1 = double.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            double oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            double lo = a == 0 ? b.Min.X : a == 1 ? b.Min.Y : b.Min.Z, hi = a == 0 ? b.Max.X : a == 1 ? b.Max.Y : b.Max.Z;
            var pad = 1e-6 * (1 + Math.Abs(hi - lo)) + extraPad;
            if (Math.Abs(da) < 1e-12) { if (oa < lo - pad || oa > hi + pad) return false; continue; }
            double ta = (lo - pad - oa) / da, tb = (hi + pad - oa) / da;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return t1 >= 0;
    }

    /// <summary>Möller–Trumbore: the distance along the ray to the triangle, or -1.</summary>
    static double RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = Vec3.Cross(d, e2);
        var det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return -1;
        var inv = 1 / det;
        var s = o - a;
        var u = Vec3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        var q = Vec3.Cross(s, e1);
        var v = Vec3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        return Vec3.Dot(e2, q) * inv;
    }

    void UpdateSceneRadius()
    {
        var b = Box3.Empty;
        foreach (var body in Bodies) b.Add(body.Mesh.Bounds);
        foreach (var i in Instances) b.Add(Placed(i.Mesh.Bounds, i.Model));
        var r = b.IsEmpty ? 100f : (float)Math.Max(100, b.Diagonal + (Camera.V(Camera.Target) - b.Center).Length);
        Camera.SceneRadius = r;
    }

    // ---- highlights ----

    sealed class Highlights
    {
        public readonly List<(SelItem item, Rgba color)> Items = new();
        /// <summary>Each face's own color for an item's color (Color by Feature: faces differ in color), else null.</summary>
        public Func<SelItem, Rgba, FaceRange, Rgba>? Tint;

        public IEnumerable<(FaceRange, Rgba)> Faces(string bodyId, DisplayMesh m)
        {
            foreach (var (it, c) in Items)
            {
                if (it.Kind == SelKind.Face && it.BodyId == bodyId)
                {
                    foreach (var fr in m.Faces) if (fr.Name == it.Name) yield return (fr, Tint?.Invoke(it, c, fr) ?? c);
                }
                else if (it.Kind == SelKind.Feature && it.FeatureId != null)
                {
                    var prefix = it.FeatureId + ".";
                    foreach (var fr in m.Faces) if (fr.Name.StartsWith(prefix, StringComparison.Ordinal) || fr.Name.Contains("." + prefix, StringComparison.Ordinal)) yield return (fr, Tint?.Invoke(it, c, fr) ?? c);
                }
                else if (it.Kind == SelKind.Body && it.BodyId == bodyId)
                    foreach (var fr in m.Faces) yield return (fr, Tint?.Invoke(it, c, fr) ?? c);
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
        foreach (var m in Marked) h.Items.Add(m);
        foreach (var e in Emphasis) h.Items.Add((e, SelectColor.WithAlpha(1)));
        if (Selection != null) foreach (var s in Selection.Items) h.Items.Add((s, SelectColor));
        if (Hover != null) h.Items.Add((Hover, HoverColor));
        if (ColorByFeature != null)
        {
            // each face from its own color: what is hovered, and features (as picked in the tree), in their feature's
            // color made stronger; selected faces and bodies tinted with the selection color as usual
            var hover = Hover;
            h.Tint = (it, c, fr) =>
            {
                var own = FaceColor(fr.Name);
                return it == hover || it.Kind == SelKind.Feature ? Vivid(own) : Mix(own, c, 0.55f);
            };
            return h;
        }
        // faces are drawn tinted rather than solid
        for (int i = 0; i < h.Items.Count; i++)
        {
            var (it, c) = h.Items[i];
            if (it.Kind is SelKind.Face or SelKind.Feature or SelKind.Body)
                h.Items[i] = (it, it == Hover ? Mix(PartColor, new Rgba(1f, 0.8f, 0.5f), 0.35f) : Mix(PartColor, c, 0.55f));
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
        // each sketch's geometry kept while its result, color and the pixel size (zoom) stay: orbiting and panning
        // draw it again without tessellating its curves (a result is not changed once made)
        var px = Camera.PixelSize;
        foreach (var s in Sketches)
        {
            var key = new SelItem { Kind = SelKind.Sketch, FeatureId = s.FeatureId }.Key;
            var c = hl.Of(key) ?? new Rgba(0.15f, 0.2f, 0.45f);
            if (_sketchPieces.TryGetValue(s.Result, out var kept) && kept.color == c && kept.px == px) { g.Add(kept.piece); continue; }
            var m = g.Mark();
            SketchDrawing.DrawStatic(g, s.Result, c, px);
            _sketchPieces[s.Result] = (c, px, g.Since(m));
        }
        if (_sketchPieces.Count > Sketches.Count)
            foreach (var r in _sketchPieces.Keys.Where(r => !Sketches.Any(s => ReferenceEquals(s.Result, r))).ToList()) _sketchPieces.Remove(r);
    }

    /// <summary>Selected/hovered vertices (a small cross) and sketch lines/arcs (re-drawn in the highlight colour).</summary>
    void DrawPointAndSegmentHighlights(DynamicGeometry g, Highlights hl)
    {
        var px = Camera.PixelSize;
        foreach (var (it, c) in hl.Items)
        {
            if (it.Kind == SelKind.Vertex)
            {
                var r = px * 6;
                g.Line(it.Point - Vec3.UnitX * r, it.Point + Vec3.UnitX * r, c, top: true);
                g.Line(it.Point - Vec3.UnitY * r, it.Point + Vec3.UnitY * r, c, top: true);
                g.Line(it.Point - Vec3.UnitZ * r, it.Point + Vec3.UnitZ * r, c, top: true);
            }
            else if (it.Kind == SelKind.Axis)
                DrawAxis(g, it.Point - it.Normal * it.AxisHalfLength, it.Point + it.Normal * it.AxisHalfLength, c);
            else if (it.Kind == SelKind.SketchSegment && Sketches.FirstOrDefault(s => s.FeatureId == it.FeatureId) is { } sd
                     && sd.Result.Sketch.Entity(it.SketchItem) is { } ent)
                foreach (var (a2, b2) in SketchDrawing.Segments2D(sd.Result.Sketch, ent))
                    g.Line(sd.Result.Frame.ToWorld(a2), sd.Result.Frame.ToWorld(b2), c, top: true);
        }
    }

    /// <summary>A temporary axis: dash-dot like a centerline, over the model, with a short stroke across each end.</summary>
    public void DrawAxis(DynamicGeometry g, Vec3 a, Vec3 b, Rgba c)
    {
        var px = Camera.PixelSize;
        var len = Vec3.Distance(a, b);
        if (len < 1e-12) return;
        var d = (b - a) / len;
        double dash = px * 12, gap = px * 3, dot = px * 2;
        if (len / (dash + dot + 2 * gap) > 2000) g.Line(a, b, c, top: true);
        else
            for (double t = 0; t < len; t += dash + dot + 2 * gap)
            {
                g.Line(a + d * t, a + d * Math.Min(t + dash, len), c, top: true);
                var t2 = t + dash + gap;
                if (t2 < len) g.Line(a + d * t2, a + d * Math.Min(t2 + dot, len), c, top: true);
            }
        // the ends: a stroke across, in the screen plane
        var n = Vec3.Cross(Camera.V(Camera.Back), d);
        if (n.Length < 1e-9) return;
        n = n.Normalized * (px * 5);
        g.Line(a - n, a + n, c, top: true); g.Line(b - n, b + n, c, top: true);
    }

    /// <summary>An axis through the origin to show (e.g. while an X/Y/Z axis button is hovered), or null.</summary>
    public (Vec3 dir, Rgba color)? AxisPreview { get => _axisPreview; set { _axisPreview = value; Redraw(); } }

    /// <summary>
    /// The center of mass to show (Mass Properties): the point, and the principal axes of inertia through it (each
    /// drawn <c>length</c> to either side). Null for none.
    /// </summary>
    public (Vec3 center, Vec3[] axes, double length)? CenterOfMass
    {
        get => _centerOfMass;
        set { _centerOfMass = value; Redraw(); }
    }
    (Vec3 center, Vec3[] axes, double length)? _centerOfMass;

    /// <summary>
    /// The center of mass symbol (as SolidWorks: a circle in black and white quarters, facing the viewer, over the
    /// model) and the principal axes, dashed.
    /// </summary>
    void DrawCenterOfMass(DynamicGeometry g, Vec3 c, Vec3[] axes, double length)
    {
        var px = Camera.PixelSize;
        var axisColor = new Rgba(0.30f, 0.30f, 0.75f);
        foreach (var a in axes) g.Dashed(c - a * length, c + a * length, axisColor, px * 6, top: true);
        var right = Camera.V(Camera.Right); var up = Camera.V(Camera.Up);
        const double r = 9; // pixels
        Vec3 At(double ang, double rad) => c + (right * Math.Cos(ang) + up * Math.Sin(ang)) * (rad * px);
        var dark = new Rgba(0.08f, 0.08f, 0.10f); var light = new Rgba(1, 1, 1);
        const int n = 8; // triangles per quarter
        for (int q = 0; q < 4; q++)
            for (int i = 0; i < n; i++)
            {
                var a0 = (q + (double)i / n) * Math.PI / 2; var a1 = (q + (double)(i + 1) / n) * Math.PI / 2;
                g.Tri(c, At(a0, r), At(a1, r), q % 2 == 0 ? dark : light, top: true);
            }
        for (int i = 0; i < 4 * n; i++)
            g.Line(At(i * Math.PI / (2 * n), r), At((i + 1) * Math.PI / (2 * n), r), dark, top: true);
    }

    /// <summary>A line of a flattened sheet metal part.</summary>
    public enum BendLineKind { BendUp, BendDown, Tangent }

    /// <summary>A bend line on the flat's unfolded face (a, b), the face's outward normal, the sheet thickness and the bend's width in the flat.</summary>
    public readonly record struct BendLine(Vec3 A, Vec3 B, BendLineKind Kind, Vec3 Normal, double Thickness, double Width);

    /// <summary>
    /// The bend lines (centerlines: up bends green, down bends red, dash-dot) and bend tangent lines (gray, dashed) of
    /// the part shown flat. Drawn on the face of the sheet toward the viewer, a few pixels off it (whatever the zoom,
    /// the face does not hide them); tangent lines only where the bend is wide enough on screen to tell them apart.
    /// </summary>
    public IReadOnlyList<BendLine> BendLines
    {
        get => _bendLines;
        set { _bendLines = value; Redraw(); }
    }
    IReadOnlyList<BendLine> _bendLines = Array.Empty<BendLine>();

    /// <summary>Cosmetic threads: drawn as a helix on their face.</summary>
    public IReadOnlyList<Core.Regen.CosmeticThread> CosmeticThreads
    {
        get => _cosmetic;
        set { _cosmetic = value; Redraw(); }
    }
    IReadOnlyList<Core.Regen.CosmeticThread> _cosmetic = Array.Empty<Core.Regen.CosmeticThread>();

    readonly Dictionary<SketchResult, (Rgba color, double px, DynamicGeometry.Piece piece)> _sketchPieces = new(ReferenceEqualityComparer.Instance);
    (IReadOnlyList<Core.Regen.CosmeticThread> threads, double px, DynamicGeometry.Piece piece)? _threadPiece;

    void DrawCosmeticThreads(DynamicGeometry g)
    {
        // kept while the threads and the pixel size (zoom) stay: orbiting and panning draw the helices again as they are
        var px = Camera.PixelSize;
        if (_threadPiece is { } kept && ReferenceEquals(kept.threads, _cosmetic) && kept.px == px) { g.Add(kept.piece); return; }
        var m = g.Mark();
        DrawCosmeticThreads(g, px);
        _threadPiece = (_cosmetic, px, g.Since(m));
    }

    void DrawCosmeticThreads(DynamicGeometry g, double px)
    {
        var c = new Rgba(0.30f, 0.36f, 0.48f);
        foreach (var th in _cosmetic)
        {
            // lifted 2 pixels off the face toward where it is seen from (out of a shaft, in from a hole's wall)
            var r = th.Radius + (th.External ? px * 2 : -px * 2);
            if (r <= 0 || th.Lead == 0) continue;
            // along the axis into the face, turning counterclockwise about it (right hand) or clockwise (left hand)
            var ax = th.Axis.Normalized; var x = th.RefDir.Normalized; var y = Vec3.Cross(ax, x);
            var turns = th.Length / Math.Abs(th.Lead);
            int n = (int)Math.Clamp(Math.Ceiling(turns * 48), 2, 40000);
            Vec3 At(double s) { var a = Math.Sign(th.Lead) * 2 * Math.PI * turns * s; return th.Origin + ax * (th.Length * s) + (x * Math.Cos(a) + y * Math.Sin(a)) * r; }
            var prev = At(0);
            for (int i = 1; i <= n; i++)
            {
                var p = At((double)i / n);
                g.Line(prev, p, c);
                prev = p;
            }
        }
    }

    void DrawBendLines(DynamicGeometry g)
    {
        var px = Camera.PixelSize;
        var toEye = Camera.V(Camera.Back);
        foreach (var l in _bendLines)
        {
            if (l.Kind == BendLineKind.Tangent && l.Width < px * 8) continue; // would crowd the centerline
            // on the face toward the viewer, lifted toward the viewer by 2 pixels
            var facing = Vec3.Dot(toEye, l.Normal) >= 0;
            var shift = facing ? l.Normal * (px * 2) : -l.Normal * (l.Thickness + px * 2);
            Vec3 a = l.A + shift, b = l.B + shift;
            var kind = l.Kind;
            if (kind == BendLineKind.Tangent) { g.Dashed(a, b, new Rgba(0.45f, 0.47f, 0.52f), px * 4); continue; }
            var c = kind == BendLineKind.BendUp ? new Rgba(0.05f, 0.55f, 0.20f) : new Rgba(0.80f, 0.12f, 0.12f);
            // dash-dot, like a centerline
            var len = Vec3.Distance(a, b);
            if (len < 1e-12) continue;
            var d = (b - a) / len;
            double dash = px * 14, gap = px * 4, dot = px * 2;
            if (len / (dash + dot + 2 * gap) > 2000) { g.Line(a, b, c); continue; }
            for (double t = 0; t < len; t += dash + dot + 2 * gap)
            {
                g.Line(a + d * t, a + d * Math.Min(t + dash, len), c);
                var t2 = t + dash + gap;
                if (t2 < len) g.Line(a + d * t2, a + d * Math.Min(t2 + dot, len), c);
            }
        }
    }

    /// <summary>How a Measure line is drawn.</summary>
    public enum MeasureStyle
    {
        /// <summary>A distance between two points: a square at each end.</summary>
        Distance,
        /// <summary>A secondary distance (center to center behind a minimum distance): dashed, squares at the ends.</summary>
        Dashed,
        /// <summary>A radius from a center (a): a center mark there, an arrow where it meets the item (b).</summary>
        Radius,
        /// <summary>A point (a; b unused): a square.</summary>
        Point,
    }

    /// <summary>The Measure tool's lines and marks, drawn over the model.</summary>
    public IReadOnlyList<(Vec3 a, Vec3 b, MeasureStyle style)> MeasureLines
    {
        get => _measureLines;
        set { _measureLines = value; Redraw(); }
    }
    IReadOnlyList<(Vec3 a, Vec3 b, MeasureStyle style)> _measureLines = Array.Empty<(Vec3, Vec3, MeasureStyle)>();
    public static readonly Rgba MeasureColor = new(0.80f, 0.10f, 0.55f);

    /// <summary>
    /// A hovered model vertex or circle center (faces and edges show by their color; a point needs a mark): a
    /// square, and for a center also a cross through it.
    /// </summary>
    void DrawPointHover(DynamicGeometry g, SelItem h)
    {
        var px = Camera.PixelSize;
        var r = Camera.V(Camera.Right) * px; var u = Camera.V(Camera.Up) * px;
        var p = h.Point;
        g.Quad(p - r * 4 - u * 4, p + r * 4 - u * 4, p + r * 4 + u * 4, p - r * 4 + u * 4, HoverColor, top: true);
        if (h.IsCenter) { g.Line(p - r * 11, p + r * 11, HoverColor, top: true); g.Line(p - u * 11, p + u * 11, HoverColor, top: true); }
    }

    void DrawMeasureLines(DynamicGeometry g)
    {
        var px = Camera.PixelSize;
        var right = Camera.V(Camera.Right); var up = Camera.V(Camera.Up);
        var c = MeasureColor;
        void Square(Vec3 p) { var r = right * (px * 3); var u = up * (px * 3); g.Quad(p - r - u, p + r - u, p + r + u, p - r + u, c, top: true); }
        foreach (var (a, b, style) in _measureLines)
        {
            switch (style)
            {
                case MeasureStyle.Point: Square(a); break;
                case MeasureStyle.Distance: g.Line(a, b, c, top: true); Square(a); Square(b); break;
                case MeasureStyle.Dashed: g.Dashed(a, b, c, px * 6, top: true); Square(a); Square(b); break;
                case MeasureStyle.Radius:
                    {
                        g.Line(a, b, c, top: true);
                        // center mark: a small screen-facing cross
                        g.Line(a - right * (px * 6), a + right * (px * 6), c, top: true);
                        g.Line(a - up * (px * 6), a + up * (px * 6), c, top: true);
                        // arrow at the item, pointing at it (drawn in the screen plane)
                        var d = b - a; d -= Camera.V(Camera.Back) * Vec3.Dot(d, Camera.V(Camera.Back));
                        if (d.Length < 1e-12) break;
                        d = d.Normalized;
                        var n = Vec3.Cross(Camera.V(Camera.Back), d);
                        g.Tri(b, b - d * (px * 11) + n * (px * 4), b - d * (px * 11) - n * (px * 4), c, top: true);
                        break;
                    }
            }
        }
    }
    (Vec3 dir, Rgba color)? _axisPreview;

    /// <summary>
    /// The axis line through the origin, as long as the model is along it, clipped to what is on screen (so the
    /// arrow at its positive end is always in view); nothing when the axis does not cross the view.
    /// </summary>
    void DrawAxisPreview(DynamicGeometry g, Vec3 dir, Rgba color)
    {
        // along the axis: as far as the model reaches (its box corners), and a little more
        var b = SceneBounds();
        double len = 50;
        if (!b.IsEmpty)
            for (int i = 0; i < 8; i++)
            {
                var c = new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                len = Math.Max(len, Math.Abs(Vec3.Dot(c, dir)) * 1.1);
            }
        // the part of [-len, len] on screen (20 px in from the edges): sampled, then each end refined
        const double margin = 20;
        bool OnScreen(double t)
        {
            if (Camera.Perspective && Camera.DepthOf(dir * t) <= 0) return false; // behind the eye
            var s = Camera.Project(dir * t);
            return s.Z > -1 && s.Z < 1 && s.X >= margin && s.X <= Camera.Width - margin && s.Y >= margin && s.Y <= Camera.Height - margin;
        }
        const int samples = 64;
        int first = -1, last = -1;
        for (int i = 0; i <= samples; i++)
            if (OnScreen(-len + 2 * len * i / samples)) { if (first < 0) first = i; last = i; }
        if (first < 0) return;
        double Refine(double inside, double outside)
        {
            for (int k = 0; k < 20; k++) { var m = (inside + outside) / 2; if (OnScreen(m)) inside = m; else outside = m; }
            return inside;
        }
        double T(int i) => -len + 2 * len * i / samples;
        var t0 = first > 0 ? Refine(T(first), T(first - 1)) : T(first);
        var t1 = last < samples ? Refine(T(last), T(last + 1)) : T(last);
        var tip = dir * t1;
        g.Line(dir * t0, tip, color, top: true);
        var s = Camera.PixelSize * 12;
        var n = Vec3.Cross(dir, Camera.V(Camera.Back));
        n = n.Length > 1e-9 ? n.Normalized : dir.AnyPerpendicular();
        g.Tri(tip, tip - dir * s + n * (s * 0.4), tip - dir * s - n * (s * 0.4), color, top: true);
    }

    /// <summary>The rotation center: a small screen-facing circle with a dot, on top of the model.</summary>
    void DrawPivot(DynamicGeometry g, Vector3 at)
    {
        var c = Camera.V(at); var r = Camera.PixelSize * 7;
        var right = Camera.V(Camera.Right); var up = Camera.V(Camera.Up);
        var color = new Rgba(0.85f, 0.35f, 0.05f);
        Vec3 P(double a, double s) => c + (right * Math.Cos(a) + up * Math.Sin(a)) * (r * s);
        for (int i = 0; i < 24; i++)
            g.Line(P(i * Math.PI / 12, 1), P((i + 1) * Math.PI / 12, 1), color, top: true);
        g.Line(c - right * (r * 0.3), c + right * (r * 0.3), color, top: true);
        g.Line(c - up * (r * 0.3), c + up * (r * 0.3), color, top: true);
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
        if (OverlayProfile)
        {
            var t0 = Stopwatch.GetTimestamp();
            Mode?.UpdateOverlay(this);
            NoteOverlay(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        else Mode?.UpdateOverlay(this);
        CameraChanged?.Invoke();
    }

    // SPRYCAD_PROFILE_OVERLAY: the overlay updates (count, average and worst time) written to the log every 2 s
    static readonly bool OverlayProfile = Environment.GetEnvironmentVariable("SPRYCAD_PROFILE_OVERLAY") != null;
    int _ovCalls; double _ovMs, _ovMax; long _ovSince = Stopwatch.GetTimestamp();

    void NoteOverlay(double ms)
    {
        _ovCalls++; _ovMs += ms; _ovMax = Math.Max(_ovMax, ms);
        var span = Stopwatch.GetElapsedTime(_ovSince).TotalSeconds;
        if (span < 2) return;
        App.Note($"overlay: {_ovCalls} updates in {span:0.0} s ({_ovCalls / span:0} a second), {_ovMs / _ovCalls:0.00} ms average, {_ovMax:0.00} ms worst ({Mode?.GetType().Name})");
        _ovCalls = 0; _ovMs = 0; _ovMax = 0; _ovSince = Stopwatch.GetTimestamp();
    }

    public void AnimateTo(Quaternion q, Vector3? target = null, float? halfHeight = null)
    {
        _animFromQ = Camera.Rotation; _animToQ = q;
        _animFromT = Camera.Target; _animToT = target ?? Camera.Target;
        _animFromH = Camera.HalfHeight; _animToH = halfHeight ?? Camera.HalfHeight;
        _animClock.Restart(); _animating = true;
        RequestNextFrameRendering();
    }

    // continuous spin (an arrow key held down): degrees per second about the view's up and right axes
    float _spinYaw, _spinPitch;
    Vector3 _spinPivot;
    readonly Stopwatch _spinClock = new();
    bool Spinning => _spinYaw != 0 || _spinPitch != 0;

    /// <summary>
    /// Spins the view steadily (degrees per second, signs as <see cref="Rotate"/>) about the rotation center
    /// until set back to zero; frame-timed, so it stays smooth whatever the key repeat rate.
    /// </summary>
    public void SetSpin(float yawPerSec, float pitchPerSec)
    {
        if (!Spinning && (yawPerSec != 0 || pitchPerSec != 0))
        {
            _spinPivot = ComputePivot();
            _spinClock.Restart();
            _animating = false; // takes over from a step still animating
        }
        _spinYaw = yawPerSec; _spinPitch = pitchPerSec;
        RequestNextFrameRendering();
    }

    void StepSpin()
    {
        if (!Spinning) return;
        var dt = (float)_spinClock.Elapsed.TotalSeconds;
        _spinClock.Restart();
        dt = MathF.Min(dt, 0.1f); // a stalled frame must not jump
        var qy = Quaternion.CreateFromAxisAngle(Camera.Up, _spinYaw * dt * MathF.PI / 180);
        var qx = Quaternion.CreateFromAxisAngle(Camera.Right, _spinPitch * dt * MathF.PI / 180);
        var q = Quaternion.Normalize(qy * qx);
        Camera.Target = _spinPivot + Vector3.Transform(Camera.Target - _spinPivot, q);
        Camera.Rotation = Quaternion.Normalize(q * Camera.Rotation);
        Dispatcher.UIThread.Post(() => { Mode?.UpdateOverlay(this); CameraChanged?.Invoke(); });
    }

    void StepAnimation()
    {
        StepSpin();
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
        foreach (var i in Instances) b.Add(Placed(i.Mesh.Bounds, i.Model));
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

    /// <summary>Turns to a named standard view, zoomed to fit (as SolidWorks' standard views).</summary>
    public void StandardView(string name, bool fit = true, bool animate = true) => OrientTo(Camera.StandardViews[name], fit, animate);

    /// <summary>Turns the view to an orientation, optionally zooming to fit the model as seen from there.</summary>
    void OrientTo(Quaternion q, bool fit, bool animate = true)
    {
        Vector3? target = null; float? halfHeight = null;
        if (fit)
        {
            var tmp = new Camera { Width = Camera.Width, Height = Camera.Height, Rotation = q };
            var b = SceneBounds();
            if (b.IsEmpty) { b.Add(new Vec3(-50, -50, -50)); b.Add(new Vec3(50, 50, 50)); }
            tmp.Fit(b);
            (target, halfHeight) = (tmp.Target, tmp.HalfHeight);
        }
        if (animate) { AnimateTo(q, target, halfHeight); return; }
        // at once (e.g. a part just switched to): no animation from the previous part's view
        _animating = false;
        Camera.Rotation = q;
        if (target is { } t) Camera.Target = t;
        if (halfHeight is { } h) Camera.HalfHeight = h;
        Redraw();
    }

    /// <summary>
    /// The view as it is meant to be: where a running animation is going (the camera is still on its way), else the
    /// camera's. Stops the animation (the view is about to be replaced, e.g. by another part's).
    /// </summary>
    public (Quaternion rotation, Vector3 target, float halfHeight) SettleView()
    {
        if (_animating) { _animating = false; return (_animToQ, _animToT, _animToH); }
        return (Camera.Rotation, Camera.Target, Camera.HalfHeight);
    }

    /// <summary>
    /// Looks at the model from a world direction (a View Selector face, edge or corner), keeping +Y up; straight
    /// down or up, the top/bottom views' up (-Z / +Z) is used.
    /// </summary>
    public void ViewFrom(Vector3 dir, bool fit = false)
    {
        dir = Vector3.Normalize(dir);
        var up = MathF.Abs(dir.Y) > 0.999f ? (dir.Y > 0 ? -Vector3.UnitZ : Vector3.UnitZ) : Vector3.UnitY;
        OrientTo(Camera.LookRotation(dir, up), fit);
    }

    /// <summary>The camera Normal To a frame ends at, fitted to a box (nothing moves).</summary>
    public Camera NormalToCamera(Frame f, Box3 box)
    {
        var cam = new Camera
        {
            Width = Camera.Width, Height = Camera.Height, Perspective = Camera.Perspective, Fov = Camera.Fov, SceneRadius = Camera.SceneRadius,
            Rotation = Camera.LookRotation(Camera.F(f.Normal), Camera.F(f.YDir)),
        };
        cam.Fit(box);
        return cam;
    }

    /// <summary>
    /// Look straight at a frame (Normal To). With <paramref name="fit"/>, also zoomed to fit that box as seen from
    /// there (e.g. the sketch being edited), in the same animation.
    /// </summary>
    public void NormalTo(Frame f, Box3? fit = null)
    {
        var q = Camera.LookRotation(Camera.F(f.Normal), Camera.F(f.YDir));
        if (fit is { IsEmpty: false } box)
        {
            var tmp = NormalToCamera(f, box);
            AnimateTo(q, tmp.Target, tmp.HalfHeight);
            return;
        }
        // keep the target in the plane's vicinity
        var t = Camera.Target;
        var tv = Camera.V(t);
        var proj = f.Plane.Project(tv);
        AnimateTo(q, Camera.F(proj));
    }

    public void Rotate(float yawDeg, float pitchDeg)
    {
        var pivot = ComputePivot();
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
            // middle double-click: zoom to fit (SolidWorks)
            if (e.ClickCount == 2) { ZoomToFit(); e.Handled = true; return; }
            _zoomAnchor = pt.Position;
            _nav = (e.KeyModifiers & KeyModifiers.Control) != 0 ? Nav.Pan
                 : (e.KeyModifiers & KeyModifiers.Shift) != 0 ? Nav.Zoom
                 : (e.KeyModifiers & KeyModifiers.Alt) != 0 ? Nav.Roll
                 : Nav.Rotate;
            _pivot = ComputePivot(pt.Position);
            _middleDown = pt.Position; _middleDragged = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed)
        {
            _rightDown = pt.Position; _navLast = pt.Position; _rightDragged = false;
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

    Point _zoomAnchor;

    /// <summary>
    /// Rotation center: one set by middle-clicking the model; otherwise per <see cref="RotateAbout"/> the selected
    /// vertex (model corner or sketch point), edge (its middle) or face (the spot it was picked), or the model under
    /// <paramref name="cursor"/>; failing those, the middle of the model.
    /// </summary>
    Vector3 ComputePivot(Point? cursor = null)
    {
        if (_pinnedPivot is { } pin) return pin;
        if (RotateAbout == RotateCenter.Selection &&
            Selection?.Items.LastOrDefault(s => s.Kind is SelKind.Vertex or SelKind.Edge or SelKind.Face) is { } sel)
            return Camera.F(sel.Point);
        if (RotateAbout == RotateCenter.UnderCursor && cursor is { } c && ModelPointAt(c) is { } hit)
            return hit;
        var b = SceneBounds();
        return b.IsEmpty ? Camera.Target : Camera.F(b.Center);
    }

    /// <summary>The model point under a screen point: a vertex near it, else where the ray meets a face or edge.</summary>
    Vector3? ModelPointAt(Point p)
    {
        if (PickVertex(p.X, p.Y) is { } v) return Camera.F(v.point);
        if (Pick(p.X, p.Y, SelFilter.Face) is { } f) return Camera.F(f.Point);
        if (Pick(p.X, p.Y, SelFilter.Edge) is { } e) return Camera.F(e.Point);
        return null;
    }

    /// <summary>Forgets a rotation center set by middle-clicking (e.g. when another part is opened).</summary>
    public void ClearPivot() { _pinnedPivot = null; Redraw(); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        var dx = (float)(pos.X - _navLast.X); var dy = (float)(pos.Y - _navLast.Y);
        if (pt.Properties.IsRightButtonPressed && _nav == Nav.None)
        {
            if (!_rightDragged && Math.Abs(pos.X - _rightDown.X) + Math.Abs(pos.Y - _rightDown.Y) > 4)
            {
                _rightDragged = true;
                GestureBegan?.Invoke(_rightDown);
            }
            if (!_rightDragged) return;
            if (GestureBegan != null) GestureMoved?.Invoke(pos);
            else { Camera.Pan(dx, dy); Redraw(); }
            _navLast = pos;
            return;
        }
        if (_nav != Nav.None)
        {
            _animating = false;
            if (!_middleDragged && Math.Abs(pos.X - _middleDown.X) + Math.Abs(pos.Y - _middleDown.Y) > 3) _middleDragged = true;
            switch (_nav)
            {
                case Nav.Rotate: Camera.Orbit(dx, dy, _pivot); break;
                case Nav.Pan: Camera.Pan(dx, dy); break;
                case Nav.Zoom: Camera.ZoomAt(MathF.Exp(dy * 0.01f), (float)_zoomAnchor.X, (float)_zoomAnchor.Y); break; // about the press point
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
        if (_nav != Nav.None)
        {
            // a plain middle click (no drag) sets the rotation center on the model, or clears it on empty space
            if (e.InitialPressMouseButton == MouseButton.Middle && _nav == Nav.Rotate && !_middleDragged)
                _pinnedPivot = ModelPointAt(e.GetPosition(this));
            _nav = Nav.None; e.Pointer.Capture(null);
            Redraw(); // the rotation marker goes away (or moves to the new center)
            return;
        }
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            e.Pointer.Capture(null);
            if (!_rightDragged) ContextRequested?.Invoke(Info(e));
            else GestureEnded?.Invoke(e.GetPosition(this));
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

    /// <summary>
    /// What is under the cursor that <paramref name="filter"/> accepts. A box asking for axes
    /// (<see cref="SelFilter.Axis"/>) gets a round face's or a circular edge's axis (as SolidWorks' temporary axes)
    /// where it would get the face or edge.
    /// </summary>
    public SelItem? Pick(double x, double y, SelFilter filter)
    {
        if ((filter & SelFilter.Axis) == 0) return PickItem(x, y, filter);
        var h = PickItem(x, y, filter | SelFilter.Face | SelFilter.Edge);
        if (h != null && AxisOf(h) is { } ax) return ax;
        // (faces and edges looked at only for their axes)
        if (h is { Kind: SelKind.Face } && (filter & (SelFilter.Face | SelFilter.PlanarFace)) == 0) return null;
        if (h is { Kind: SelKind.Face, Planar: false } && (filter & SelFilter.Face) == 0) return null;
        if (h is { Kind: SelKind.Edge } && (filter & SelFilter.Edge) == 0) return null;
        return h;
    }

    /// <summary>The axis of a picked round face (cylinder, cone) or circular edge, as an Axis item; null for anything else.</summary>
    public SelItem? AxisOf(SelItem h)
    {
        if (Bodies.FirstOrDefault(b => b.Id == h.BodyId) is not { } body) return null;
        if (h.Kind == SelKind.Face && h.Name != null) return FaceAxis(body, h.Name);
        if (h.Kind == SelKind.Edge && h.EdgeCircular && h.Edge != null) return EdgeAxis(body, h.Edge);
        return null;
    }

    /// <summary>
    /// The center of a circle a pick is on: a circular edge's, or of a round face's circular edge nearest where it was
    /// picked (a hole's rim). Null when there is none.
    /// </summary>
    public SelItem? CenterOf(SelItem h)
    {
        if (Bodies.FirstOrDefault(b => b.Id == h.BodyId) is not { Solid.Native: { } nb } body) return null;
        nb.Link();
        Kernel.Native.NEdge? best = null; double bestD = double.MaxValue;
        foreach (var e in nb.Edges)
        {
            if (e.Curve is not Kernel.Native.NCircle || e.Uses.Count != 2) continue;
            string fa = e.Uses[0].Face.Tag.Name, fb = e.Uses[1].Face.Tag.Name;
            bool on = h.Kind switch
            {
                SelKind.Edge => h.Edge != null && ((fa == h.Edge.A && fb == h.Edge.B) || (fa == h.Edge.B && fb == h.Edge.A)),
                SelKind.Face => fa == h.Name || fb == h.Name,
                _ => false,
            };
            if (!on) continue;
            var d = e.Samples(1e-3).Min(p => Vec3.Distance(p, h.Point));
            if (d < bestD) { bestD = d; best = e; }
        }
        if (best?.Curve is not Kernel.Native.NCircle c) return null;
        var er = new Core.Model.EdgeRef { A = best.Uses[0].Face.Tag.Name, B = best.Uses[1].Face.Tag.Name, Body = body.Id, Hint = best.Mid };
        return new SelItem { Kind = SelKind.Vertex, BodyId = body.Id, Point = c.C, IsCenter = true, CenterRadius = c.R, Edge = er };
    }

    /// <summary>A round face's axis (cylinders and cones), drawn along the face and a little past it.</summary>
    public static SelItem? FaceAxis(Body body, string face)
    {
        var m = body.Mesh;
        foreach (var fr in m.Faces)
        {
            if (fr.Name != face || fr.Surface.Kind is not (SurfaceKind.Cylinder or SurfaceKind.Cone)) continue;
            var o = fr.Surface.Origin; var d = fr.Surface.Axis.Normalized;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int k = fr.IndexStart; k < fr.IndexStart + fr.IndexCount; k++)
            {
                var i = m.Indices[k];
                var t = Vec3.Dot(new Vec3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]) - o, d);
                lo = Math.Min(lo, t); hi = Math.Max(hi, t);
            }
            if (lo > hi) return null;
            var half = (hi - lo) / 2;
            return new SelItem
            {
                Kind = SelKind.Axis, BodyId = body.Id, Name = face, Point = o + d * ((lo + hi) / 2), Normal = d,
                AxisHalfLength = half + Math.Max(2, half * 0.15),
            };
        }
        return null;
    }

    /// <summary>A circular edge's axis (through its center, square to it), drawn about as long as the circle is wide.</summary>
    public static SelItem? EdgeAxis(Body body, Core.Model.EdgeRef edge)
    {
        if (body.Solid.Native is not { } nb) return null;
        nb.Link();
        foreach (var e in nb.Edges)
        {
            if (e.Curve is not Kernel.Native.NCircle c || e.Uses.Count != 2) continue;
            var fa = e.Uses[0].Face.Tag.Name; var fb = e.Uses[1].Face.Tag.Name;
            if (!((fa == edge.A && fb == edge.B) || (fa == edge.B && fb == edge.A))) continue;
            return new SelItem { Kind = SelKind.Axis, BodyId = body.Id, Edge = edge.Clone(), Point = c.C, Normal = c.Axis.Normalized, AxisHalfLength = c.R * 1.3 };
        }
        return null;
    }

    SelItem? PickItem(double x, double y, SelFilter filter)
    {
        var (ro, rd) = Camera.Ray(x, y);
        double bestT = double.MaxValue; SelItem? best = null;
        const double edgePx = 6, sketchPx = 6;

        // faces (memoized: hover, vertex and edge picks at the same cursor share the ray test)
        var faceKey = (ro, rd, Bodies, Style);
        if (_faceMemo is { } fm && fm.key.Equals(faceKey)) { best = fm.hit; bestT = fm.t; }
        else if (Style != DisplayStyle.Wireframe)
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
        _faceMemo = (faceKey, best, bestT);
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

        // vertices: model vertices and sketch points win when a box asks for points
        if ((filter & (SelFilter.Vertex | SelFilter.Center)) != 0)
        {
            SelItem? bestV = null; double bestVPx = 9;
            // circle and arc centers (the Measure tool): a model vertex as near wins
            if ((filter & SelFilter.Center) != 0)
                foreach (var b in Bodies)
                    foreach (var (c, r, ce) in CircleCenters(b))
                    {
                        var sc = Camera.Project(c);
                        if (Camera.Perspective && Camera.DepthOf(c) <= 0) continue;
                        var d = Math.Sqrt((sc.X - x) * (sc.X - x) + (sc.Y - y) * (sc.Y - y));
                        if (d < bestVPx - 0.5) { bestVPx = d; bestV = new SelItem { Kind = SelKind.Vertex, BodyId = b.Id, Point = c, IsCenter = true, CenterRadius = r, Edge = ce }; }
                    }
            if ((filter & SelFilter.Vertex) == 0) { if (bestV != null) return bestV; }
            else if (PickVertex(x, y) is { } mv && ScreenPx(mv.point, x, y) <= bestVPx + 0.5)
            {
                var s0 = Camera.Project(mv.point);
                bestVPx = Math.Sqrt((s0.X - x) * (s0.X - x) + (s0.Y - y) * (s0.Y - y));
                bestV = new SelItem
                {
                    Kind = SelKind.Vertex, BodyId = mv.body.Id, Point = mv.point,
                    Edge = new Core.Model.EdgeRef { A = mv.edge.FaceA, B = mv.edge.FaceB, Body = mv.body.Id, Hint = mv.point },
                };
            }
            if ((filter & SelFilter.Vertex) != 0)
            foreach (var s in Sketches)
                foreach (var p in s.Result.Sketch.Points)
                {
                    var w = s.Result.Frame.ToWorld(p.P);
                    var sp = Camera.Project(w);
                    var d = Math.Sqrt((sp.X - x) * (sp.X - x) + (sp.Y - y) * (sp.Y - y));
                    if (d < bestVPx) { bestVPx = d; bestV = new SelItem { Kind = SelKind.Vertex, FeatureId = s.FeatureId, SketchItem = p.Id, Point = w }; }
                }
            if (bestV != null) return bestV;
        }
        if ((filter & SelFilter.SketchSegment) != 0)
        {
            double bestPx = sketchPx; SelItem? bestSeg = null;
            foreach (var s in Sketches)
                foreach (var e in s.Result.Sketch.Entities)
                {
                    if (e is not (Core.Sketching.SkLine or Core.Sketching.SkArc or Core.Sketching.SkCircle or Core.Sketching.SkSpline)) continue;
                    foreach (var (a2, b2) in SketchDrawing.Segments2D(s.Result.Sketch, e))
                    {
                        var (d, wp) = ScreenDistance(s.Result.Frame.ToWorld(a2), s.Result.Frame.ToWorld(b2), x, y);
                        if (d < bestPx) { bestPx = d; bestSeg = new SelItem { Kind = SelKind.SketchSegment, FeatureId = s.FeatureId, SketchItem = e.Id, Point = wp }; }
                    }
                }
            if (bestSeg != null) return bestSeg;
        }

        // edges (screen distance), must be visible
        if ((filter & SelFilter.Edge) != 0)
        {
            double bestPx = edgePx; SelItem? bestEdge = null;
            foreach (var b in Bodies)
            {
                var m = b.Mesh;
                var topo = b.Solid.Topology;
                var boxes = EdgeBoxes(m);
                for (int k = 0; k < m.Edges.Count; k++)
                {
                    var er = m.Edges[k];
                    // skip edges whose projected bounds are farther than the best distance so far
                    if (ScreenRectDistance(boxes[k], x, y) >= bestPx) continue;
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

    /// <summary>Visible model vertex (edge end point) under the cursor.</summary>
    double ScreenPx(Vec3 p, double x, double y) { var s = Camera.Project(p); return Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y)); }

    // circle and arc centers of each body (from its exact edges), worked out once per body
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Body, List<(Vec3 center, double radius, Core.Model.EdgeRef? edge)>> _centers = new();

    /// <summary>The distinct centers of a body's circular edges (circles and arcs), with their radii.</summary>
    // (each with the edge it is the center of, by its faces: a point reference to it follows the model)
    static List<(Vec3 center, double radius, Core.Model.EdgeRef? edge)> CircleCenters(Body b) => _centers.GetValue(b, body =>
    {
        var list = new List<(Vec3, double, Core.Model.EdgeRef?)>();
        if (body.Solid.Native is not { } nb) return list;
        nb.Link();
        foreach (var e in nb.Edges)
            if (e.Curve is Kernel.Native.NCircle c && !list.Any(x => Vec3.Distance(x.Item1, c.C) < 1e-6 && Math.Abs(x.Item2 - c.R) < 1e-6))
                list.Add((c.C, c.R, e.Uses.Count == 2 ? new Core.Model.EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Body = body.Id, Hint = e.Mid } : null));
        return list;
    });

    public (Body body, TopoEdge edge, Vec3 point)? PickVertex(double x, double y, double radiusPx = 8)
    {
        var face = Pick(x, y, SelFilter.Face);
        var (ro, rd) = Camera.Ray(x, y);
        double faceDepth = face != null ? Vec3.Dot(face.Point - ro, rd) : double.MaxValue;
        (Body, TopoEdge, Vec3)? best = null; double bestPx = radiusPx;
        foreach (var b in Bodies)
            foreach (var e in b.Solid.Topology.Edges)
            {
                Try(b, e, e.Start);
                if (!e.Closed) Try(b, e, e.End);
            }
        return best;

        void Try(Body b, TopoEdge e, Vec3 p)
        {
            var s = Camera.Project(p);
            var d = Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y));
            if (d >= bestPx) return;
            if (Vec3.Dot(p - ro, rd) > faceDepth + Camera.PixelSize * 3 + 1e-4) return;
            bestPx = d; best = (b, e, p);
        }
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
    ((Vec3, Vec3, IReadOnlyList<Body>, DisplayStyle) key, SelItem? hit, double t)? _faceMemo;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DisplayMesh, Box3[]> _edgeBoxes = new();

    /// <summary>3D bounds of each display edge (cached per mesh).</summary>
    static Box3[] EdgeBoxes(DisplayMesh m) => _edgeBoxes.GetValue(m, mm =>
    {
        var res = new Box3[mm.Edges.Count];
        for (int k = 0; k < mm.Edges.Count; k++)
        {
            var b = Box3.Empty;
            var er = mm.Edges[k];
            for (int i = er.VertexStart; i < er.VertexStart + er.VertexCount; i++) b.Add(E(mm, i));
            res[k] = b;
        }
        return res;
    });

    /// <summary>Screen distance from (x, y) to the projected bounding rectangle of a 3D box (0 inside).</summary>
    double ScreenRectDistance(Box3 b, double x, double y)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int c = 0; c < 8; c++)
        {
            var p = new Vec3((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z);
            // a corner behind the eye (perspective) projects mirrored: do not cull that edge
            if (Camera.DepthOf(p) <= 0) return 0;
            var s = Camera.Project(p);
            x0 = Math.Min(x0, s.X); x1 = Math.Max(x1, s.X); y0 = Math.Min(y0, s.Y); y1 = Math.Max(y1, s.Y);
        }
        var dx = Math.Max(0, Math.Max(x0 - x, x - x1)); var dy = Math.Max(0, Math.Max(y0 - y, y - y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

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
