using SpryCad.Core;
using SpryCad.App.Viewport;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Sketcher;

/// <summary>Sketch Fillet and Mirror Entities (the geometry is in <see cref="SketchOps"/>).</summary>
public sealed partial class SketchEditor
{
    /// <summary>The Sketch Fillet tool's radius (sketch units).</summary>
    public double FilletRadius { get; set; } = 5;

    // a line clicked with the Fillet tool, waiting for the second one
    long _filletFirstLine = -1;

    /// <summary>A click with the Fillet tool: a corner (two lines meeting there), or two lines one after the other.</summary>
    void FilletAt(PointerInfo p)
    {
        var h = HitTest(p.X, p.Y, includeDims: false);
        long corner = -1;
        if (h is { Kind: SkRefKind.Point } hp) corner = hp.Id;
        else if (h is { Kind: SkRefKind.Entity } he && Sk.Entity(he.Id) is SkLine line)
        {
            if (_filletFirstLine < 0 || _filletFirstLine == line.Id || Sk.Entity(_filletFirstLine) is not SkLine first)
            {
                _filletFirstLine = line.Id;
                Selected.Clear(); Selected.Add(new SkRef(SkRefKind.Entity, line.Id)); SelectionChanged?.Invoke();
                Message?.Invoke("Sketch Fillet: now click the other line of the corner.");
                return;
            }
            corner = SharedPoint(first, line);
            _filletFirstLine = -1;
            if (corner < 0) { Message?.Invoke("Sketch Fillet: those lines do not meet at a corner."); return; }
        }
        else { Message?.Invoke("Sketch Fillet: click a corner where two lines meet, or the two lines."); return; }
        FilletCorners(new[] { corner });
    }

    static long SharedPoint(SkLine a, SkLine b) => a.P1 == b.P1 || a.P1 == b.P2 ? a.P1 : a.P2 == b.P1 || a.P2 == b.P2 ? a.P2 : -1;

    /// <summary>The Sketch Fillet tool: corners (or pairs of lines) already selected are filleted first.</summary>
    public void StartFillet()
    {
        FilletSelection();
        SetTool(SketchTool.Fillet);
    }

    void FilletSelection()
    {
        var corners = SelectedPoints.ToList();
        var lines = SelectedEntities.OfType<SkLine>().ToList();
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
                if (SharedPoint(lines[i], lines[j]) is var c && c >= 0) corners.Add(c);
        if (corners.Count > 0) FilletCorners(corners.Distinct().ToList());
    }

    void FilletCorners(IList<long> corners)
    {
        var undo = _undo.Count;
        Snapshot();
        string? error = null; int done = 0;
        foreach (var c in corners)
        {
            var e = SketchOps.Fillet(Sk, c, FilletRadius);
            if (e == null) done++; else error = e;
        }
        if (done == 0)
        {
            // nothing changed: no undo step
            _undo.RemoveRange(undo, _undo.Count - undo);
            Message?.Invoke(error ?? "Sketch Fillet: nothing to fillet.");
            return;
        }
        Selected.Clear(); SelectionChanged?.Invoke();
        Resolve();
        Message?.Invoke(error ?? $"Filleted {done} corner{(done == 1 ? "" : "s")} (R {Expr.Format(FilletRadius)}). The original lines remain as construction geometry; delete the arc to restore the corner.");
        _vp.Redraw();
    }

    /// <summary>The distance Offset Entities uses (the last one entered).</summary>
    public double OffsetDistance { get; set; } = 5;

    /// <summary>
    /// Offset Entities: offsets the selected lines, arcs and circles (and the chain they belong to), associatively,
    /// then opens the distance for typing. Null when done, else why not.
    /// </summary>
    public string? OffsetSelection()
    {
        var ids = SelectedEntities.Where(e => e is SkLine or SkArc or SkCircle).Select(e => e.Id).ToList();
        var err = Offset(ids, OffsetDistance, false);
        if (err == null && Selected.FirstOrDefault() is { Kind: SkRefKind.Constraint } r && Sk.Constraint(r.Id) is { } off)
            OpenModifyBox(off); // type the distance straight away
        return err;
    }

    /// <summary>
    /// Offsets the given lines, arcs and circles (and the chains they belong to) by <paramref name="distance"/>,
    /// associatively, to the other side with <paramref name="reverse"/>; the offset's dimension is left selected.
    /// Null when done, else why not.
    /// </summary>
    public string? Offset(IReadOnlyCollection<long> ids, double distance, bool reverse)
    {
        ids = ids.Where(id => Sk.Entity(id) is SkLine or SkArc or SkCircle).ToList();
        if (ids.Count == 0) return "Offset Entities: select a line, arc or circle (the chain it is part of is offset).";
        Snapshot();
        OffsetDistance = distance;
        var (off, err) = SketchOps.Offset(Sk, SketchOps.Chain(Sk, ids), distance, reverse);
        if (off == null) { Undo(); _redo.Clear(); return err; }
        Selected.Clear(); Selected.Add(new SkRef(SkRefKind.Constraint, off.Id));
        SelectionChanged?.Invoke();
        Resolve();
        _vp.Redraw();
        return null;
    }

    /// <summary>Puts an offset's copies on the other side.</summary>
    public void ReverseOffset(SkConstraint offset)
    {
        Snapshot();
        SketchOps.ReverseOffset(Sk, offset);
        Resolve();
        _vp.Redraw();
    }

    /// <summary>
    /// Where an Offset dimension measures: from the middle of the first original to the copy, square across (a
    /// line: to the copy line; an arc or circle: along its radius).
    /// </summary>
    (Vec2 a, Vec2 b) OffsetPoints(SkConstraint c)
    {
        if (c.Refs.Length < 2) return (Vec2.Zero, Vec2.Zero);
        var o = Sk.Entity(c.Refs[0]); var q = Sk.Entity(c.Refs[1]);
        switch (o, q)
        {
            case (SkLine ol, SkLine ql):
                {
                    var a = (Sk.P(ol.P1) + Sk.P(ol.P2)) * 0.5;
                    var q1 = Sk.P(ql.P1); var d = Sk.P(ql.P2) - q1;
                    return (a, d.LengthSq < 1e-18 ? q1 : q1 + d * (Vec2.Dot(a - q1, d) / d.LengthSq));
                }
            case (SkArc oa, SkArc qa):
                {
                    var cen = Sk.P(oa.Center);
                    var s = Sk.P(oa.Start) - cen; var e = Sk.P(oa.End) - cen;
                    var a0 = Math.Atan2(s.Y, s.X); var a1 = Math.Atan2(e.Y, e.X);
                    while (a1 <= a0) a1 += 2 * Math.PI;
                    var u = Vec2.FromAngle((a0 + a1) / 2, 1);
                    return (cen + u * Sk.ArcRadius(oa), cen + u * Sk.ArcRadius(qa));
                }
            case (SkCircle oc, SkCircle qc):
                {
                    var cen = Sk.P(oc.Center); var u = new Vec2(Math.Sqrt(0.5), Math.Sqrt(0.5));
                    return (cen + u * oc.R, cen + u * qc.R);
                }
        }
        return (Vec2.Zero, Vec2.Zero);
    }

    /// <summary>
    /// Mirror Entities: mirrors the selected entities about the selected centerline (a construction line), or about
    /// the line selected last when no centerline is selected. Null when done, else why not.
    /// </summary>
    public string? MirrorSelection()
    {
        var ents = SelectedEntities.Where(e => !e.IsReference).ToList();
        var lines = ents.OfType<SkLine>().ToList();
        var axis = lines.LastOrDefault(l => l.Construction)
            ?? Selected.Where(r => r.Kind == SkRefKind.Entity).Select(r => Sk.Entity(r.Id)).OfType<SkLine>().LastOrDefault();
        if (axis == null) return "Mirror: select the entities to mirror and a centerline (or line) to mirror about.";
        return Mirror(ents.Where(e => e.Id != axis.Id).Select(e => e.Id).ToList(), axis.Id);
    }

    /// <summary>
    /// Mirrors the given entities about a line (a centerline or any line): copies kept symmetric to them, left
    /// selected. Null when done, else why not.
    /// </summary>
    public string? Mirror(IReadOnlyCollection<long> ids, long axisLine)
    {
        if (Sk.Entity(axisLine) is not SkLine) return "Mirror: pick a centerline (or line) to mirror about.";
        var toMirror = ids.Where(id => id != axisLine && Sk.Entity(id) is { IsReference: false }).ToList();
        if (toMirror.Count == 0) return "Mirror: pick the entities to mirror as well as the line to mirror about.";
        Snapshot();
        var made = SketchOps.Mirror(Sk, toMirror, axisLine);
        Selected.Clear();
        foreach (var id in made) Selected.Add(new SkRef(SkRefKind.Entity, id));
        SelectionChanged?.Invoke();
        Resolve();
        _vp.Redraw();
        return null;
    }
}
