using SpryCad.App.Viewport;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Sketcher;

/// <summary>
/// Sketch pictures: images on the sketch plane to trace over. While one's page is open (<see cref="EditingPicture"/>)
/// it shows its outline and handles: dragged inside it moves, by a corner it scales (about the opposite corner, its
/// shape kept), by the round handle above it turns. Its page can also ask for two points on it
/// (<see cref="PickScalePoints"/>), to scale it from a known distance between them.
/// </summary>
public sealed partial class SketchEditor
{
    /// <summary>The picture whose page is open (null: none).</summary>
    public SkPicture? EditingPicture => _editPicture is { } id ? Sk.Picture(id) : null;
    /// <summary>The two points picked on the picture to scale it from (fewer while picking).</summary>
    public List<Vec2> ScalePoints { get; } = new();
    public bool PickingScalePoints { get; private set; }
    /// <summary>A click on the plane is awaited for the picture's page (<see cref="PickPoint"/>), or the scale points are.</summary>
    public bool PickingPoint => PickingScalePoints || _pickOnce != null;
    Action<Vec2>? _pickOnce;

    /// <summary>The next click on the plane given to <paramref name="got"/> (Esc: none).</summary>
    public void PickPoint(string message, Action<Vec2> got)
    {
        PickingScalePoints = false;
        _pickOnce = got;
        _vp.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross);
        Message?.Invoke(message);
    }

    /// <summary>Stops waiting for a click (Esc).</summary>
    public void CancelPick()
    {
        _pickOnce = null;
        if (PickingScalePoints) { PickingScalePoints = false; ScalePoints.Clear(); }
        _vp.Cursor = Cursor;
        _vp.Redraw();
        PictureChanged?.Invoke();
    }
    /// <summary>The picture moved, scaled or turned in the viewport, or its scale points picked: its page shows the new values.</summary>
    public event Action? PictureChanged;
    /// <summary>A picture double-clicked (in Select, off any sketch entity): to open its page.</summary>
    public event Action<long>? PictureOpened;

    long? _editPicture;
    int _pictureUndo;
    string? _pictureBefore;
    enum PicDrag { None, Move, Scale, Rotate }
    PicDrag _picDrag;
    int _picCorner;
    SkPicture? _picStart;
    Vec2 _picDown;

    /// <summary>
    /// Starts editing a picture (or, with none, one about to be added: <see cref="AddPicture"/>). Everything until
    /// <see cref="EndPictureEdit"/> is one undo step, or nothing when canceled.
    /// </summary>
    public void BeginPictureEdit(long? id)
    {
        SetTool(SketchTool.Select);
        Selected.Clear(); SelectionChanged?.Invoke();
        _pictureUndo = _undo.Count;
        _pictureBefore = System.Text.Json.JsonSerializer.Serialize(Sk, Serialization.Compact);
        Snapshot();
        _editPicture = id;
        if (EditingPicture is { Hidden: true } hidden) hidden.Hidden = false; // (edited where it can be seen)
        ScalePoints.Clear(); PickingScalePoints = false;
        _vp.Redraw();
    }

    /// <summary>Adds a picture (in a <see cref="BeginPictureEdit"/>) and edits it.</summary>
    public SkPicture AddPicture(SkPicture p)
    {
        p.Id = Sk.NewId();
        (Sk.Pictures ??= new()).Add(p);
        _editPicture = p.Id;
        _vp.Redraw();
        return p;
    }

    /// <summary>Ends the picture's edit: kept (one undo step, none when nothing changed), or put back as it was.</summary>
    public void EndPictureEdit(bool keep)
    {
        if (_pictureBefore == null) return;
        var now = System.Text.Json.JsonSerializer.Serialize(Sk, Serialization.Compact);
        if (!keep && now != _pictureBefore) Feature.Sketch = System.Text.Json.JsonSerializer.Deserialize<Sketch>(_pictureBefore, Serialization.Options)!;
        if (!keep || now == _pictureBefore) _undo.RemoveRange(_pictureUndo, _undo.Count - _pictureUndo);
        _pictureBefore = null; _editPicture = null; _picDrag = PicDrag.None;
        _pickOnce = null;
        ScalePoints.Clear(); PickingScalePoints = false;
        Resolve();
        _vp.Redraw();
    }

    /// <summary>Removes a picture (its own undo step outside a picture edit).</summary>
    public void DeletePicture(long id)
    {
        if (Sk.Picture(id) is not { } p) return;
        if (_pictureBefore == null) Snapshot();
        Sk.Pictures!.Remove(p);
        if (Sk.Pictures.Count == 0) Sk.Pictures = null;
        if (_editPicture == id) { _editPicture = null; ScalePoints.Clear(); PickingScalePoints = false; }
        _vp.Redraw();
    }

    /// <summary>Shows or hides a picture (its own undo step).</summary>
    public void SetPictureHidden(long id, bool hidden)
    {
        if (Sk.Picture(id) is not { } p || p.Hidden == hidden) return;
        Snapshot();
        p.Hidden = hidden;
        _vp.Redraw();
    }

    /// <summary>The next two clicks on the plane are the scale points (picked again from none).</summary>
    public void PickScalePoints()
    {
        ScalePoints.Clear();
        PickingScalePoints = true;
        _vp.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross);
        Message?.Invoke("Click two points on the picture a known distance apart.");
        _vp.Redraw();
    }

    /// <summary>The picture or the scale points changed outside the viewport (its page's fields): drawn again.</summary>
    public void PictureEdited() => _vp.Redraw();

    /// <summary>
    /// Scales the editing picture so the scale points are <paramref name="dx"/> apart along its width and
    /// <paramref name="dy"/> along its height (either null: that direction scaled as the other; both null: nothing),
    /// or <paramref name="distance"/> apart (uniformly). About the first point, which stays where it is on the
    /// picture; the points move with it. The problem, or null.
    /// </summary>
    public string? ScalePicture(double? distance, double? dx, double? dy)
    {
        if (EditingPicture is not { } p || ScalePoints.Count < 2) return "Click two points on the picture first.";
        Vec2 a = ScalePoints[0], b = ScalePoints[1];
        var la = p.ToLocal(a); var d = p.ToLocal(b) - la;
        double sx, sy;
        if (distance is { } dist)
        {
            if (d.Length < 1e-9) return "The two points are the same point.";
            if (dist <= 0) return "The distance must be more than 0.";
            sx = sy = dist / d.Length;
        }
        else
        {
            // a direction the points hardly differ in can't be measured: scaled as the other one
            const double minPart = 0.1;
            bool useX = dx != null && Math.Abs(d.X) > minPart * d.Length, useY = dy != null && Math.Abs(d.Y) > minPart * d.Length;
            if (dx != null && !useX) return "The points are almost in line vertically, so they can't set the width. Click points farther apart horizontally.";
            if (dy != null && !useY) return "The points are almost in line horizontally, so they can't set the height. Click points farther apart vertically.";
            if (dx is <= 0 || dy is <= 0) return "Distances must be more than 0.";
            sx = useX ? dx!.Value / Math.Abs(d.X) : 0;
            sy = useY ? dy!.Value / Math.Abs(d.Y) : 0;
            if (!useX) sx = sy;
            if (!useY) sy = sx;
            if (sx == 0) return "Enter a distance.";
        }
        // the first point stays: the center moves with the scale about it, in the picture's own axes
        var newLocalA = new Vec2(la.X * sx, la.Y * sy);
        var centerShift = la - newLocalA;
        p.Width *= sx; p.Height *= sy;
        p.Center = p.Center + p.AxisX * centerShift.X + p.AxisY * centerShift.Y;
        ScalePoints[1] = a + p.AxisX * (d.X * sx) + p.AxisY * (d.Y * sy);
        _vp.Redraw();
        return null;
    }

    /// <summary>Turns the editing picture about the first scale point so the line through the points is horizontal (or vertical).</summary>
    public string? LevelPicture(bool vertical)
    {
        if (EditingPicture is not { } p || ScalePoints.Count < 2) return "Click two points on the picture first.";
        Vec2 a = ScalePoints[0], b = ScalePoints[1];
        if (Vec2.Distance(a, b) < 1e-9) return "The two points are the same point.";
        var ang = (b - a).Angle;
        // the nearer of the two ways the line can lie
        var target = vertical ? Math.PI / 2 : 0;
        var turn = Remainder(target - ang, Math.PI);
        Rotate(p, a, turn);
        ScalePoints[1] = a + Vec2.FromAngle(ang + turn, Vec2.Distance(a, b));
        _vp.Redraw();
        return null;
    }

    static double Remainder(double a, double period)
    {
        a %= period;
        if (a > period / 2) a -= period;
        if (a < -period / 2) a += period;
        return a;
    }

    static void Rotate(SkPicture p, Vec2 about, double turn)
    {
        var c = p.Center - about;
        p.Center = about + Vec2.FromAngle(c.Angle + turn, c.Length);
        p.Angle = Remainder(p.Angle + turn, 2 * Math.PI);
    }

    // ---- in the viewport

    const double HandlePx = 6, RotateHandlePx = 28;

    Vec2 RotateHandle(SkPicture p) => p.FromLocal(new Vec2(0, p.Height / 2)) + p.AxisY * (RotateHandlePx * Px);

    /// <summary>A press while a picture is edited: picked as a scale point, or the start of a move, scale or turn. Handled (true) whatever it hits.</summary>
    bool PicturePointerDown(PointerInfo p)
    {
        if (EditingPicture is not { } pic) return false;
        var m = SketchPos(p);
        if (_pickOnce is { } once)
        {
            _pickOnce = null;
            _vp.Cursor = Cursor;
            once(m);
            return true;
        }
        if (PickingScalePoints)
        {
            ScalePoints.Add(m);
            if (ScalePoints.Count >= 2)
            {
                PickingScalePoints = false;
                _vp.Cursor = Cursor;
                PictureChanged?.Invoke();
            }
            else Message?.Invoke("Click the second point.");
            _vp.Redraw();
            return true;
        }
        var tol = HandlePx * 1.5 * Px;
        var corners = pic.Corners();
        _picDrag = PicDrag.None;
        if (Vec2.Distance(m, RotateHandle(pic)) < tol) _picDrag = PicDrag.Rotate;
        else for (int i = 0; i < 4; i++) if (Vec2.Distance(m, corners[i]) < tol) { _picDrag = PicDrag.Scale; _picCorner = i; break; }
        if (_picDrag == PicDrag.None && pic.Contains(m)) _picDrag = PicDrag.Move;
        _picStart = pic.Clone(); _picDown = m;
        return true;
    }

    bool PicturePointerMove(PointerInfo p)
    {
        if (EditingPicture is not { } pic) return false;
        var m = SketchPos(p);
        _mouse = m;
        if (_picDrag == PicDrag.None || _picStart is not { } s) { _vp.Redraw(); return true; }
        switch (_picDrag)
        {
            case PicDrag.Move:
                pic.Center = s.Center + (m - _picDown);
                break;
            case PicDrag.Rotate:
                {
                    var turn = (m - s.Center).Angle - (_picDown - s.Center).Angle;
                    var a = s.Angle + turn;
                    // within a degree of a quarter turn: on it
                    var q = Math.Round(a / (Math.PI / 2)) * (Math.PI / 2);
                    if (Math.Abs(a - q) < Math.PI / 180) a = q;
                    pic.Angle = Remainder(a, 2 * Math.PI);
                    break;
                }
            case PicDrag.Scale:
                {
                    var c = s.Corners();
                    var opp = c[(_picCorner + 2) % 4];
                    var diag = c[_picCorner] - opp;
                    var k = Vec2.Dot(m - opp, diag) / diag.LengthSq;
                    if (k * Math.Max(s.Width, s.Height) < 4 * Px) k = 4 * Px / Math.Max(s.Width, s.Height);
                    pic.Width = s.Width * k; pic.Height = s.Height * k;
                    pic.Center = opp + diag * (k / 2);
                    break;
                }
        }
        _vp.Redraw();
        return true;
    }

    bool PicturePointerUp()
    {
        if (EditingPicture == null) return false;
        var dragged = _picDrag != PicDrag.None && _picStart is { } s && EditingPicture is { } p
            && (s.X != p.X || s.Y != p.Y || s.Width != p.Width || s.Angle != p.Angle);
        _picDrag = PicDrag.None; _picStart = null;
        if (dragged) PictureChanged?.Invoke();
        return true;
    }

    /// <summary>The topmost shown picture under a sketch point (null: none).</summary>
    SkPicture? PictureAt(Vec2 m) => Sk.Pictures?.LastOrDefault(p => !p.Hidden && p.Contains(m));

    /// <summary>A double-click in Select off any entity: the picture under it opened.</summary>
    bool OpenPictureAt(PointerInfo p)
    {
        if (p.ClickCount < 2 || PictureAt(SketchPos(p)) is not { } pic) return false;
        _down = false;
        PictureOpened?.Invoke(pic.Id);
        return true;
    }

    /// <summary>The pictures (under everything), and the edited one's outline, handles and scale points.</summary>
    void DrawPictures(DynamicGeometry g)
    {
        SketchDrawing.AddPictures(g, Frame, Sk, top: true);
        if (EditingPicture is not { } pic) return;
        var f = Frame; var px = Px;
        var c = pic.Corners();
        var col = ViewportControl.SelectColor;
        for (int i = 0; i < 4; i++) g.Line(f.ToWorld(c[i]), f.ToWorld(c[(i + 1) % 4]), col, top: true);
        if (!PickingScalePoints && ScalePoints.Count < 2)
        {
            foreach (var k in c) Square(g, k, HandlePx * px, col);
            var top = pic.FromLocal(new Vec2(0, pic.Height / 2));
            var rh = RotateHandle(pic);
            g.Line(f.ToWorld(top), f.ToWorld(rh), col, top: true);
            Ring(g, rh, HandlePx * px, col);
        }
        var pc = new Rgba(0.9f, 0.35f, 0.05f);
        foreach (var sp in ScalePoints) SketchDrawing.PointMark(g, f, sp, pc, px, 5);
        if (ScalePoints.Count == 2) g.Line(f.ToWorld(ScalePoints[0]), f.ToWorld(ScalePoints[1]), pc, top: true);
        else if (PickingScalePoints && ScalePoints.Count == 1) g.Dashed(f.ToWorld(ScalePoints[0]), f.ToWorld(_mouse), pc, px * 6, top: true);
    }

    void Square(DynamicGeometry g, Vec2 at, double h, Rgba col)
    {
        var f = Frame;
        Vec2[] q = { at + new Vec2(-h, -h), at + new Vec2(h, -h), at + new Vec2(h, h), at + new Vec2(-h, h) };
        g.Quad(f.ToWorld(q[0]), f.ToWorld(q[1]), f.ToWorld(q[2]), f.ToWorld(q[3]), new Rgba(1, 1, 1), top: true);
        for (int i = 0; i < 4; i++) g.Line(f.ToWorld(q[i]), f.ToWorld(q[(i + 1) % 4]), col, top: true);
    }

    void Ring(DynamicGeometry g, Vec2 at, double r, Rgba col)
    {
        const int n = 20;
        for (int i = 0; i < n; i++)
            g.Line(Frame.ToWorld(at + Vec2.FromAngle(2 * Math.PI * i / n, r)), Frame.ToWorld(at + Vec2.FromAngle(2 * Math.PI * (i + 1) / n, r)), col, top: true);
    }
}
