using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.Viewport;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Sketcher;

public enum SketchTool
{
    Select, Line, Centerline, Rectangle, CenterRectangle, Circle, CenterArc, ThreePointArc, TangentArc, Point, Polygon, Slot, SmartDimension, Trim, Fillet,
}

public enum SkRefKind { Point, Entity, Constraint }

public readonly record struct SkRef(SkRefKind Kind, int Id);

enum SnapKind { None, Point, Origin, Midpoint, OnLine, OnCircle, Center, ModelVertex, ModelEdge, ModelMidpoint, ModelCenter }

/// <summary>Model edge a snap refers to (projected into the sketch as reference geometry on click).</summary>
sealed record ModelHit(Body Body, TopoEdge Edge, Vec3 Point);

readonly record struct Snap(SnapKind Kind, Vec2 P, int Id = -1, bool H = false, bool V = false, int AlignId = -1, ModelHit? Model = null,
    Vec2? AlignX = null, Vec2? AlignY = null);

/// <summary>
/// Interactive 2D sketch editing on a plane: drawing tools with automatic relations (coincident,
/// horizontal/vertical, midpoint, on-entity), Smart Dimension, dragging with live solving and
/// relation display, closely following the SolidWorks sketcher.
/// </summary>
public sealed partial class SketchEditor : IViewportMode
{
    public SketchFeature Feature { get; }
    public Sketch Sk => Feature.Sketch;
    public Frame Frame { get; }
    public Func<string, double?> Lookup { get; set; }
    public SolveResult Status { get; private set; } = new();
    public SketchTool Tool { get; private set; } = SketchTool.Select;
    public List<SkRef> Selected { get; } = new();
    public int PolygonSides { get; set; } = 6;
    public bool ShowRelations { get; set; } = true;
    public bool Modified { get; private set; }
    /// <summary>Restricts drawing to points (Hole Wizard positions).</summary>
    public bool PointsOnly { get; init; }

    public event Action? Changed;          // geometry / status changed
    public event Action? SelectionChanged;
    public event Action<string>? Message;  // status bar hints
    public event Action? ToolChanged;

    readonly ViewportControl _vp;
    readonly List<string> _undo = new();
    readonly List<string> _redo = new();

    // pointer state
    Vec2 _mouse; Snap _snap; SkRef? _hover;
    readonly List<Vec2> _clicks = new();
    readonly List<Snap> _clickSnaps = new();
    int _chainPoint = -1, _chainStartPoint = -1;
    double _arcSweep; Vec2 _arcLast;
    Point _downScreen; bool _down; bool _dragging; Vec2 _downSketch;
    Dictionary<int, Vec2>? _dragOrigins; int _dragCircle = -1; SkRef? _dragLabel;
    Rect? _boxSel;
    Vec3? _hoverVertex;
    // smart dimension state
    readonly List<SkRef> _dimPicks = new();
    SkConstraint? _dimPreview;
    TextBox? _modifyBox; SkConstraint? _modifyTarget;

    public static readonly Rgba Under = new(0.05f, 0.25f, 0.95f);
    public static readonly Rgba Full = new(0.05f, 0.05f, 0.05f);
    public static readonly Rgba Over = new(0.9f, 0.1f, 0.1f);
    public static readonly Rgba Constr = new(0.35f, 0.45f, 0.6f);
    public static readonly Rgba DimColor = new(0.10f, 0.10f, 0.12f);
    /// <summary>A selected dimension's lines: the blue of its label (Brushes.RoyalBlue).</summary>
    public static readonly Rgba DimSelectedColor = new(0.255f, 0.412f, 0.882f);
    public static readonly Rgba RefColor = new(0.55f, 0.25f, 0.65f);

    public SketchEditor(ViewportControl vp, SketchFeature feature, Frame frame, Func<string, double?> lookup)
    {
        _vp = vp; Feature = feature; Frame = frame; Lookup = lookup;
        Resolve();
    }

    public Cursor? Cursor => Tool == SketchTool.Select ? null : new Cursor(StandardCursorType.Cross);

    // ---------------------------------------------------------------------------------------
    // public API

    public void SetTool(SketchTool t)
    {
        if (PointsOnly && t is not (SketchTool.Point or SketchTool.Select or SketchTool.SmartDimension)) t = SketchTool.Point;
        CancelOperation();
        Tool = t;
        _filletFirstLine = -1;
        ToolChanged?.Invoke();
        Message?.Invoke(t switch
        {
            SketchTool.Line => "Line: click to start, click to add segments. Esc or double-click ends the chain.",
            SketchTool.Centerline => "Centerline: click start and end points.",
            SketchTool.Rectangle => "Corner Rectangle: click the first corner, then the opposite corner.",
            SketchTool.CenterRectangle => "Center Rectangle: click the center, then a corner.",
            SketchTool.Circle => "Circle: click the center, then a point on the circle.",
            SketchTool.CenterArc => "Centerpoint Arc: click the center, the start point, then the end point.",
            SketchTool.ThreePointArc => "3 Point Arc: click start, end, then a point on the arc.",
            SketchTool.TangentArc => "Tangent Arc: click the end of a line or arc, then the arc end point.",
            SketchTool.Point => "Point: click to place points.",
            SketchTool.Polygon => $"Polygon ({PolygonSides} sides): click the center, then a vertex.",
            SketchTool.Slot => "Straight Slot: click the first center, the second center, then the width.",
            SketchTool.SmartDimension => "Smart Dimension: select one or two entities, then click to place the dimension.",
            SketchTool.Trim => "Trim: click the part of an entity to delete (between intersections).",
            SketchTool.Fillet => $"Sketch Fillet (R {Expr.Format(FilletRadius)}): click corners where two lines meet, or two lines. Deleting a fillet's arc restores the corner.",
            _ => "Select sketch entities. Drag to move; double-click a dimension to edit it.",
        });
        _vp.Cursor = Cursor;
        _vp.Redraw();
    }

    public void CancelOperation()
    {
        _clicks.Clear(); _clickSnaps.Clear(); _chainPoint = -1; _chainStartPoint = -1;
        _dimPicks.Clear(); _dimPreview = null;
        Sk.RemoveUnusedReferences();
        CloseModifyBox(false);
        _vp.Redraw();
    }

    /// <summary>Solves the sketch with dimension values evaluated from their expressions.</summary>
    public void Resolve(IReadOnlyDictionary<int, Vec2>? drag = null)
    {
        foreach (var c in Sk.Constraints)
            if (c.IsDimension && !c.Driven && !string.IsNullOrWhiteSpace(c.Expr) && Expr.TryEval(c.Expr, Lookup, out var v, out _))
                c.Value = v;
        Status = SketchSolver.Solve(Sk, drag);
        foreach (var c in Sk.Constraints) if (c.Driven) c.Value = SketchSolver.Measure(Sk, c);
        Changed?.Invoke();
    }

    void Snapshot()
    {
        _undo.Add(System.Text.Json.JsonSerializer.Serialize(Sk, Serialization.Options));
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        Modified = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(System.Text.Json.JsonSerializer.Serialize(Sk, Serialization.Options));
        Restore(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(System.Text.Json.JsonSerializer.Serialize(Sk, Serialization.Options));
        Restore(_redo[^1]); _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    void Restore(string json)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<Sketch>(json, Serialization.Options)!;
        Feature.Sketch = s;
        Selected.Clear();
        Resolve();
        SelectionChanged?.Invoke();
        _vp.Redraw();
    }

    public void DeleteSelection()
    {
        if (Selected.Count == 0) return;
        Snapshot();
        foreach (var r in Selected.ToList())
        {
            switch (r.Kind)
            {
                case SkRefKind.Constraint:
                    if (r.Id < 0) { var p = Sk.Point(-r.Id); if (p != null && p.Id != Sketch.OriginId) p.Fixed = false; }
                    else Sk.DeleteConstraint(r.Id);
                    break;
                case SkRefKind.Entity: Sk.DeleteEntity(r.Id); break;
                case SkRefKind.Point:
                    foreach (var e in Sk.EntitiesUsing(r.Id).ToList()) Sk.DeleteEntity(e.Id);
                    break;
            }
        }
        Sk.RemoveUnusedReferences();
        Sk.Cleanup();
        Selected.Clear();
        Resolve();
        SelectionChanged?.Invoke();
        _vp.Redraw();
    }

    public void SetConstruction(bool on)
    {
        Snapshot();
        foreach (var r in Selected) if (r.Kind == SkRefKind.Entity && Sk.Entity(r.Id) is { } e) e.Construction = on;
        Resolve();
        _vp.Redraw();
    }

    // ---- relations ----

    public IEnumerable<SkEntity> SelectedEntities => Selected.Where(r => r.Kind == SkRefKind.Entity).Select(r => Sk.Entity(r.Id)).OfType<SkEntity>();
    public IEnumerable<int> SelectedPoints => Selected.Where(r => r.Kind == SkRefKind.Point).Select(r => r.Id);

    /// <summary>Relations applicable to the current selection.</summary>
    public List<CType> ApplicableRelations()
    {
        var res = new List<CType>();
        var ents = SelectedEntities.ToList();
        var lines = ents.OfType<SkLine>().ToList();
        var rounds = ents.Where(e => e is SkCircle or SkArc).ToList();
        var pts = SelectedPoints.ToList();
        pts.AddRange(ents.OfType<SkPointEntity>().Select(p => p.P));
        int others = ents.Count - lines.Count - rounds.Count - ents.OfType<SkPointEntity>().Count();
        if (others > 0) return res;
        if (pts.Count == 0 && rounds.Count == 0 && lines.Count >= 1) { res.Add(CType.Horizontal); res.Add(CType.Vertical); }
        if (pts.Count == 0 && rounds.Count == 0 && lines.Count >= 2) { res.Add(CType.Collinear); res.Add(CType.Parallel); res.Add(CType.Equal); if (lines.Count == 2) res.Add(CType.Perpendicular); }
        if (lines.Count == 0 && rounds.Count == 0 && pts.Count == 2) { res.Add(CType.Horizontal); res.Add(CType.Vertical); res.Add(CType.Coincident); }
        if (pts.Count == 1 && lines.Count == 1 && rounds.Count == 0) { res.Add(CType.PointOnLine); res.Add(CType.Midpoint); }
        if (pts.Count == 1 && rounds.Count == 1 && lines.Count == 0) res.Add(CType.PointOnCircle);
        if (pts.Count == 0 && lines.Count == 1 && rounds.Count == 1) res.Add(CType.Tangent);
        if (pts.Count == 0 && lines.Count == 0 && rounds.Count == 2) { res.Add(CType.Tangent); res.Add(CType.Equal); res.Add(CType.Concentric); }
        if (pts.Count == 0 && lines.Count == 0 && rounds.Count > 2) res.Add(CType.Equal);
        if (pts.Count == 2 && lines.Count == 1 && rounds.Count == 0) res.Add(CType.Symmetric);
        return res;
    }

    public static string RelationName(CType t) => t switch
    {
        CType.PointOnLine or CType.PointOnCircle or CType.Coincident => "Coincident",
        CType.Midpoint => "Midpoint",
        CType.Horizontal => "Horizontal",
        CType.Vertical => "Vertical",
        CType.Parallel => "Parallel",
        CType.Perpendicular => "Perpendicular",
        CType.Collinear => "Collinear",
        CType.Equal => "Equal",
        CType.Tangent => "Tangent",
        CType.Concentric => "Concentric",
        CType.Symmetric => "Symmetric",
        _ => t.ToString(),
    };

    public static string Glyph(CType t) => t switch
    {
        CType.Horizontal => "—",
        CType.Vertical => "|",
        CType.Parallel => "∥",
        CType.Perpendicular => "⊥",
        CType.Collinear => "⁄",
        CType.Equal => "=",
        CType.Tangent => "T",
        CType.PointOnLine or CType.PointOnCircle or CType.Coincident => "•",
        CType.Midpoint => "M",
        CType.Concentric => "◎",
        CType.Symmetric => "⇹",
        _ => "?",
    };

    public string? AddRelation(CType type)
    {
        var ents = SelectedEntities.ToList();
        var lines = ents.OfType<SkLine>().ToList();
        var rounds = ents.Where(e => e is SkCircle or SkArc).ToList();
        var pts = SelectedPoints.ToList();
        pts.AddRange(ents.OfType<SkPointEntity>().Select(p => p.P));
        Snapshot();
        var added = new List<SkConstraint>();
        SkConstraint New(CType t, params int[] refs) { var c = new SkConstraint { Id = Sk.NewId(), Type = t, Refs = refs }; SketchSolver.CaptureSign(Sk, c); added.Add(c); return c; }
        switch (type)
        {
            case CType.Horizontal:
            case CType.Vertical:
                if (lines.Count > 0) foreach (var l in lines) New(type, l.Id);
                else if (pts.Count == 2) New(type, pts[0], pts[1]);
                break;
            case CType.Parallel:
            case CType.Collinear:
            case CType.Equal when lines.Count >= 2:
                for (int i = 1; i < lines.Count; i++) New(type, lines[0].Id, lines[i].Id);
                break;
            case CType.Equal:
                for (int i = 1; i < rounds.Count; i++) New(type, rounds[0].Id, rounds[i].Id);
                break;
            case CType.Perpendicular: New(type, lines[0].Id, lines[1].Id); break;
            case CType.Coincident:
                Sk.MergePoints(pts[1], pts[0]);
                break;
            case CType.PointOnLine: New(type, pts[0], lines[0].Id); break;
            case CType.Midpoint: New(type, pts[0], lines[0].Id); break;
            case CType.PointOnCircle: New(type, pts[0], rounds[0].Id); break;
            case CType.Tangent:
                if (lines.Count == 1) New(type, lines[0].Id, rounds[0].Id); else New(type, rounds[0].Id, rounds[1].Id);
                break;
            case CType.Concentric: New(type, rounds[0].Id, rounds[1].Id); break;
            case CType.Symmetric: New(type, pts[0], pts[1], lines[0].Id); break;
        }
        foreach (var c in added)
        {
            if (SketchSolver.WouldOverDefine(Sk, c))
            {
                Undo(); _redo.Clear();
                return "Adding this relation would over define the sketch.";
            }
            Sk.Constraints.Add(c);
        }
        Resolve();
        _vp.Redraw();
        return null;
    }

    public void FixSelection(bool fix)
    {
        Snapshot();
        var pts = SelectedPoints.ToList();
        foreach (var e in SelectedEntities) pts.AddRange(e.PointIds);
        // floating converted geometry takes it off its model edge (else the next rebuild would hold it there again)
        if (!fix) foreach (var e in SelectedEntities.Concat(Sk.Entities.Where(x => x.Link != null && x.PointIds.Any(pts.Contains))).Distinct().ToList()) Sk.Unlink(e);
        foreach (var id in pts) if (Sk.Point(id) is { } p && p.Id != Sketch.OriginId) p.Fixed = fix;
        Resolve();
        _vp.Redraw();
    }

    /// <summary>Takes the selected converted geometry off its model edges (it stays, as free geometry).</summary>
    public void UnlinkSelection()
    {
        Snapshot();
        foreach (var e in SelectedEntities.Where(e => e.Link != null).ToList()) Sk.Unlink(e);
        Resolve();
        _vp.Redraw();
    }

    /// <summary>Relations involving the selected items (for the "Existing Relations" list).</summary>
    public IEnumerable<SkConstraint> RelationsOfSelection()
    {
        var ids = new HashSet<int>(Selected.Where(s => s.Kind != SkRefKind.Constraint).Select(s => s.Id));
        foreach (var e in SelectedEntities) foreach (var p in e.PointIds) ids.Add(p);
        return Sk.Constraints.Where(c => !c.IsDimension && c.Refs.Any(ids.Contains));
    }

    public void SetPointPosition(int id, Vec2 p)
    {
        Snapshot();
        Resolve(new Dictionary<int, Vec2> { [id] = p });
        _vp.Redraw();
    }

    // ---- convert entities (project model edges) ----

    /// <summary>
    /// Projects model edges into the sketch (<see cref="SketchConvert"/>), kept on them as the model changes: exact where the model has
    /// its curves; edges known only by their display polyline come as fixed lines.
    /// </summary>
    public int ConvertEdges(IEnumerable<(Kernel.Native.NEdge edge, string bodyId)> exact, IEnumerable<TopoEdge> meshOnly)
    {
        Snapshot();
        int created = 0;
        foreach (var (e, body) in exact) if (SketchConvert.Edge(Sk, Frame, e, body)) created++;
        foreach (var e in meshOnly)
        {
            var pts = new List<Vec2> { Frame.ToLocal(e.Segs[0].A) };
            if (e.IsLinear) pts.Add(Frame.ToLocal(e.End));
            else foreach (var s in e.Segs) pts.Add(Frame.ToLocal(s.B));
            if (SketchConvert.Polyline(Sk, pts)) created++;
        }
        Resolve();
        Message?.Invoke(created > 0 ? $"Converted {created} edge(s): they follow the model as it changes." : "Nothing new to convert: those edges are in the sketch already.");
        _vp.Redraw();
        return created;
    }

    // ---------------------------------------------------------------------------------------
    // input

    // a camera the view will move to, standing in for the view's own while working out a layout for it
    Camera? _layoutCamera;
    Camera Cam => _layoutCamera ?? _vp.Camera;

    double Px => Cam.PixelSize;

    Vec2 SketchPos(PointerInfo p) => _vp.PlanePoint(Frame, p.X, p.Y) ?? _mouse;

    Point Screen(Vec2 p)
    {
        var s = Cam.Project(Frame.ToWorld(p));
        return new Point(s.X, s.Y);
    }

    Point? _lastScreen;

    public void PointerMove(ViewportControl vp, PointerInfo p)
    {
        _lastScreen = new Point(p.X, p.Y);
        _mouse = SketchPos(p);
        if (_down && Tool == SketchTool.Select)
        {
            var d = Math.Abs(p.X - _downScreen.X) + Math.Abs(p.Y - _downScreen.Y);
            if (!_dragging && d > 4) BeginDrag();
            if (_dragging) { UpdateDrag(p); return; }
        }
        if (Tool is SketchTool.Select or SketchTool.SmartDimension or SketchTool.Trim or SketchTool.Fillet)
        {
            var h = HitTest(p.X, p.Y);
            if (h != _hover) { _hover = h; }
            _hoverVertex = null;
            if (h == null && Tool == SketchTool.Select)
                _vp.Hover = _vp.Pick(p.X, p.Y, SelFilter.Edge | SelFilter.Face);
            else if (h == null && Tool == SketchTool.SmartDimension)
            {
                var v = _vp.PickVertex(p.X, p.Y);
                _hoverVertex = v?.point;
                _vp.Hover = v == null ? _vp.Pick(p.X, p.Y, SelFilter.Edge) : null;
            }
            else _vp.Hover = null;
            if (Tool == SketchTool.SmartDimension) UpdateDimPreview();
        }
        else
        {
            _snap = FindSnap(_mouse, _clicks.Count > 0 ? _clicks[^1] : null);
            UpdateSnapLabel();
            if (Tool == SketchTool.CenterArc && _clicks.Count == 2)
            {
                var c = _clicks[0];
                var a0 = (_arcLast - c).Angle; var a1 = (_snap.P - c).Angle;
                var da = a1 - a0; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
                _arcSweep += da; _arcLast = _snap.P;
            }
        }
        vp.Redraw();
    }

    public void PointerDown(ViewportControl vp, PointerInfo p)
    {
        _lastScreen = new Point(p.X, p.Y);
        _mouse = SketchPos(p);
        _down = true; _dragging = false;
        _downScreen = new Point(p.X, p.Y); _downSketch = _mouse;
        CloseModifyBox(true);
        switch (Tool)
        {
            case SketchTool.Select:
                {
                    var h = HitTest(p.X, p.Y);
                    if (p.ClickCount >= 2 && h is { Kind: SkRefKind.Constraint } hc && Sk.Constraint(hc.Id) is { IsDimension: true } dim)
                    {
                        _down = false;
                        OpenModifyBox(dim);
                        return;
                    }
                    if (h != null)
                    {
                        if (p.Ctrl) { if (Selected.Contains(h.Value)) Selected.Remove(h.Value); else Selected.Add(h.Value); }
                        else if (!Selected.Contains(h.Value)) { Selected.Clear(); Selected.Add(h.Value); }
                        _vp.Selection?.Clear();
                    }
                    else
                    {
                        // model edges/faces can be selected too (for Convert Entities / Normal To), with a click:
                        // done on release, so a drag starting over the part is a box selection as anywhere else
                        _modelPick = _vp.Pick(p.X, p.Y, SelFilter.Edge | SelFilter.Face);
                        _modelPickCtrl = p.Ctrl;
                        if (!p.Ctrl) Selected.Clear();
                        if (_modelPick == null && !p.Ctrl) _vp.Selection?.Clear();
                    }
                    SelectionChanged?.Invoke();
                    break;
                }
            case SketchTool.SmartDimension:
                DimClick(p);
                break;
            case SketchTool.Trim:
                TrimAt(p);
                break;
            case SketchTool.Fillet:
                FilletAt(p);
                break;
            default:
                if (p.ClickCount >= 2 && (Tool is SketchTool.Line or SketchTool.Centerline)) { EndChain(); return; }
                DrawClick();
                break;
        }
        vp.Redraw();
    }

    // a model edge/face pressed on in Select: selected on release if the press was a click (not a box drag)
    SelItem? _modelPick;
    bool _modelPickCtrl;

    public void PointerUp(ViewportControl vp, PointerInfo p)
    {
        var wasDragging = _dragging;
        _down = false;
        if (!wasDragging && _modelPick is { } mp && _vp.Selection != null)
        {
            if (_modelPickCtrl) _vp.Selection.Toggle(mp); else _vp.Selection.Set(mp);
            SelectionChanged?.Invoke();
            vp.Redraw();
        }
        _modelPick = null;
        if (_dragging) EndDrag();
        else if (Tool is SketchTool.Line or SketchTool.Centerline && _clicks.Count == 1 && _chainPoint < 0)
        {
            // click-drag-release creates a single line
            var d = Math.Abs(p.X - _downScreen.X) + Math.Abs(p.Y - _downScreen.Y);
            if (d > 8) { _mouse = SketchPos(p); _snap = FindSnap(_mouse, _clicks[0]); DrawClick(); EndChain(); }
        }
        _boxSel = null;
        if (wasDragging) vp.Redraw();
    }

    public bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods)
    {
        if (_modifyBox != null) return false;
        switch (key)
        {
            case Key.Escape:
                if (_clicks.Count > 0 || _dimPicks.Count > 0) { CancelOperation(); return true; }
                if (Tool != SketchTool.Select) { SetTool(SketchTool.Select); return true; }
                if (Selected.Count > 0) { Selected.Clear(); SelectionChanged?.Invoke(); vp.Redraw(); return true; }
                return false;
            case Key.Delete:
            case Key.Back:
                DeleteSelection();
                return true;
        }
        if (mods == KeyModifiers.None)
        {
            SketchTool? t = key switch
            {
                Key.L => SketchTool.Line,
                Key.R => SketchTool.Rectangle,
                Key.C => SketchTool.Circle,
                Key.A => SketchTool.CenterArc,
                Key.D => SketchTool.SmartDimension,
                Key.P => SketchTool.Point,
                Key.T => SketchTool.Trim,
                _ => null,
            };
            if (t != null) { SetTool(t.Value); return true; }
        }
        return false;
    }

    // ---- hit testing ----

    SkRef? HitTest(double sx, double sy, bool includeDims = true)
    {
        const double tolPx = 7;
        SkRef? best = null; double bestD = tolPx;
        // points first (they win over entities)
        foreach (var pt in Sk.Points)
        {
            var s = Screen(pt.P);
            var d = Math.Sqrt((s.X - sx) * (s.X - sx) + (s.Y - sy) * (s.Y - sy));
            if (d < bestD) { bestD = d; best = new SkRef(SkRefKind.Point, pt.Id); }
        }
        if (best != null)
        {
            // a point entity selects as the entity
            var pe = Sk.Entities.OfType<SkPointEntity>().FirstOrDefault(e => e.P == best.Value.Id);
            return pe != null ? new SkRef(SkRefKind.Entity, pe.Id) : best;
        }
        bestD = tolPx;
        // where geometry overlaps (a fillet's trimmed line over its construction original), the solid line is the one
        // seen, so it wins: construction and model reference geometry counts a little farther away
        foreach (var e in Sk.Entities)
        {
            var penalty = e.Construction || e.IsReference ? 2.0 : 0;
            foreach (var (a, b) in SketchDrawing.Segments2D(Sk, e))
            {
                var d = SegDistPx(a, b, sx, sy) + penalty;
                if (d < bestD) { bestD = d; best = new SkRef(SkRefKind.Entity, e.Id); }
            }
        }
        if (best != null) return best;
        if (includeDims && Overlay != null)
        {
            foreach (var (c, ctl) in _labels)
            {
                var b = ctl.Bounds;
                if (ctl.IsVisible && b.Inflate(2).Contains(new Point(sx, sy)))
                    return new SkRef(SkRefKind.Constraint, c);
            }
            // a dimension's own lines pick it too (geometry under the cursor wins, above)
            bestD = 5;
            foreach (var c in Sk.Constraints.Where(c => c.IsDimension))
                foreach (var (a, b) in DimLines(c))
                {
                    var d = SegDistPx(a, b, sx, sy);
                    if (d < bestD) { bestD = d; best = new SkRef(SkRefKind.Constraint, c.Id); }
                }
            if (best != null) return best;
        }
        return null;
    }

    double SegDistPx(Vec2 a, Vec2 b, double sx, double sy)
    {
        var pa = Screen(a); var pb = Screen(b);
        double dx = pb.X - pa.X, dy = pb.Y - pa.Y; var l2 = dx * dx + dy * dy;
        var t = l2 > 1e-12 ? Math.Clamp(((sx - pa.X) * dx + (sy - pa.Y) * dy) / l2, 0, 1) : 0;
        var px = pa.X + dx * t - sx; var py = pa.Y + dy * t - sy;
        return Math.Sqrt(px * px + py * py);
    }

    // ---- snapping / inference ----

    Snap FindSnap(Vec2 m, Vec2? from)
    {
        var tol = Px * 8;
        // a line ending on a fixed snap point that is level/plumb with its start also gets Horizontal/Vertical
        // (the point can't be moved onto the inference, so only when it already lines up to within a pixel)
        Snap Aligned(Snap s)
        {
            if (from is not { } f || Tool is not (SketchTool.Line or SketchTool.Centerline)) return s;
            var d = s.P - f;
            if (d.Length < tol) return s;
            if (Math.Abs(d.Y) < Px) return s with { H = true };
            if (Math.Abs(d.X) < Px) return s with { V = true };
            return s;
        }
        // existing points
        SkPoint? bestP = null; double bestD = tol;
        foreach (var p in Sk.Points)
        {
            var d = Vec2.Distance(p.P, m);
            if (d < bestD) { bestD = d; bestP = p; }
        }
        if (bestP != null) return Aligned(new Snap(bestP.Id == Sketch.OriginId ? SnapKind.Origin : SnapKind.Point, bestP.P, bestP.Id));
        // model vertices (part corners, arc ends) under the cursor; the hit is kept so the point can be tied to
        // this vertex later (a line's start is only resolved when its end is clicked, elsewhere on screen)
        if (_lastScreen is { } scr && _vp.PickVertex(scr.X, scr.Y) is { } mv)
            return Aligned(new Snap(SnapKind.ModelVertex, Frame.ToLocal(mv.point), Model: new ModelHit(mv.body, mv.edge, mv.point)));
        // centers of model circles in planes parallel to the sketch (hole and boss rims, arcs)
        foreach (var mc in ModelCircles())
            if (Vec2.Distance(mc.center, m) < tol) return Aligned(new Snap(SnapKind.ModelCenter, mc.center, Model: new ModelHit(mc.body, mc.edge, mc.edge.Start)));
        // midpoints
        foreach (var l in Sk.Entities.OfType<SkLine>())
        {
            var mid = (Sk.P(l.P1) + Sk.P(l.P2)) * 0.5;
            if (Vec2.Distance(mid, m) < tol) return Aligned(new Snap(SnapKind.Midpoint, mid, l.Id));
        }
        // model edge under the cursor: its midpoint, or a point on it
        var modelEdge = ModelEdgeSnap(m, tol);
        if (modelEdge is { Kind: SnapKind.ModelMidpoint }) return Aligned(modelEdge.Value);
        // horizontal / vertical inference relative to the previous point
        bool h = false, v = false;
        var pos = m;
        if (from is { } f && Tool is SketchTool.Line or SketchTool.Centerline)
        {
            var d = m - f;
            if (d.Length > tol)
            {
                if (Math.Abs(d.Y) < tol * 0.6) { pos = new Vec2(m.X, f.Y); h = true; }
                else if (Math.Abs(d.X) < tol * 0.6) { pos = new Vec2(f.X, m.Y); v = true; }
            }
        }
        // on entity
        foreach (var e in Sk.Entities)
        {
            if (e is SkLine l)
            {
                var a = Sk.P(l.P1); var b = Sk.P(l.P2); var dir = b - a; var len2 = dir.LengthSq;
                if (len2 < 1e-18) continue;
                var t = Vec2.Dot(m - a, dir) / len2;
                if (t <= 0.02 || t >= 0.98) continue;
                var proj = a + dir * t;
                if (Vec2.Distance(proj, m) < tol * 0.7 && !h && !v) return new Snap(SnapKind.OnLine, proj, l.Id);
            }
            else if (e is SkCircle or SkArc)
            {
                var c = e is SkCircle ci ? Sk.P(ci.Center) : Sk.P(((SkArc)e).Center);
                var r = Sk.Radius(e);
                var dist = (m - c).Length;
                if (Math.Abs(dist - r) < tol * 0.7 && dist > 1e-9 && !h && !v)
                    return new Snap(SnapKind.OnCircle, c + (m - c) * (r / dist), e.Id);
            }
        }
        if (modelEdge is { } me && !h && !v) return me;
        // inference lines: line up with the X or Y of sketch points / model vertices (no relation is added)
        Vec2? ax = null, ay = null;
        double bx = tol * 0.6, by = tol * 0.6;
        foreach (var q in AlignmentPoints())
        {
            if (Vec2.Distance(q, m) < tol * 2) continue; // that would be a point snap
            if (!v && Math.Abs(q.X - m.X) < bx) { bx = Math.Abs(q.X - m.X); ax = q; }
            if (!h && Math.Abs(q.Y - m.Y) < by) { by = Math.Abs(q.Y - m.Y); ay = q; }
        }
        if (ax is { } qx) pos = new Vec2(qx.X, pos.Y);
        if (ay is { } qy) pos = new Vec2(pos.X, qy.Y);
        return new Snap(SnapKind.None, pos, -1, h, v, AlignX: ax, AlignY: ay);
    }

    /// <summary>
    /// Snap to the model edge under the cursor (projected into the sketch plane): its midpoint (lines) or the
    /// nearest point on it (lines and circles parallel to the sketch). Null when no usable edge is hovered.
    /// </summary>
    Snap? ModelEdgeSnap(Vec2 m, double tol)
    {
        if (_lastScreen is not { } scr) return null;
        var hit = _vp.Pick(scr.X, scr.Y, SelFilter.Edge);
        if (hit?.Edge == null) return null;
        var body = _vp.Bodies.FirstOrDefault(b => b.Id == hit.BodyId);
        if (body == null || hit.EdgeIndex < 0 || hit.EdgeIndex >= body.Solid.Topology.Edges.Count) return null;
        var edge = body.Solid.Topology.Edges[hit.EdgeIndex];
        if (edge.IsLinear)
        {
            var a = Frame.ToLocal(edge.Start); var b = Frame.ToLocal(edge.End);
            var d = b - a; var len2 = d.LengthSq;
            if (len2 < 1e-12) return null; // seen end-on
            var mid = (a + b) * 0.5;
            if (Vec2.Distance(mid, m) < tol) return new Snap(SnapKind.ModelMidpoint, mid, Model: new ModelHit(body, edge, (edge.Start + edge.End) * 0.5));
            var t = Math.Clamp(Vec2.Dot(m - a, d) / len2, 0, 1);
            var proj = a + d * t;
            if (Vec2.Distance(proj, m) > tol * 1.5) return null;
            return new Snap(SnapKind.ModelEdge, proj, Model: new ModelHit(body, edge, edge.Start + (edge.End - edge.Start) * t));
        }
        if (SketchReferences.CircleOf(edge, Frame) is { } circ)
        {
            var c = Frame.ToLocal(circ.center);
            var dist = (m - c).Length;
            if (dist < 1e-9 || Math.Abs(dist - circ.r) > tol * 1.5) return null;
            return new Snap(SnapKind.ModelEdge, c + (m - c) * (circ.r / dist), Model: new ModelHit(body, edge, edge.Midpoint));
        }
        return null;
    }

    List<Vec2>? _modelVertices;
    Solid[]? _modelVerticesKey;

    /// <summary>Sketch points plus projected model vertices (cached per model state) for inference lines.</summary>
    IEnumerable<Vec2> AlignmentPoints()
    {
        foreach (var p in Sk.Points) yield return p.P;
        var key = _vp.Bodies.Select(b => b.Solid).ToArray();
        if (_modelVertices == null || _modelVerticesKey == null || key.Length != _modelVerticesKey.Length
            || !key.Zip(_modelVerticesKey).All(z => ReferenceEquals(z.First, z.Second)))
        {
            var list = new List<Vec2>();
            foreach (var b in _vp.Bodies)
                foreach (var e in b.Solid.Topology.Edges)
                {
                    list.Add(Frame.ToLocal(e.Start));
                    if (!e.Closed) list.Add(Frame.ToLocal(e.End));
                }
            foreach (var (_, _, c) in ModelCircles()) list.Add(c);
            _modelVertices = list.Distinct().ToList(); _modelVerticesKey = key;
        }
        foreach (var q in _modelVertices) yield return q;
    }

    List<(Body body, TopoEdge edge, Vec2 center)>? _modelCircles;
    Solid[]? _modelCirclesKey;

    /// <summary>Circular model edges parallel to the sketch plane with their projected centers (cached per model state).</summary>
    List<(Body body, TopoEdge edge, Vec2 center)> ModelCircles()
    {
        var bodies = _vp.Bodies;
        // keyed by the solids themselves (rebuilds produce new solids; the body list may be reused)
        var key = bodies.Select(b => b.Solid).ToArray();
        if (_modelCircles != null && _modelCirclesKey != null && key.Length == _modelCirclesKey.Length
            && key.Zip(_modelCirclesKey).All(z => ReferenceEquals(z.First, z.Second))) return _modelCircles;
        var list = new List<(Body, TopoEdge, Vec2)>();
        foreach (var b in bodies)
            foreach (var e in b.Solid.Topology.Edges)
                if (!e.IsLinear && SketchReferences.CircleOf(e, Frame) is { } circ)
                    list.Add((b, e, Frame.ToLocal(circ.center)));
        _modelCircles = list; _modelCirclesKey = key;
        return list;
    }

    int ResolvePoint(Snap s)
    {
        switch (s.Kind)
        {
            case SnapKind.Point:
            case SnapKind.Origin:
                return s.Id;
        }
        if (s.Kind == SnapKind.ModelVertex && s.Model is { } vh &&
            SketchReferences.Create(Sk, Frame, vh.Edge, vh.Body.Id, true, vh.Point) is SkPointEntity rp)
            return rp.P; // shares the fixed reference point: coincident with the model vertex
        if (s.Model is { } mh && s.Kind is SnapKind.ModelEdge or SnapKind.ModelMidpoint or SnapKind.ModelCenter)
        {
            // associative relation to the model: the edge becomes reference geometry, the point is tied to it
            var ent = SketchReferences.Create(Sk, Frame, mh.Edge, mh.Body.Id, false, mh.Point);
            if (s.Kind == SnapKind.ModelCenter && ent is SkCircle rc) return rc.Center;
            var mp = Sk.AddPoint(s.P);
            if (s.Kind == SnapKind.ModelMidpoint && ent is SkLine ml) Sk.AddConstraint(CType.Midpoint, mp.Id, ml.Id);
            else if (ent is SkLine el) Sk.AddConstraint(CType.PointOnLine, mp.Id, el.Id);
            else if (ent is SkCircle ec) Sk.AddConstraint(CType.PointOnCircle, mp.Id, ec.Id);
            return mp.Id;
        }
        var p = Sk.AddPoint(s.P);
        switch (s.Kind)
        {
            case SnapKind.Midpoint: Sk.AddConstraint(CType.Midpoint, p.Id, s.Id); break;
            case SnapKind.OnLine: Sk.AddConstraint(CType.PointOnLine, p.Id, s.Id); break;
            case SnapKind.OnCircle: Sk.AddConstraint(CType.PointOnCircle, p.Id, s.Id); break;
        }
        return p.Id;
    }

    // ---- drawing tools ----

    void DrawClick()
    {
        var s = _snap.Kind == SnapKind.None && _snap.P == default ? new Snap(SnapKind.None, _mouse) : _snap;
        if (s.Kind == SnapKind.None && !s.H && !s.V) s = s with { P = _mouse };
        switch (Tool)
        {
            case SketchTool.Point:
                Snapshot();
                var pid = ResolvePoint(s);
                Sk.Add(new SkPointEntity { P = pid });
                Resolve();
                return;
            case SketchTool.Line:
            case SketchTool.Centerline:
                LineClick(s);
                return;
        }
        _clicks.Add(s.P); _clickSnaps.Add(s);
        if (Tool == SketchTool.CenterArc && _clicks.Count == 2) { _arcSweep = 0; _arcLast = s.P; }
        int needed = Tool switch
        {
            SketchTool.Rectangle or SketchTool.CenterRectangle or SketchTool.Circle or SketchTool.Polygon or SketchTool.TangentArc => 2,
            SketchTool.CenterArc or SketchTool.ThreePointArc or SketchTool.Slot => 3,
            _ => 2,
        };
        if (Tool == SketchTool.TangentArc && _clicks.Count == 1 && TangentAt(_clickSnaps[0]) == null)
        {
            Message?.Invoke("Tangent Arc must start at the end point of a line or arc.");
            _clicks.Clear(); _clickSnaps.Clear();
            return;
        }
        if (_clicks.Count < needed) return;
        Snapshot();
        switch (Tool)
        {
            case SketchTool.Rectangle: MakeRectangle(); break;
            case SketchTool.CenterRectangle: MakeCenterRectangle(); break;
            case SketchTool.Circle: MakeCircle(); break;
            case SketchTool.CenterArc: MakeCenterArc(); break;
            case SketchTool.ThreePointArc: MakeThreePointArc(); break;
            case SketchTool.TangentArc: MakeTangentArc(); break;
            case SketchTool.Polygon: MakePolygon(); break;
            case SketchTool.Slot: MakeSlot(); break;
        }
        _clicks.Clear(); _clickSnaps.Clear();
        Resolve();
    }

    void LineClick(Snap s)
    {
        if (_clicks.Count == 0)
        {
            _clicks.Add(s.P); _clickSnaps.Add(s);
            return;
        }
        var start = _clicks[^1];
        if (Vec2.Distance(start, s.P) < Px * 2) return;
        Snapshot();
        int a = _chainPoint >= 0 ? _chainPoint : ResolvePoint(_clickSnaps[^1]);
        if (_chainStartPoint < 0) _chainStartPoint = a;
        int b = ResolvePoint(s);
        if (a == b) return;
        var l = Sk.Add(new SkLine { P1 = a, P2 = b, Construction = Tool == SketchTool.Centerline });
        if (s.H || s.V)
        {
            // skipped when the ends are already held level/plumb (e.g. both on fixed model corners)
            var hv = new SkConstraint { Id = Sk.NewId(), Type = s.H ? CType.Horizontal : CType.Vertical, Refs = new[] { l.Id } };
            if (!SketchSolver.WouldOverDefine(Sk, hv)) Sk.Constraints.Add(hv);
        }
        Resolve();
        _clicks.Add(Sk.P(b)); _clickSnaps.Add(new Snap(SnapKind.Point, Sk.P(b), b));
        _chainPoint = b;
        // closing the loop or connecting to existing geometry ends the chain
        if (Tool == SketchTool.Centerline || (s.Kind is SnapKind.Point or SnapKind.Origin && b == _chainStartPoint)) EndChain();
    }

    void EndChain()
    {
        _clicks.Clear(); _clickSnaps.Clear(); _chainPoint = -1; _chainStartPoint = -1;
        _vp.Redraw();
    }

    void MakeRectangle()
    {
        var p1 = _clicks[0]; var p3 = _clicks[1];
        if (Math.Abs(p1.X - p3.X) < 1e-9 || Math.Abs(p1.Y - p3.Y) < 1e-9) return;
        int a = ResolvePoint(_clickSnaps[0]);
        int c = ResolvePoint(_clickSnaps[1]);
        int b = Sk.AddPoint(new Vec2(p3.X, p1.Y)).Id;
        int d = Sk.AddPoint(new Vec2(p1.X, p3.Y)).Id;
        var l1 = Sk.Add(new SkLine { P1 = a, P2 = b }); var l2 = Sk.Add(new SkLine { P1 = b, P2 = c });
        var l3 = Sk.Add(new SkLine { P1 = c, P2 = d }); var l4 = Sk.Add(new SkLine { P1 = d, P2 = a });
        Sk.AddConstraint(CType.Horizontal, l1.Id); Sk.AddConstraint(CType.Horizontal, l3.Id);
        Sk.AddConstraint(CType.Vertical, l2.Id); Sk.AddConstraint(CType.Vertical, l4.Id);
    }

    void MakeCenterRectangle()
    {
        var c = _clicks[0]; var p = _clicks[1];
        var dx = p.X - c.X; var dy = p.Y - c.Y;
        if (Math.Abs(dx) < 1e-9 || Math.Abs(dy) < 1e-9) return;
        int center = ResolvePoint(_clickSnaps[0]);
        int a = Sk.AddPoint(new Vec2(c.X - dx, c.Y - dy)).Id;
        int b = Sk.AddPoint(new Vec2(c.X + dx, c.Y - dy)).Id;
        int cc = ResolvePoint(_clickSnaps[1]);
        int d = Sk.AddPoint(new Vec2(c.X - dx, c.Y + dy)).Id;
        var l1 = Sk.Add(new SkLine { P1 = a, P2 = b }); var l2 = Sk.Add(new SkLine { P1 = b, P2 = cc });
        var l3 = Sk.Add(new SkLine { P1 = cc, P2 = d }); var l4 = Sk.Add(new SkLine { P1 = d, P2 = a });
        Sk.AddConstraint(CType.Horizontal, l1.Id); Sk.AddConstraint(CType.Horizontal, l3.Id);
        Sk.AddConstraint(CType.Vertical, l2.Id); Sk.AddConstraint(CType.Vertical, l4.Id);
        var d1 = Sk.Add(new SkLine { P1 = a, P2 = cc, Construction = true });
        var d2 = Sk.Add(new SkLine { P1 = b, P2 = d, Construction = true });
        Sk.AddConstraint(CType.Midpoint, center, d1.Id);
        Sk.AddConstraint(CType.Midpoint, center, d2.Id);
    }

    void MakeCircle()
    {
        var r = Vec2.Distance(_clicks[0], _clicks[1]);
        if (r < 1e-9) return;
        int c = ResolvePoint(_clickSnaps[0]);
        var ci = Sk.Add(new SkCircle { Center = c, R = r });
        if (_clickSnaps[1].Kind is SnapKind.Point or SnapKind.Origin) Sk.AddConstraint(CType.PointOnCircle, _clickSnaps[1].Id, ci.Id);
    }

    void MakeCenterArc()
    {
        var c = _clicks[0]; var s = _clicks[1]; var e = _clicks[2];
        var r = Vec2.Distance(c, s);
        if (r < 1e-9) return;
        e = c + (e - c).Normalized * r;
        int ci = ResolvePoint(_clickSnaps[0]);
        int si = ResolvePoint(_clickSnaps[1]);
        int ei = _clickSnaps[2].Kind is SnapKind.Point or SnapKind.Origin ? _clickSnaps[2].Id : Sk.AddPoint(e).Id;
        if (_arcSweep >= 0) Sk.Add(new SkArc { Center = ci, Start = si, End = ei });
        else Sk.Add(new SkArc { Center = ci, Start = ei, End = si });
    }

    static Vec2? Circumcenter(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return null;
        var ux = ((a.LengthSq) * (b.Y - c.Y) + (b.LengthSq) * (c.Y - a.Y) + (c.LengthSq) * (a.Y - b.Y)) / d;
        var uy = ((a.LengthSq) * (c.X - b.X) + (b.LengthSq) * (a.X - c.X) + (c.LengthSq) * (b.X - a.X)) / d;
        return new Vec2(ux, uy);
    }

    void MakeThreePointArc()
    {
        var s = _clicks[0]; var e = _clicks[1]; var m = _clicks[2];
        var cc = Circumcenter(s, e, m);
        if (cc == null) return;
        int si = ResolvePoint(_clickSnaps[0]);
        int ei = ResolvePoint(_clickSnaps[1]);
        var c = Sk.AddPoint(cc.Value).Id;
        // is m on the ccw arc from s to e?
        bool ccw = Vec2.Cross(e - s, m - s) < 0;
        Sk.Add(ccw ? new SkArc { Center = c, Start = si, End = ei } : new SkArc { Center = c, Start = ei, End = si });
    }

    /// <summary>Tangent direction at an end point (pointing away from the existing entity).</summary>
    (int point, Vec2 dir)? TangentAt(Snap s)
    {
        if (s.Kind != SnapKind.Point) return null;
        foreach (var e in Sk.EntitiesUsing(s.Id))
        {
            if (e is SkLine l)
            {
                var other = l.P1 == s.Id ? l.P2 : l.P1;
                return (s.Id, (Sk.P(s.Id) - Sk.P(other)).Normalized);
            }
            if (e is SkArc a && (a.Start == s.Id || a.End == s.Id))
            {
                var radial = (Sk.P(s.Id) - Sk.P(a.Center)).Normalized;
                // arc runs ccw from Start to End: tangent at End is +perp, at Start is -perp
                return (s.Id, a.End == s.Id ? radial.Perp : -radial.Perp);
            }
        }
        return null;
    }

    void MakeTangentArc()
    {
        var t = TangentAt(_clickSnaps[0]);
        if (t == null) return;
        var (pid, dir) = t.Value;
        var s = Sk.P(pid); var e = _clicks[1];
        // center lies on the normal at s, equidistant from s and e
        var n = dir.Perp;
        var se = e - s;
        var denom = 2 * Vec2.Dot(n, se);
        if (Math.Abs(denom) < 1e-12) return;
        var k = se.LengthSq / denom;
        var c = s + n * k;
        int ei = ResolvePoint(_clickSnaps[1]);
        var ci = Sk.AddPoint(c).Id;
        // ccw if turning left
        var arc = k > 0 ? new SkArc { Center = ci, Start = pid, End = ei } : new SkArc { Center = ci, Start = ei, End = pid };
        Sk.Add(arc);
        var host = Sk.EntitiesUsing(pid).First(x => x != arc);
        var tg = Sk.AddConstraint(CType.Tangent, host.Id, arc.Id);
        SketchSolver.CaptureSign(Sk, tg);
    }

    void MakePolygon()
    {
        var c = _clicks[0]; var v = _clicks[1];
        var r = Vec2.Distance(c, v);
        if (r < 1e-9) return;
        int n = Math.Clamp(PolygonSides, 3, 64);
        int ci = ResolvePoint(_clickSnaps[0]);
        var circ = Sk.Add(new SkCircle { Center = ci, R = r, Construction = true });
        var a0 = (v - c).Angle;
        var ids = new List<int>();
        for (int i = 0; i < n; i++) ids.Add(Sk.AddPoint(c + Vec2.FromAngle(a0 + 2 * Math.PI * i / n, r)).Id);
        var lines = new List<SkLine>();
        for (int i = 0; i < n; i++) lines.Add(Sk.Add(new SkLine { P1 = ids[i], P2 = ids[(i + 1) % n] }));
        foreach (var id in ids) Sk.AddConstraint(CType.PointOnCircle, id, circ.Id);
        for (int i = 1; i < n; i++) Sk.AddConstraint(CType.Equal, lines[0].Id, lines[i].Id);
    }

    void MakeSlot()
    {
        var a = _clicks[0]; var b = _clicks[1]; var w = _clicks[2];
        var axis = b - a; var len = axis.Length;
        if (len < 1e-9) return;
        var u = axis / len; var n = u.Perp;
        var r = Math.Abs(Vec2.Dot(w - a, n));
        if (r < 1e-9) return;
        int ca = ResolvePoint(_clickSnaps[0]); int cb = ResolvePoint(_clickSnaps[1]);
        var axisLine = Sk.Add(new SkLine { P1 = ca, P2 = cb, Construction = true });
        int p1 = Sk.AddPoint(a + n * r).Id, p2 = Sk.AddPoint(b + n * r).Id, p3 = Sk.AddPoint(b - n * r).Id, p4 = Sk.AddPoint(a - n * r).Id;
        var top = Sk.Add(new SkLine { P1 = p1, P2 = p2 });
        var bot = Sk.Add(new SkLine { P1 = p3, P2 = p4 });
        var arcB = Sk.Add(new SkArc { Center = cb, Start = p3, End = p2 });
        var arcA = Sk.Add(new SkArc { Center = ca, Start = p1, End = p4 });
        foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
        {
            var t = Sk.AddConstraint(CType.Tangent, l.Id, arc.Id);
            SketchSolver.CaptureSign(Sk, t);
        }
        // sides parallel to the centerline. One relation is enough: with the four tangencies it makes the end radii
        // equal, so the other side follows (a second Parallel would be redundant)
        Sk.AddConstraint(CType.Parallel, top.Id, axisLine.Id);
    }

    // ---- trim ----

    void TrimAt(PointerInfo p)
    {
        var h = HitTest(p.X, p.Y, includeDims: false);
        if (h is not { Kind: SkRefKind.Entity } || Sk.Entity(h.Value.Id) is not SkLine line) { Message?.Invoke("Trim currently supports lines: click the segment to remove."); return; }
        var a = Sk.P(line.P1); var b = Sk.P(line.P2);
        var m = SketchPos(p);
        var dir = b - a; var len2 = dir.LengthSq;
        var tm = Vec2.Dot(m - a, dir) / len2;
        // intersections with other entities along the line
        var ts = new List<double> { 0, 1 };
        foreach (var e in Sk.Entities)
        {
            if (e.Id == line.Id || e.Construction) continue;
            foreach (var (c, d) in SketchDrawing.Segments2D(Sk, e))
            {
                var den = Vec2.Cross(dir, d - c);
                if (Math.Abs(den) < 1e-12) continue;
                var t = Vec2.Cross(c - a, d - c) / den;
                var u = Vec2.Cross(c - a, dir) / den;
                if (t > 1e-6 && t < 1 - 1e-6 && u >= -1e-9 && u <= 1 + 1e-9) ts.Add(t);
            }
        }
        ts.Sort();
        double lo = 0, hi = 1;
        foreach (var t in ts) { if (t <= tm) lo = t; else { hi = t; break; } }
        Snapshot();
        if (lo <= 1e-9 && hi >= 1 - 1e-9) { Sk.DeleteEntity(line.Id); }
        else if (lo <= 1e-9) { var np = Sk.AddPoint(a + dir * hi); ReplaceEnd(line, line.P1, np.Id); }
        else if (hi >= 1 - 1e-9) { var np = Sk.AddPoint(a + dir * lo); ReplaceEnd(line, line.P2, np.Id); }
        else
        {
            var p1 = Sk.AddPoint(a + dir * lo); var p2 = Sk.AddPoint(a + dir * hi);
            var oldEnd = line.P2;
            line.P2 = p1.Id;
            Sk.Add(new SkLine { P1 = p2.Id, P2 = oldEnd, Construction = line.Construction });
        }
        // dimensions on the trimmed line no longer make sense
        Sk.Constraints.RemoveAll(c => c.IsDimension && c.Refs.Length == 1 && c.Refs[0] == line.Id);
        Sk.Cleanup();
        Resolve();
    }

    void ReplaceEnd(SkLine l, int oldPoint, int newPoint)
    {
        if (l.P1 == oldPoint) l.P1 = newPoint; else l.P2 = newPoint;
        Sk.Constraints.RemoveAll(c => c.Refs.Contains(oldPoint) && c.Refs.Contains(l.Id));
    }

    // ---- dragging ----

    void BeginDrag()
    {
        _dragging = true;
        _dragOrigins = null; _dragCircle = -1; _dragLabel = null;
        var h = HitTest(_downScreen.X, _downScreen.Y);
        if (h == null)
        {
            _boxSel = new Rect(_downScreen, _downScreen);
            return;
        }
        Snapshot();
        switch (h.Value.Kind)
        {
            case SkRefKind.Point:
                _dragOrigins = new() { [h.Value.Id] = Sk.P(h.Value.Id) };
                break;
            case SkRefKind.Entity:
                {
                    var e = Sk.Entity(h.Value.Id)!;
                    if (e is SkCircle c)
                    {
                        // dragging the circumference changes the radius
                        _dragCircle = c.Id;
                        break;
                    }
                    _dragOrigins = e.PointIds.Distinct().ToDictionary(id => id, id => Sk.P(id));
                    break;
                }
            case SkRefKind.Constraint:
                Snapshot(); // a moved label is a change (undoable, saved)
                _dragLabel = h;
                break;
        }
    }

    void UpdateDrag(PointerInfo p)
    {
        var m = SketchPos(p);
        if (_boxSel != null)
        {
            _boxSel = new Rect(_downScreen, new Point(p.X, p.Y)).Normalize();
            _vp.Redraw();
            return;
        }
        if (_dragLabel != null && Sk.Constraint(_dragLabel.Value.Id) is { } dc)
        {
            var anchor = DimAnchor(dc);
            dc.LabelX = m.X - anchor.X; dc.LabelY = m.Y - anchor.Y;
            dc.LabelPlaced = true;
            UpdateOverlay(_vp);
            _vp.Redraw();
            return;
        }
        if (_dragCircle >= 0 && Sk.Entity(_dragCircle) is SkCircle circ)
        {
            circ.R = Math.Max(1e-6, Vec2.Distance(Sk.P(circ.Center), m));
            Resolve();
            _vp.Redraw();
            return;
        }
        if (_dragOrigins != null)
        {
            var delta = m - _downSketch;
            var targets = _dragOrigins.ToDictionary(kv => kv.Key, kv => kv.Value + delta);
            Resolve(targets);
            _vp.Redraw();
        }
    }

    void EndDrag()
    {
        if (_boxSel is { } r)
        {
            if (!(_vp.Selection?.Count > 0)) { }
            Selected.Clear();
            foreach (var e in Sk.Entities)
            {
                var all = e.PointIds.All(id => r.Contains(Screen(Sk.P(id))));
                if (e is SkCircle c)
                {
                    var cs = Screen(Sk.P(c.Center)); var rr = c.R / Px;
                    all = r.Contains(new Point(cs.X - rr, cs.Y - rr)) && r.Contains(new Point(cs.X + rr, cs.Y + rr));
                }
                if (all) Selected.Add(new SkRef(SkRefKind.Entity, e.Id));
            }
            // dimensions whose label lies wholly inside the box
            foreach (var (id, label) in _labels)
                if (label.IsVisible && r.Contains(label.Bounds)) Selected.Add(new SkRef(SkRefKind.Constraint, id));
            SelectionChanged?.Invoke();
        }
        _dragging = false; _dragOrigins = null; _dragCircle = -1; _dragLabel = null; _boxSel = null;
        Resolve();
    }

    // ---- smart dimension ----

    void DimClick(PointerInfo p)
    {
        var h = HitTest(p.X, p.Y, includeDims: false);
        if (h != null && h.Value.Kind == SkRefKind.Entity && Sk.Entity(h.Value.Id) is SkPointEntity pe) h = new SkRef(SkRefKind.Point, pe.P);
        // model edges / vertices act as references when no sketch geometry is under the cursor
        bool modelHit = h == null && _dimPicks.Count < 2 && ModelCandidate(p.X, p.Y) != null;
        if (_dimPreview != null && ((h == null && !modelHit) || _dimPicks.Count >= 2))
        {
            PlaceDimension(SketchPos(p));
            return;
        }
        if (h == null && modelHit)
        {
            Snapshot();
            h = CreateModelReference(p.X, p.Y);
            Resolve();
        }
        if (h == null) return;
        if (h.Value.Kind == SkRefKind.Constraint) return;
        _dimPicks.Add(h.Value);
        UpdateDimPreview();
        _vp.Redraw();
    }

    /// <summary>Model vertex or edge under the cursor (vertices win).</summary>
    (Body body, TopoEdge edge, bool vertex, Vec3 point)? ModelCandidate(double x, double y)
    {
        if (_vp.PickVertex(x, y) is { } v) return (v.body, v.edge, true, v.point);
        var e = _vp.Pick(x, y, SelFilter.Edge);
        if (e?.Edge == null) return null;
        var body = _vp.Bodies.FirstOrDefault(b => b.Id == e.BodyId);
        if (body == null || e.EdgeIndex < 0 || e.EdgeIndex >= body.Solid.Topology.Edges.Count) return null;
        return (body, body.Solid.Topology.Edges[e.EdgeIndex], false, e.Point);
    }

    /// <summary>Projects the model edge/vertex under the cursor into the sketch as reference geometry.</summary>
    SkRef? CreateModelReference(double x, double y)
    {
        if (ModelCandidate(x, y) is not { } m) return null;
        var ent = SketchReferences.Create(Sk, Frame, m.edge, m.body.Id, m.vertex, m.point);
        if (ent == null) { Message?.Invoke("That edge is seen end-on from this sketch plane."); return null; }
        if (ent is SkPointEntity rp) return new SkRef(SkRefKind.Point, rp.P);
        if (ent is SkCircle rc && Vec2.Distance(Sk.P(rc.Center), SketchPos(new PointerInfo(x, y, default, 0))) < rc.R * 0.25)
            return new SkRef(SkRefKind.Point, rc.Center);
        return new SkRef(SkRefKind.Entity, ent.Id);
    }

    void UpdateDimPreview()
    {
        _dimPreview = BuildDim(_mouse);
        UpdateOverlay(_vp);
    }

    SkConstraint? BuildDim(Vec2 mouse)
    {
        if (_dimPicks.Count == 0) return null;
        var p0 = _dimPicks[0];
        var e0 = p0.Kind == SkRefKind.Entity ? Sk.Entity(p0.Id) : null;
        SkConstraint? c = null;
        if (_dimPicks.Count == 1)
        {
            switch (e0)
            {
                case SkCircle ci: c = new SkConstraint { Type = CType.Diameter, Refs = new[] { ci.Id } }; break;
                case SkArc ar: c = new SkConstraint { Type = CType.Radius, Refs = new[] { ar.Id } }; break;
                case SkLine l: c = new SkConstraint { Type = TwoPointType(Sk.P(l.P1), Sk.P(l.P2), mouse), Refs = new[] { l.Id } }; break;
            }
        }
        else
        {
            var p1 = _dimPicks[1];
            var e1 = p1.Kind == SkRefKind.Entity ? Sk.Entity(p1.Id) : null;
            int Pt(SkRef r, SkEntity? e) => r.Kind == SkRefKind.Point ? r.Id : e is SkCircle cc ? cc.Center : e is SkArc aa ? aa.Center : -1;
            bool isPt0 = p0.Kind == SkRefKind.Point || e0 is SkCircle or SkArc;
            bool isPt1 = p1.Kind == SkRefKind.Point || e1 is SkCircle or SkArc;
            if (isPt0 && isPt1)
            {
                int a = Pt(p0, e0), b = Pt(p1, e1);
                if (a != b && a >= 0 && b >= 0)
                    c = new SkConstraint { Type = TwoPointType(Sk.P(a), Sk.P(b), mouse), Refs = new[] { a, b } };
            }
            else if (e0 is SkLine l0 && e1 is SkLine l1)
            {
                var d0 = Sk.P(l0.P2) - Sk.P(l0.P1); var d1 = Sk.P(l1.P2) - Sk.P(l1.P1);
                var sin = Math.Abs(Vec2.Cross(d0.Normalized, d1.Normalized));
                c = sin < 1e-6
                    ? new SkConstraint { Type = CType.Distance, Refs = new[] { l0.Id, l1.Id } }
                    : new SkConstraint { Type = CType.Angle, Refs = new[] { l0.Id, l1.Id } };
            }
            else if (isPt0 && e1 is SkLine ln1) c = new SkConstraint { Type = CType.Distance, Refs = new[] { Pt(p0, e0), ln1.Id } };
            else if (isPt1 && e0 is SkLine ln0) c = new SkConstraint { Type = CType.Distance, Refs = new[] { Pt(p1, e1), ln0.Id } };
        }
        if (c == null) return null;
        var anchor = DimAnchor(c);
        c.LabelX = mouse.X - anchor.X; c.LabelY = mouse.Y - anchor.Y;
        if (c.Type == CType.Angle)
        {
            // measure the sector the dimension is being placed in
            var (a1, b1) = LinePts(c.Refs[0]); var (a2, b2) = LinePts(c.Refs[1]);
            c.Flip = SketchSolver.ChooseAngleFlip(b1 - a1, b2 - a2, mouse - anchor);
        }
        c.Value = SketchSolver.Measure(Sk, c);
        return c;
    }

    static CType TwoPointType(Vec2 a, Vec2 b, Vec2 m)
    {
        double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
        bool inX = m.X > minX && m.X < maxX, inY = m.Y > minY && m.Y < maxY;
        if (Math.Abs(a.Y - b.Y) < 1e-9) return CType.Distance;
        if (Math.Abs(a.X - b.X) < 1e-9) return CType.Distance;
        if (inX && !inY) return CType.HDistance;
        if (inY && !inX) return CType.VDistance;
        return CType.Distance;
    }

    void PlaceDimension(Vec2 at)
    {
        var c = BuildDim(at);
        _dimPicks.Clear(); _dimPreview = null;
        if (c == null) { Sk.RemoveUnusedReferences(); return; }
        Snapshot();
        c.Id = Sk.NewId();
        SketchSolver.CaptureSign(Sk, c);
        c.Expr = Expr.Format(c.Value);
        c.Name = "D" + (Sk.Constraints.Count(x => x.IsDimension) + 1);
        c.LabelPlaced = true; // where it was clicked
        if (SketchSolver.WouldOverDefine(Sk, c))
        {
            c.Driven = true;
            Sk.Constraints.Add(c);
            Message?.Invoke("This dimension would over define the sketch, so it was added as a driven (reference) dimension.");
            Resolve();
            return;
        }
        Sk.Constraints.Add(c);
        Resolve();
        OpenModifyBox(c);
    }

    // ---- modify box ----

    public void OpenModifyBox(SkConstraint c)
    {
        if (Overlay == null || c.Driven) return;
        CloseModifyBox(false);
        var pos = Screen(LabelPos(c));
        var tb = new TextBox
        {
            Text = c.Expr ?? Expr.Format(c.Value),
            Width = 120, FontSize = 13,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 120, 220)),
            BorderThickness = new Thickness(2),
        };
        ToolTip.SetTip(tb, "Enter a value or an expression (e.g. 25, W/2, 1in). Enter = OK, Esc = cancel.");
        Canvas.SetLeft(tb, pos.X - 60); Canvas.SetTop(tb, pos.Y - 14);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CloseModifyBox(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseModifyBox(false); e.Handled = true; }
        };
        Overlay.Children.Add(tb);
        _modifyBox = tb; _modifyTarget = c;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tb.Focus();
            tb.SelectAll();
            tb.LostFocus += (_, _) => { if (_modifyBox == tb) CloseModifyBox(true); };
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    void CloseModifyBox(bool apply)
    {
        if (_modifyBox == null) return;
        var tb = _modifyBox; var c = _modifyTarget!;
        _modifyBox = null; _modifyTarget = null;
        Overlay?.Children.Remove(tb);
        if (!apply) return;
        SetDimensionValue(c, tb.Text);
        _vp.Focus();
    }

    /// <summary>Applies a typed value/expression to a dimension (one undo step). False when invalid or unchanged.</summary>
    public bool SetDimensionValue(SkConstraint c, string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text == c.Expr) return false;
        if (!Expr.TryEval(text, Lookup, out var v, out var err)) { Message?.Invoke("Invalid value: " + err); return false; }
        if (v <= 0 && c.Type != CType.Angle && c.Type != CType.HDistance && c.Type != CType.VDistance) { Message?.Invoke("Value must be greater than zero."); return false; }
        Snapshot();
        c.Expr = text; c.Value = v;
        Resolve();
        if (Status.Status == SketchStatus.OverDefined) Message?.Invoke("The sketch cannot be solved with that value.");
        _vp.Redraw();
        return true;
    }

    public bool IsEditingDimension => _modifyBox != null;

    // ---------------------------------------------------------------------------------------
    // drawing

    Rgba ColorOfPoint(int id) => Status.Status == SketchStatus.OverDefined ? Over : Status.FreePoints.Contains(id) ? Under : Full;

    Rgba ColorOf(SkEntity e)
    {
        var sel = Selected.Contains(new SkRef(SkRefKind.Entity, e.Id));
        if (_hover is { Kind: SkRefKind.Entity } h && h.Id == e.Id) return ViewportControl.HoverColor;
        if (sel || _dimPicks.Contains(new SkRef(SkRefKind.Entity, e.Id))) return ViewportControl.SelectColor;
        if (Status.Status == SketchStatus.OverDefined) return Over;
        bool free = e.PointIds.Any(Status.FreePoints.Contains) || (e is SkCircle c && Status.FreeRadii.Contains(c.Id));
        return free ? Under : Full;
    }

    public void Draw(ViewportControl vp, DynamicGeometry g)
    {
        var px = Px; var f = Frame;
        // sketch origin (red)
        var o = Sketch.OriginId;
        var oc = new Rgba(0.85f, 0.15f, 0.15f);
        g.Line(f.ToWorld(Sk.P(o)), f.ToWorld(Sk.P(o) + new Vec2(px * 25, 0)), oc, top: true);
        g.Line(f.ToWorld(Sk.P(o)), f.ToWorld(Sk.P(o) + new Vec2(0, px * 25)), oc, top: true);

        if (_hoverVertex is { } hv) SketchDrawing.PointMark(g, Frame, Frame.ToLocal(hv), ViewportControl.HoverColor, px, 4.5);
        foreach (var e in Sk.Entities)
        {
            var c = ColorOf(e);
            if (e.IsReference)
            {
                var rcol = c == Under || c == Full ? RefColor : c;
                if (e is SkPointEntity rpe) { SketchDrawing.PointMark(g, f, Sk.P(rpe.P), rcol, px, 3); continue; }
                foreach (var (a, b) in SketchDrawing.Segments2D(Sk, e)) g.Dashed(f.ToWorld(a), f.ToWorld(b), rcol, px * 3, top: true);
                continue;
            }
            if (e is SkPointEntity pe) { SketchDrawing.PointMark(g, f, Sk.P(pe.P), c, px, 3.5); continue; }
            foreach (var (a, b) in SketchDrawing.Segments2D(Sk, e))
            {
                if (e.Construction) g.Dashed(f.ToWorld(a), f.ToWorld(b), e == null ? c : (c == Under || c == Full ? Constr : c), px * 6, top: true);
                else g.Line(f.ToWorld(a), f.ToWorld(b), c, top: true);
            }
        }
        // end points, and points only relations use (e.g. a center rectangle's center, where its diagonals meet):
        // shown, as they can be snapped to and dimensioned. Point entities have their own mark (above).
        var shown = Sk.Entities.Where(e => e is not SkPointEntity).SelectMany(e => e.PointIds)
            .Concat(Sk.Constraints.SelectMany(c => c.Refs)).ToHashSet();
        var ownMark = Sk.Entities.OfType<SkPointEntity>().Select(e => e.P).ToHashSet();
        foreach (var p in Sk.Points)
        {
            if (p.Id == o || !shown.Contains(p.Id) || ownMark.Contains(p.Id)) continue;
            var c = _hover is { Kind: SkRefKind.Point } h && h.Id == p.Id ? ViewportControl.HoverColor
                : Selected.Contains(new SkRef(SkRefKind.Point, p.Id)) || _dimPicks.Contains(new SkRef(SkRefKind.Point, p.Id)) ? ViewportControl.SelectColor
                : ColorOfPoint(p.Id);
            SketchDrawing.PointMark(g, f, p.P, c, px, 2.2);
        }
        // dimensions
        // a selected or hovered dimension is drawn whole (lines, extension lines, arrows) in its label's color, so
        // it is plain where it leads
        foreach (var c in Sk.Constraints.Where(c => c.IsDimension))
        {
            var sel = Selected.Contains(new SkRef(SkRefKind.Constraint, c.Id));
            var hov = _hover is { Kind: SkRefKind.Constraint } dh && dh.Id == c.Id;
            DrawDimension(g, c, sel ? DimSelectedColor : hov ? ViewportControl.HoverColor : c.Driven ? new Rgba(0.45f, 0.45f, 0.5f) : DimColor, ends: sel);
        }
        if (_dimPreview != null) DrawDimension(g, _dimPreview, ViewportControl.HoverColor);

        // tool preview
        DrawPreview(g);

        // box selection
        if (_boxSel is { } r)
        {
            var c = new Rgba(0.2f, 0.5f, 1f, 0.9f);
            var pts = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }
                .Select(s => vp.PlanePoint(Frame, s.X, s.Y) ?? Vec2.Zero).Select(Frame.ToWorld).ToArray();
            for (int i = 0; i < 4; i++) g.Line(pts[i], pts[(i + 1) % 4], c, top: true);
        }
    }

    void DrawPreview(DynamicGeometry g)
    {
        if (Tool is SketchTool.Select or SketchTool.SmartDimension or SketchTool.Trim or SketchTool.Fillet) return;
        var f = Frame; var px = Px;
        var pc = new Rgba(0.95f, 0.55f, 0.1f);
        var m = _snap.P;
        // snap marker
        if (_snap.Kind != SnapKind.None)
        {
            bool model = _snap.Kind is SnapKind.ModelVertex or SnapKind.ModelEdge or SnapKind.ModelMidpoint or SnapKind.ModelCenter;
            SketchDrawing.PointMark(g, f, m, model ? new Rgba(0.65f, 0.25f, 0.85f) : new Rgba(1f, 0.6f, 0f), px, 4);
            // highlight the model edge the relation will refer to
            if (_snap.Model is { } mh)
                foreach (var seg in mh.Edge.Segs) g.Line(seg.A, seg.B, new Rgba(0.65f, 0.25f, 0.85f), top: true);
        }
        if (_snap.AlignX is { } iax) g.Dashed(f.ToWorld(iax), f.ToWorld(m), new Rgba(0.35f, 0.55f, 0.95f), px * 4, top: true);
        if (_snap.AlignY is { } iay) g.Dashed(f.ToWorld(iay), f.ToWorld(m), new Rgba(0.35f, 0.55f, 0.95f), px * 4, top: true);
        if (_clicks.Count == 0) return;
        void L(Vec2 a, Vec2 b, bool dashed = false) { if (dashed) g.Dashed(f.ToWorld(a), f.ToWorld(b), pc, px * 5, top: true); else g.Line(f.ToWorld(a), f.ToWorld(b), pc, top: true); }
        void Arc(Vec2 c, double r, double a0, double a1)
        {
            Vec2? prev = null;
            foreach (var p in SketchDrawing.ArcPoints(c, r, a0, a1)) { if (prev != null) L(prev.Value, p); prev = p; }
        }
        var c0 = _clicks[0];
        switch (Tool)
        {
            case SketchTool.Line:
            case SketchTool.Centerline:
                L(_clicks[^1], m, Tool == SketchTool.Centerline);
                if (_snap.H || _snap.V) L(_clicks[^1] + (m - _clicks[^1]) * -0.3, m + (m - _clicks[^1]) * 0.3, true);
                break;
            case SketchTool.Rectangle:
                L(c0, new Vec2(m.X, c0.Y)); L(new Vec2(m.X, c0.Y), m); L(m, new Vec2(c0.X, m.Y)); L(new Vec2(c0.X, m.Y), c0);
                break;
            case SketchTool.CenterRectangle:
                {
                    var d = m - c0;
                    var a = c0 - d; L(a, new Vec2(m.X, a.Y)); L(new Vec2(m.X, a.Y), m); L(m, new Vec2(a.X, m.Y)); L(new Vec2(a.X, m.Y), a);
                    // both construction diagonals and the center point, as the rectangle is made
                    L(a, m, true); L(new Vec2(m.X, a.Y), new Vec2(a.X, m.Y), true);
                    SketchDrawing.PointMark(g, f, c0, pc, px, 2.2);
                    break;
                }
            case SketchTool.Circle:
                Arc(c0, Vec2.Distance(c0, m), 0, 2 * Math.PI);
                break;
            case SketchTool.Polygon:
                {
                    var r = Vec2.Distance(c0, m); var a0 = (m - c0).Angle; int n = Math.Clamp(PolygonSides, 3, 64);
                    for (int i = 0; i < n; i++) L(c0 + Vec2.FromAngle(a0 + 2 * Math.PI * i / n, r), c0 + Vec2.FromAngle(a0 + 2 * Math.PI * (i + 1) / n, r));
                    break;
                }
            case SketchTool.CenterArc:
                if (_clicks.Count == 1) L(c0, m, true);
                else
                {
                    var r = Vec2.Distance(c0, _clicks[1]);
                    var a0 = (_clicks[1] - c0).Angle; var a1 = (m - c0).Angle;
                    if (_arcSweep >= 0) Arc(c0, r, a0, a1); else Arc(c0, r, a1, a0);
                }
                break;
            case SketchTool.ThreePointArc:
                if (_clicks.Count == 1) L(c0, m, true);
                else
                {
                    var s = _clicks[0]; var e = _clicks[1];
                    var cc = Circumcenter(s, e, m);
                    if (cc != null)
                    {
                        var r = Vec2.Distance(cc.Value, s);
                        bool ccw = Vec2.Cross(e - s, m - s) < 0;
                        if (ccw) Arc(cc.Value, r, (s - cc.Value).Angle, (e - cc.Value).Angle);
                        else Arc(cc.Value, r, (e - cc.Value).Angle, (s - cc.Value).Angle);
                    }
                }
                break;
            case SketchTool.TangentArc:
                {
                    var t = TangentAt(_clickSnaps[0]);
                    if (t == null) break;
                    var (pid, dir) = t.Value; var s = Sk.P(pid);
                    var n = dir.Perp; var se = m - s; var den = 2 * Vec2.Dot(n, se);
                    if (Math.Abs(den) < 1e-12) { L(s, m); break; }
                    var k = se.LengthSq / den; var c = s + n * k; var r = Math.Abs(k);
                    if (k > 0) Arc(c, r, (s - c).Angle, (m - c).Angle); else Arc(c, r, (m - c).Angle, (s - c).Angle);
                    break;
                }
            case SketchTool.Slot:
                if (_clicks.Count == 1) L(c0, m, true);
                else
                {
                    var a = _clicks[0]; var b = _clicks[1]; var u = (b - a).Normalized; var n = u.Perp;
                    var r = Math.Abs(Vec2.Dot(m - a, n));
                    L(a + n * r, b + n * r); L(a - n * r, b - n * r);
                    Arc(b, r, (n * -1).Angle, n.Angle); Arc(a, r, n.Angle, (n * -1).Angle);
                }
                break;
        }
    }

    // ---- dimension geometry ----

    /// <summary>Anchor point of a dimension (label offsets are relative to it).</summary>
    public Vec2 DimAnchor(SkConstraint c)
    {
        try
        {
            var r = c.Refs;
            switch (c.Type)
            {
                case CType.Radius:
                case CType.Diameter:
                    {
                        var e = Sk.Entity(r[0]);
                        return e is SkCircle ci ? Sk.P(ci.Center) : e is SkArc a ? Sk.P(a.Center) : Vec2.Zero;
                    }
                case CType.Angle:
                    {
                        var (a1, b1) = LinePts(r[0]); var (a2, b2) = LinePts(r[1]);
                        return Intersect(a1, b1, a2, b2) ?? (a1 + a2) * 0.5;
                    }
                default:
                    {
                        var (a, b) = DimPoints(c);
                        return (a + b) * 0.5;
                    }
            }
        }
        catch { return Vec2.Zero; }
    }

    (Vec2, Vec2) LinePts(int id) { var l = (SkLine)Sk.Entity(id)!; return (Sk.P(l.P1), Sk.P(l.P2)); }

    static Vec2? Intersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var r = b - a; var s = d - c; var den = Vec2.Cross(r, s);
        if (Math.Abs(den) < 1e-12) return null;
        var t = Vec2.Cross(c - a, s) / den;
        return a + r * t;
    }

    /// <summary>The two measured points of a linear dimension.</summary>
    (Vec2 a, Vec2 b) DimPoints(SkConstraint c)
    {
        if (c.Type == CType.Offset) return OffsetPoints(c);
        var r = c.Refs;
        if (r.Length == 1) return LinePts(r[0]);
        bool p0 = Sk.Point(r[0]) != null, p1 = Sk.Point(r[1]) != null;
        if (p0 && p1) return (Sk.P(r[0]), Sk.P(r[1]));
        if (Sk.Entity(r[0]) is SkLine && Sk.Entity(r[1]) is SkLine)
        {
            var (a, b) = LinePts(r[0]); var (p, q) = LinePts(r[1]);
            var mid = (p + q) * 0.5;
            var d = (b - a).Normalized;
            var foot = a + d * Vec2.Dot(mid - a, d);
            return (foot, mid);
        }
        {
            var pi = p0 ? 0 : 1; var pt = Sk.P(r[pi]); var o = Sk.Entity(r[1 - pi]);
            if (o is SkLine) { var (a, b) = LinePts(o.Id); var d = (b - a).Normalized; return (pt, a + d * Vec2.Dot(pt - a, d)); }
            if (o is SkCircle ci) return (pt, Sk.P(ci.Center));
            if (o is SkArc ar) return (pt, Sk.P(ar.Center));
        }
        return (Vec2.Zero, Vec2.Zero);
    }

    /// <summary>The lines a dimension is drawn with (dimension, extension and leader lines, the angle arc).</summary>
    List<(Vec2 a, Vec2 b)> DimLines(SkConstraint c)
    {
        var lines = new List<(Vec2, Vec2)>();
        DrawDimension(null, c, default, collect: lines);
        return lines;
    }

    /// <param name="ends">mark the points the dimension measures (a selected one), so where it stops shows even
    /// with another dimension drawn over it</param>
    /// <param name="collect">instead of drawing, gather the lines (for hit testing)</param>
    void DrawDimension(DynamicGeometry? g, SkConstraint c, Rgba col, bool ends = false, List<(Vec2, Vec2)>? collect = null)
    {
        var f = Frame; var px = Px;
        void L(Vec2 a, Vec2 b) { if (collect != null) collect.Add((a, b)); else g!.Line(f.ToWorld(a), f.ToWorld(b), col, top: true); }
        void End(Vec2 p) { if (ends && g != null) SketchDrawing.PointMark(g, f, p, col, px, 3.5); }
        void Arrow(Vec2 tip, Vec2 dir)
        {
            if (g == null) return;
            dir = dir.Normalized; var n = dir.Perp; var s = px * 9; var w = px * 3;
            g.Tri(f.ToWorld(tip), f.ToWorld(tip - dir * s + n * w), f.ToWorld(tip - dir * s - n * w), col, top: true);
        }
        try
        {
            var anchor = DimAnchor(c);
            var label = LabelPos(c);
            switch (c.Type)
            {
                case CType.Distance:
                case CType.HDistance:
                case CType.VDistance:
                case CType.Offset:
                    {
                        var (a, b) = DimPoints(c);
                        Vec2 dir, nrm;
                        if (c.Type == CType.HDistance) { dir = new Vec2(1, 0); nrm = new Vec2(0, 1); }
                        else if (c.Type == CType.VDistance) { dir = new Vec2(0, 1); nrm = new Vec2(1, 0); }
                        else
                        {
                            dir = (b - a).LengthSq > 1e-18 ? (b - a).Normalized : new Vec2(1, 0);
                            nrm = dir.Perp;
                        }
                        var off = Vec2.Dot(label - a, nrm);
                        var offB = Vec2.Dot(label - b, nrm);
                        var da = a + nrm * off; var db = b + nrm * offB;
                        var ext = Math.Sign(off == 0 ? 1 : off) * px * 4;
                        L(a + nrm * Math.Sign(off) * px * 2, da + nrm * ext);
                        L(b + nrm * Math.Sign(offB) * px * 2, db + nrm * Math.Sign(offB) * px * 4);
                        L(da, db);
                        // extend dim line to the label if it sits outside
                        var t = Vec2.Dot(label - da, dir); var tb = Vec2.Dot(db - da, dir);
                        if (t < Math.Min(0, tb)) L(da, da + dir * t);
                        if (t > Math.Max(0, tb)) L(db, da + dir * t);
                        if ((db - da).Length > px * 2) { Arrow(da, da - db); Arrow(db, db - da); }
                        End(a); End(b);
                        break;
                    }
                case CType.Radius:
                case CType.Diameter:
                    {
                        var e = Sk.Entity(c.Refs[0])!;
                        var r = Sk.Radius(e);
                        var u = (label - anchor).LengthSq > 1e-18 ? (label - anchor).Normalized : new Vec2(1, 0);
                        var onCirc = anchor + u * r;
                        if (c.Type == CType.Diameter)
                        {
                            var other = anchor - u * r;
                            L(other, (label - anchor).Length > r ? label : onCirc);
                            Arrow(onCirc, u); Arrow(other, -u);
                            End(other);
                        }
                        else
                        {
                            L(anchor, (label - anchor).Length > r ? label : onCirc);
                            Arrow(onCirc, u);
                        }
                        End(anchor); End(onCirc);
                        break;
                    }
                case CType.Angle:
                    {
                        var (a1, b1) = LinePts(c.Refs[0]); var (a2, b2) = LinePts(c.Refs[1]);
                        var rr = Math.Max((label - anchor).Length, px * 10);
                        var (r1, r2) = SketchSolver.AngleRays(Sk, c);
                        var d1 = r1.Normalized; var d2 = r2.Normalized;
                        var ang0 = d1.Angle; var ang1 = d2.Angle;
                        var sweep = ang1 - ang0; while (sweep > Math.PI) sweep -= 2 * Math.PI; while (sweep < -Math.PI) sweep += 2 * Math.PI;
                        int n = 24; Vec2? prev = null;
                        for (int i = 0; i <= n; i++)
                        {
                            var p = anchor + Vec2.FromAngle(ang0 + sweep * i / n, rr);
                            if (prev != null) L(prev.Value, p);
                            prev = p;
                        }
                        L(anchor, anchor + d1 * rr * 1.05); L(anchor, anchor + d2 * rr * 1.05);
                        End(anchor); End(anchor + d1 * rr); End(anchor + d2 * rr);
                        break;
                    }
            }
        }
        catch { /* entity deleted mid-operation */ }
    }

    // ---------------------------------------------------------------------------------------
    // overlay labels (dimension values and relation glyphs)

    public Canvas? Overlay { get; set; }
    readonly Dictionary<int, Border> _labels = new();
    readonly List<Border> _glyphs = new();
    Border? _previewLabel;
    Border? _snapLabel;

    /// <summary>Name of the relation the current snap will create (shown next to the cursor).</summary>
    static string? SnapText(Snap s)
    {
        var text = SnapKindText(s);
        if (s.Kind == SnapKind.None || text == null) return text;
        return s.H ? text + ", Horizontal" : s.V ? text + ", Vertical" : text;
    }

    static string? SnapKindText(Snap s) => s.Kind switch
    {
        SnapKind.Point or SnapKind.Origin => "Coincident",
        SnapKind.ModelVertex => "Coincident (model)",
        SnapKind.Midpoint => "Midpoint",
        SnapKind.ModelMidpoint => "Midpoint (model edge)",
        SnapKind.OnLine => "On line",
        SnapKind.OnCircle => "On circle",
        SnapKind.ModelEdge => "On edge (model)",
        SnapKind.ModelCenter => "Center (model)",
        _ => s.H ? "Horizontal" : s.V ? "Vertical" : null,
    };

    /// <summary>Updates only the cursor-side snap label (cheap enough for every mouse move).</summary>
    void UpdateSnapLabel()
    {
        if (Overlay == null) return;
        var text = Tool is SketchTool.Select or SketchTool.SmartDimension or SketchTool.Trim or SketchTool.Fillet ? null : SnapText(_snap);
        if (text == null) { if (_snapLabel != null) _snapLabel.IsVisible = false; return; }
        if (_snapLabel == null) { _snapLabel = MakeLabel(); Overlay.Children.Add(_snapLabel); }
        SetLabel(_snapLabel, text, Brushes.DarkMagenta, _snap.P + new Vec2(Px * 40, -Px * 18));
        _snapLabel.IsVisible = true;
    }

    public void UpdateOverlay(ViewportControl vp)
    {
        if (Overlay == null) return;
        var live = new HashSet<int>();
        var dims = Sk.Constraints.Where(c => c.IsDimension).ToList();
        foreach (var c in dims)
        {
            live.Add(c.Id);
            if (_labels.ContainsKey(c.Id)) continue;
            var nb = MakeLabel();
            _labels[c.Id] = nb;
            Overlay.Children.Add(nb);
        }
        // labels the user did not place go where they are clear of each other and of the geometry
        LayoutLabels(dims, c => LabelSize(_labels[c.Id], DimText(c)));
        foreach (var c in dims)
        {
            var sel = Selected.Contains(new SkRef(SkRefKind.Constraint, c.Id));
            var hov = _hover is { Kind: SkRefKind.Constraint } h && h.Id == c.Id;
            SetLabel(_labels[c.Id], DimText(c), c.Driven ? Brushes.Gray : sel ? Brushes.RoyalBlue : hov ? Brushes.DarkOrange : Brushes.Black,
                LabelPos(c));
        }
        foreach (var id in _labels.Keys.Where(k => !live.Contains(k)).ToList())
        {
            Overlay.Children.Remove(_labels[id]); _labels.Remove(id);
        }
        if (_dimPreview != null)
        {
            if (_previewLabel == null) { _previewLabel = MakeLabel(); Overlay.Children.Add(_previewLabel); }
            SetLabel(_previewLabel, DimText(_dimPreview), Brushes.DarkOrange, DimAnchor(_dimPreview) + new Vec2(_dimPreview.LabelX, _dimPreview.LabelY));
            _previewLabel.IsVisible = true;
        }
        else if (_previewLabel != null) _previewLabel.IsVisible = false;

        // relation glyphs
        foreach (var g in _glyphs) Overlay.Children.Remove(g);
        _glyphs.Clear();
        if (!ShowRelations) return;
        var perEntity = new Dictionary<int, int>();
        foreach (var c in Sk.Constraints.Where(c => !c.IsDimension))
        {
            var pos = GlyphPos(c, perEntity);
            if (pos == null) continue;
            var sel = Selected.Contains(new SkRef(SkRefKind.Constraint, c.Id));
            var gb = new Border
            {
                Background = sel ? new SolidColorBrush(Color.FromRgb(120, 170, 255)) : new SolidColorBrush(Color.FromArgb(230, 200, 240, 200)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 140, 60)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 0),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock { Text = Glyph(c.Type), FontSize = 10, Foreground = Brushes.DarkGreen, FontWeight = FontWeight.Bold },
                IsHitTestVisible = true,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(gb, RelationName(c.Type));
            var cid = c.Id;
            gb.PointerPressed += (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) Selected.Clear();
                var r = new SkRef(SkRefKind.Constraint, cid);
                if (!Selected.Contains(r)) Selected.Add(r);
                SelectionChanged?.Invoke();
                UpdateOverlay(_vp);
                _vp.Redraw();
                e.Handled = true;
            };
            var s = Screen(pos.Value);
            Canvas.SetLeft(gb, s.X + 6); Canvas.SetTop(gb, s.Y + 4);
            Overlay.Children.Add(gb);
            _glyphs.Add(gb);
        }
        // fixed points
        // converted geometry: one On Edge mark on each entity, not an anchor on each of its points
        foreach (var e in Sk.Entities.Where(e => e.Link != null))
        {
            if (MidOf(e) is not { } at) continue;
            var s = Screen(at);
            var gb = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 210, 228, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 110, 170)), BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 0), CornerRadius = new CornerRadius(2),
                Child = new TextBlock { Text = "⛓", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(40, 70, 130)) },
                IsHitTestVisible = false,
            };
            ToolTip.SetTip(gb, "On Edge: converted from the model, follows it");
            Canvas.SetLeft(gb, s.X + 4); Canvas.SetTop(gb, s.Y + 2);
            Overlay.Children.Add(gb);
            _glyphs.Add(gb);
        }
        var refPoints = Sk.Entities.Where(e => e.IsReference || e.Link != null).SelectMany(e => e.PointIds).ToHashSet();
        foreach (var p in Sk.Points.Where(p => p.Fixed && p.Id != Sketch.OriginId && !refPoints.Contains(p.Id)))
        {
            var s = Screen(p.P);
            var gb = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 200, 240, 200)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 140, 60)), BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 0), CornerRadius = new CornerRadius(2),
                Child = new TextBlock { Text = "⚓", FontSize = 9, Foreground = Brushes.DarkGreen },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(gb, s.X + 6); Canvas.SetTop(gb, s.Y - 16);
            Overlay.Children.Add(gb);
            _glyphs.Add(gb);
        }
    }

    /// <summary>The middle of a line or arc, the top of a circle (where an entity's mark goes).</summary>
    Vec2? MidOf(SkEntity e)
    {
        switch (e)
        {
            case SkLine l when Sk.Point(l.P1) is { } a && Sk.Point(l.P2) is { } b: return (a.P + b.P) * 0.5;
            case SkCircle c when Sk.Point(c.Center) is { } cc: return cc.P + new Vec2(0, c.R);
            case SkArc a when Sk.Point(a.Center) is { } c && Sk.Point(a.Start) is { } s && Sk.Point(a.End) is { } en:
                {
                    var a0 = (s.P - c.P).Angle; var a1 = (en.P - c.P).Angle;
                    var sweep = a1 - a0; while (sweep <= 0) sweep += 2 * Math.PI;
                    return c.P + Vec2.FromAngle(a0 + sweep / 2, (s.P - c.P).Length);
                }
        }
        return null;
    }

    Vec2? GlyphPos(SkConstraint c, Dictionary<int, int> perEntity)
    {
        try
        {
            int key = c.Refs.Length > 0 ? c.Refs[^1] : 0;
            int k = perEntity.GetValueOrDefault(key);
            perEntity[key] = k + 1;
            var shift = new Vec2(k * Px * 16, 0);
            switch (c.Type)
            {
                case CType.Horizontal:
                case CType.Vertical:
                    if (c.Refs.Length == 1) { var (a, b) = LinePts(c.Refs[0]); return (a + b) * 0.5 + shift; }
                    return Sk.P(c.Refs[1]) + shift;
                case CType.PointOnLine:
                case CType.PointOnCircle:
                case CType.Midpoint:
                case CType.Coincident:
                    return Sk.P(c.Refs[0]) + shift;
                case CType.Symmetric:
                    return (Sk.P(c.Refs[0]) + Sk.P(c.Refs[1])) * 0.5 + shift;
                default:
                    {
                        // place near the midpoint of each referenced entity (use the last)
                        var e = Sk.Entity(c.Refs[^1]);
                        if (e is SkLine l) return (Sk.P(l.P1) + Sk.P(l.P2)) * 0.5 + shift;
                        if (e is SkCircle ci) return Sk.P(ci.Center) + new Vec2(ci.R * 0.7, ci.R * 0.7) + shift;
                        if (e is SkArc ar) return Sk.P(ar.Start) + shift;
                        return null;
                    }
            }
        }
        catch { return null; }
    }

    static Border MakeLabel() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
        Padding = new Thickness(3, 0),
        CornerRadius = new CornerRadius(2),
        Child = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold },
        IsHitTestVisible = false,
    };

    void SetLabel(Border b, string text, IBrush brush, Vec2 at)
    {
        var tb = (TextBlock)b.Child!;
        tb.Text = text; tb.Foreground = brush;
        var s = Screen(at);
        b.Measure(Size.Infinity);
        var sz = b.DesiredSize;
        Canvas.SetLeft(b, s.X - sz.Width / 2); Canvas.SetTop(b, s.Y - sz.Height / 2);
        // keep Bounds usable for hit testing before the next layout pass
        b.Arrange(new Rect(s.X - sz.Width / 2, s.Y - sz.Height / 2, sz.Width, sz.Height));
    }

    static string DimText(SkConstraint c)
    {
        var v = Expr.Format(c.Value);
        var prefix = c.Type switch { CType.Diameter => "⌀", CType.Radius => "R", _ => "" };
        var suffix = c.Type == CType.Angle ? "°" : "";
        var txt = prefix + v + suffix;
        if (c.Driven) txt = "(" + txt + ")";
        if (!string.IsNullOrEmpty(c.Expr) && !double.TryParse(c.Expr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            txt = "Σ " + txt;
        return txt;
    }

    public void DetachOverlay()
    {
        if (Overlay == null) return;
        CloseModifyBox(false);
        foreach (var b in _labels.Values) Overlay.Children.Remove(b);
        foreach (var g in _glyphs) Overlay.Children.Remove(g);
        if (_previewLabel != null) Overlay.Children.Remove(_previewLabel);
        if (_snapLabel != null) Overlay.Children.Remove(_snapLabel);
        _labels.Clear(); _glyphs.Clear(); _previewLabel = null; _snapLabel = null;
    }
}
