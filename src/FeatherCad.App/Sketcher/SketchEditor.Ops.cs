using FeatherCad.Core;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Sketching;

namespace FeatherCad.App.Sketcher;

/// <summary>Sketch Fillet and Mirror Entities (the geometry is in <see cref="SketchOps"/>).</summary>
public sealed partial class SketchEditor
{
    /// <summary>The Sketch Fillet tool's radius (sketch units).</summary>
    public double FilletRadius { get; set; } = 5;

    // a line clicked with the Fillet tool, waiting for the second one
    int _filletFirstLine = -1;

    /// <summary>A click with the Fillet tool: a corner (two lines meeting there), or two lines one after the other.</summary>
    void FilletAt(PointerInfo p)
    {
        var h = HitTest(p.X, p.Y, includeDims: false);
        int corner = -1;
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

    static int SharedPoint(SkLine a, SkLine b) => a.P1 == b.P1 || a.P1 == b.P2 ? a.P1 : a.P2 == b.P1 || a.P2 == b.P2 ? a.P2 : -1;

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

    void FilletCorners(IList<int> corners)
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
        var toMirror = ents.Where(e => e.Id != axis.Id).Select(e => e.Id).ToList();
        if (toMirror.Count == 0) return "Mirror: select the entities to mirror as well as the line to mirror about.";
        Snapshot();
        var made = SketchOps.Mirror(Sk, toMirror, axis.Id);
        Selected.Clear();
        foreach (var id in made) Selected.Add(new SkRef(SkRefKind.Entity, id));
        SelectionChanged?.Invoke();
        Resolve();
        _vp.Redraw();
        return null;
    }
}
