using Avalonia;
using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;

namespace SpryCad.App;

/// <summary>
/// Mirror Entities and Offset Entities as PropertyManager pages, as the features have (and Convert Entities): boxes
/// of the sketch entities they work on, filled by clicking them in the sketch (clicked again, or Delete in the box:
/// taken out), their settings, and OK / Cancel. The entities in the boxes are the sketch's selection, so they show
/// highlighted. What was selected when the command started is put in the boxes.
/// </summary>
public partial class MainWindow
{
    PmPage? _entityPage;
    bool _syncingEntityPicks;

    /// <summary>A Mirror or Offset page is the one shown (picks in the sketch go into its boxes).</summary>
    bool InEntityPage => _entityPage != null && _page == _entityPage && _sketch != null;

    void MirrorEntities()
    {
        if (_sketch is not { } ed) { Status("Mirror Entities requires an active sketch. Edit or create a sketch first."); return; }
        if (InEntityPage) EndEntityPage();
        var page = new PmPage("Mirror Entities", "sketchmirror");
        var what = page.Section("Entities to Mirror");
        var ents = page.Selection(what, "Entities", SelFilter.SketchSegment,
            "Select the sketch entities to mirror. Select one again, or press Delete in the box, to remove it.",
            accept: i => ed.Sk.Entity(i.SketchItem) is { IsReference: false });
        var about = page.Section("Mirror About");
        var axis = page.Selection(about, "Line", SelFilter.SketchSegment,
            "Centerline or line to mirror about. The copies stay symmetric about it.", max: 1,
            accept: i => ed.Sk.Entity(i.SketchItem) is SkLine);
        // what was selected: a centerline (else the last line) to mirror about, the rest to mirror
        var picked = ed.SelectedEntities.Where(e => !e.IsReference).ToList();
        var line = picked.OfType<SkLine>().LastOrDefault(l => l.Construction) ?? (picked.Count > 1 ? picked.OfType<SkLine>().LastOrDefault() : null);
        if (line != null) axis.Add(EntityItem(line.Id));
        foreach (var e in picked.Where(e => e.Id != line?.Id)) ents.Add(EntityItem(e.Id));
        // the line picked: back to the entities
        axis.Changed += () => { if (axis.Items.Count > 0) page.Activate(ents); };
        OpenEntityPage(page, () =>
        {
            if (axis.Items.Count == 0) return "Select the line to mirror about.";
            return ed.Mirror(ents.Items.Select(i => i.SketchItem).ToList(), axis.Items[0].SketchItem);
        }, axis.Items.Count == 0 && ents.Items.Count > 0 ? axis : ents,
           "Entities mirrored. The copies stay symmetric about the line.");
    }

    void OffsetEntities()
    {
        if (_sketch is not { } ed) { Status("Offset Entities requires an active sketch. Edit or create a sketch first."); return; }
        if (InEntityPage) EndEntityPage();
        var page = new PmPage("Offset Entities", "offset");
        var what = page.Section("Entities to Offset");
        var ents = page.Selection(what, "Entities", SelFilter.SketchSegment,
            "Select lines, arcs or circles; each adds its connected chain. Select one again, or press Delete in the box, to remove it.",
            accept: i => ed.Sk.Entity(i.SketchItem) is SkLine or SkArc or SkCircle or SkSpline);
        var prm = page.Section("Parameters");
        var distance = ed.OffsetDistance;
        var reverse = false;
        page.Number(prm, "Distance", () => Units.Entry(distance), v =>
        {
            if (UnitFormat.TryLength(v, Session.Lookup, out var d, out _) && d > 0) distance = d;
        }, "offset");
        page.Check(prm, "Reverse direction", () => reverse, v => reverse = v);
        page.Info(prm, "The offset follows changes to the original entities. Its distance dimension can be edited later.");
        foreach (var e in ed.SelectedEntities.Where(e => e is SkLine or SkArc or SkCircle or SkSpline)) ents.Add(EntityItem(e.Id));
        OpenEntityPage(page, () => ed.Offset(ents.Items.Select(i => i.SketchItem).ToList(), distance, reverse), ents,
            "Offset created. Double-click its dimension to change the distance.");
    }

    /// <summary>
    /// Add Relations, as SolidWorks': a box of the points and entities to relate, picked by plain clicks (no Ctrl),
    /// the relations that fit them as buttons (as the selection gets them on the sketch's page), and the relations
    /// they have already, each to delete. Each relation added is a step of its own (Ctrl+Z); OK closes the page.
    /// </summary>
    void AddRelationCommand()
    {
        if (_sketch is not { } ed) { Status("Add Relations requires an active sketch. Edit or create a sketch first."); return; }
        if (InEntityPage) EndEntityPage();
        var page = new PmPage("Add Relations", "relations");
        var sel = page.Section("Selected Entities");
        var box = page.Selection(sel, "Entities", SelFilter.SketchSegment | SelFilter.Vertex,
            "Select the points, lines, arcs and circles to relate. Select one again, or press Delete in the box, to remove it.");
        var add = page.Section("Add Relations");
        var buttons = new WrapPanel();
        add.Children.Add(buttons);
        var exSection = page.Section("Existing Relations");
        var existing = new StackPanel { Spacing = 2 };
        exSection.Children.Add(existing);
        foreach (var r in ed.Selected.Where(r => r.Kind is SkRefKind.Entity or SkRefKind.Point)) box.Add(ItemOf(r));

        void Refresh()
        {
            buttons.Children.Clear();
            var rels = ed.ApplicableRelations();
            if (box.Items.Count == 0) buttons.Children.Add(Muted("Select entities to see the relations that apply to them."));
            else if (rels.Count == 0) buttons.Children.Add(Muted("No relations apply to the selected entities."));
            foreach (var rel in rels)
            {
                var b = new Button { Margin = new Thickness(2), Padding = new Thickness(6, 2), Content = $"{SketchEditor.Glyph(rel)}  {SketchEditor.RelationName(rel)}" };
                var rr = rel;
                b.Click += (_, _) =>
                {
                    var err = ed.AddRelation(rr);
                    Status(err ?? $"{SketchEditor.RelationName(rr)} relation added.");
                    Prune();
                };
                buttons.Children.Add(b);
            }
            existing.Children.Clear();
            var have = ed.RelationsOfSelection().ToList();
            if (have.Count == 0) existing.Children.Add(Muted(box.Items.Count == 0 ? "" : "None."));
            foreach (var c in have)
            {
                var row = new DockPanel();
                var del = Dialogs.DeleteButton("Delete relation");
                var cid = c.Id;
                del.Click += (_, _) =>
                {
                    // deleted as the sketch deletes a selected relation, then the boxes' picks selected again
                    _syncingEntityPicks = true;
                    try { ed.Selected.Clear(); ed.Selected.Add(new SkRef(SkRefKind.Constraint, cid)); ed.DeleteSelection(); }
                    finally { _syncingEntityPicks = false; }
                    Prune();
                };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = $"{SketchEditor.Glyph(c.Type)}  {SketchEditor.RelationName(c.Type)}", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                existing.Children.Add(row);
            }
        }
        // after a change: picks gone from the sketch (a coincident merges two points into one) out of the box
        void Prune()
        {
            for (int i = box.Items.Count - 1; i >= 0; i--)
            {
                var it = box.Items[i];
                if (it.Kind == SelKind.Vertex ? ed.Sk.Point(it.SketchItem) == null : ed.Sk.Entity(it.SketchItem) == null) box.Items.RemoveAt(i);
            }
            box.Refresh();
            SyncEntitySelection();
            Refresh();
        }
        static TextBlock Muted(string text) => new() { Text = text, Foreground = Palette.TextMuted, FontSize = TypeScale.Small, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        OpenEntityPage(page, () => null, box, "");
        box.Changed += Refresh; // after the selection follows the box (OpenEntityPage), so the relations are the box's
        Refresh();
    }

    /// <summary>Shows an entity page: its boxes take the sketch's picks; OK runs <paramref name="apply"/> (null: done, else why not).</summary>
    void OpenEntityPage(PmPage page, Func<string?> apply, PmSelectionBox first, string done)
    {
        _sketch!.SetTool(SketchTool.Select);
        foreach (var b in page.Boxes) b.Changed += SyncEntitySelection;
        void Ok()
        {
            if (!InEntityPage) return;
            if (apply() is { } problem) { Status(problem); return; }
            EndEntityPage(keepSelection: true);
            if (done.Length > 0) Status(done);
        }
        page.OnOk = Ok;
        page.OnCancel = () => EndEntityPage();
        _entityPage = page;
        ShowPage(page);
        // its boxes list the entities by the sketch's names ("Line3"), set after ShowPage (which names model picks)
        foreach (var b in page.Boxes) { b.LabelOf = i => DisplayNames.Of(_sketch!.Sk, i.SketchItem); b.Refresh(); }
        page.Activate(first);
        ShowConfirmCorner(Ok, () => EndEntityPage(), "OK (Enter)", "Cancel (Esc)");
        SyncEntitySelection();
    }

    /// <summary>Back to the sketch's own page and its corner (Exit Sketch).</summary>
    void EndEntityPage(bool keepSelection = false)
    {
        if (_entityPage == null) return;
        _entityPage = null;
        if (_sketch is { } ed && !keepSelection) { ed.Selected.Clear(); Viewport.Redraw(); }
        ShowConfirmCorner(() => ExitSketch(true), DiscardSketch, "Exit Sketch", "Discard Changes", okIcon: "exitsketch");
        BuildSketchPage();
    }

    static SelItem EntityItem(long id) => new() { Kind = SelKind.SketchSegment, SketchItem = id };
    static SelItem PointItem(long id) => new() { Kind = SelKind.Vertex, SketchItem = id };
    static SelItem ItemOf(SkRef r) => r.Kind == SkRefKind.Point ? PointItem(r.Id) : EntityItem(r.Id);
    static SkRef RefOf(SelItem i) => new(i.Kind == SelKind.Vertex ? SkRefKind.Point : SkRefKind.Entity, i.SketchItem);

    /// <summary>
    /// What was clicked in the sketch goes into the page's active box: an entity not in a box is added (several, from
    /// a box select), one clicked again alone is taken out; then the boxes' entities are the selection again.
    /// </summary>
    void TakeEntityPicks()
    {
        if (_syncingEntityPicks || _sketch is not { } ed || _entityPage is not { } page) return;
        var boxed = page.Boxes.SelectMany(b => b.Items).Select(RefOf).ToHashSet();
        var sel = ed.Selected.Where(r => r.Kind is SkRefKind.Entity or SkRefKind.Point).ToList();
        var added = sel.Where(r => !boxed.Contains(r)).ToList();
        // the item just clicked (the sketch keeps a selected item selected on a plain click, so it says nothing changed)
        var clicked = ed.LastClick; ed.LastClick = null;
        if (added.Count > 0) foreach (var r in added) page.HandlePick(ItemOf(r));
        else if (clicked is { } c && boxed.Contains(c)) RemoveFromBoxes(page, c);
        else if (sel.Count > 0) foreach (var r in boxed.Except(sel)) RemoveFromBoxes(page, r);
        SyncEntitySelection();
    }

    static void RemoveFromBoxes(PmPage page, SkRef r)
    {
        foreach (var b in page.Boxes)
        {
            var i = b.Items.FindIndex(x => RefOf(x) == r);
            if (i >= 0) b.Remove(i);
        }
    }

    /// <summary>The sketch's selection made the boxes' entities, so they show highlighted.</summary>
    void SyncEntitySelection()
    {
        if (_sketch is not { } ed || _entityPage is not { } page) return;
        _syncingEntityPicks = true;
        try
        {
            ed.Selected.Clear();
            foreach (var r in page.Boxes.SelectMany(b => b.Items).Select(RefOf).Distinct()) ed.Selected.Add(r);
            Viewport.Redraw();
        }
        finally { _syncingEntityPicks = false; }
    }
}
