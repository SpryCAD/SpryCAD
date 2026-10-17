using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// Sketch Axes (a page within the sketch, as Inventor's Edit Coordinate System): the sketch's axes turned in its plane
/// to follow a straight edge (X along it) or a planar face (at the top). The geometry stays where it is in the model;
/// Horizontal, Vertical and the horizontal and vertical dimensions then hold along the new axes.
/// </summary>
public partial class MainWindow
{
    PmPage? _axesPage;
    PmSelectionBox? _axesBox;

    /// <summary>The Sketch Axes page is the one shown (picks in the graphics area go into its box).</summary>
    bool InAxesPage => _axesPage != null && _page == _axesPage && _sketch != null;

    void SketchAxesCommand()
    {
        if (_sketch is not { } ed) { Status("Sketch Axes requires an active sketch."); return; }
        if (_profileHole != null) { Status("A hole profile's axes are set by the hole."); return; }
        if (InAxesPage) { _axesPage!.Activate(_axesBox); return; }
        ed.SetTool(SketchTool.Select);
        var st = StateForEdit(ed.Feature, _sketchIsNew);
        Frame plane;
        try { plane = Resolver.ResolvePlane(ed.Feature.Plane, st); }
        catch (FeatureException ex) { Status("Could not find the sketch plane: " + ex.Message); return; }
        var undo = ed.UndoDepth;

        var page = new PmPage("Sketch Axes", "axis");
        var s = page.Section("Align To");
        var box = page.Selection(s, "Direction", SelFilter.Edge | SelFilter.Face,
            "Select a straight edge for the X axis, or a planar face to put it at the top.", 1);
        page.Info(s, "The geometry stays in place. Horizontal and Vertical relations follow the new axes.");
        page.ButtonRow(s, "Reverse Direction", () =>
        {
            if (ed.Feature.Align?.Clone() is not { } a) return;
            a.Flip = !a.Flip;
            Apply(a);
        }, "axis");
        page.ButtonRow(s, "Use Plane Axes", () => { box.Items.Clear(); box.Refresh(); ed.Realign(null, plane); });
        if (ed.Feature.Align?.Dir is { } dir && DirItem(dir, st) is { } shown) { box.Items.Add(shown); box.Refresh(); }
        // what was selected already is what the axes follow
        if (Selection.Items.FirstOrDefault(i => i.Kind is SelKind.Edge or SelKind.Face) is { } first) box.Add(first);
        Selection.Clear();
        box.Changed += () =>
        {
            if (box.Items.FirstOrDefault() is not { } it) { ed.Realign(null, plane); return; }
            try
            {
                if (AlignTo(it, plane, plane, st) is { } a) ed.Realign(a.align, a.frame);
                else Status("Select a straight edge or a planar face.");
            }
            catch (FeatureException ex) { Status($"Sketch axes not aligned: {ex.Message}"); }
        };

        void Apply(SketchAlign a)
        {
            try { ed.Realign(a, Resolver.Aligned(plane, Resolver.ResolveDir(a.Dir, st).dir, a.AlongY, a.Flip)); }
            catch (FeatureException ex) { Status($"Sketch axes not aligned: {ex.Message}"); }
        }

        page.OnOk = () => EndAxesPage();
        page.OnCancel = () => { ed.UndoTo(undo); EndAxesPage(); };
        _axesPage = page; _axesBox = box;
        ShowPage(page);
        ShowConfirmCorner(page.OnOk, page.OnCancel, "OK (Enter)", "Cancel (Esc)");
        Status("Select a straight edge for the sketch's X axis, or a planar face to put it at the top.");
    }

    /// <summary>Back to the sketch's page and its corner (Exit Sketch).</summary>
    void EndAxesPage()
    {
        if (!InAxesPage) return;
        _axesPage = null; _axesBox = null;
        ShowConfirmCorner(() => ExitSketch(true), DiscardSketch, "Exit Sketch", "Discard Changes", okIcon: "exitsketch");
        BuildSketchPage();
        Viewport.Redraw();
    }

    /// <summary>A model edge or face selected in the sketch goes into the page's box (selected again: taken out).</summary>
    void TakeAxesPick()
    {
        var pick = Selection.Items.LastOrDefault(i => i.Kind is SelKind.Edge or SelKind.Face);
        Selection.Clear();
        if (pick != null) _axesBox!.Add(pick);
    }
}
