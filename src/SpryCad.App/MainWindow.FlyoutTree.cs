using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.UI;

namespace SpryCad.App;

public partial class MainWindow
{
    FeatureTree? _flyTree;
    AssemblyTree? _asmFlyTree;

    /// <summary>
    /// The flyout design tree, as SolidWorks': while the left panel shows something else (a PropertyManager page,
    /// another tab), a copy of the part's tree (or the assembly's) at the graphics area's top left, on the heads-up
    /// toolbars' light backing, so a plane, feature or component out of view can be picked without going back to the
    /// tree. Closed to its top row until its arrow opens it; its picks go where the tree's do (the open command's box,
    /// else the selection).
    /// </summary>
    void BuildFlyoutTree()
    {
        _flyTree = new FeatureTree { Flyout = true, Width = 240, IsVisible = false };
        _flyTree.SelectedChanged += id => { Tree.Select(id); OnTreeSelected(id); };
        _flyTree.HoverChanged += OnTreeHover;
        // the assembly's: its rows pick and hover as the assembly tree's (an origin plane into the Mate page's box)
        _asmFlyTree = new AssemblyTree { Flyout = true, Width = 260, IsVisible = false };
        _asmFlyTree.Selected += id => SelectComponent(id, exact: true);
        _asmFlyTree.MateSelected += SelectMate;
        _asmFlyTree.OriginPicked += OnAssemblyOriginPicked;
        _asmFlyTree.Hovered += OnAssemblyTreeHover;
        FlyoutHost.Child = _flyTree;
        // never taller than the graphics area, its rows scrolling inside
        GraphicsArea.SizeChanged += (_, e) => FlyoutHost.MaxHeight = Math.Max(80, e.NewSize.Height - 60);
    }

    /// <summary>Shows the flyout (the part's or the assembly's) while the panel is not showing the tree, else hides it.</summary>
    void UpdateFlyoutTree()
    {
        if (_flyTree == null || _asmFlyTree == null) return;
        var treeShown = _panelTabs.FirstOrDefault(t => t.Key == TreeTab)?.Content.IsVisible == true;
        var show = !treeShown;
        Control want = Asm != null ? _asmFlyTree : _flyTree;
        if (show == FlyoutHost.IsVisible && FlyoutHost.Child == want) return;
        _flyTree.CloseFlyout(); // closed again for next time
        _asmFlyTree.FlyoutOpen = false;
        _flyTree.IsVisible = _asmFlyTree.IsVisible = false; // a hidden tree lets go of its hover highlight
        FlyoutHost.Child = want;
        if (show) RebuildFlyoutTree();
        FlyoutHost.IsVisible = show;
        want.IsVisible = show;
    }

    void RebuildFlyoutTree()
    {
        if (Asm is { } a) { _asmFlyTree?.Show(a); return; }
        if (_flyTree == null) return;
        _flyTree.Rebuild(Session.Doc, TreeTitle(), Session.Result);
        _flyTree.Select(Tree.SelectedId);
    }

    /// <summary>An assembly tree's origin row clicked: an origin plane is the pick while faces are picked (a mate, a motor, a pattern).</summary>
    void OnAssemblyOriginPicked(string o)
    {
        if (PickingFaces && Asm is { } a && o is "Front" or "Top" or "Right") PickRef(a.Model.RefTo(null, o), "");
    }

    /// <summary>An assembly tree's row hovered: its component tinted, or its origin plane lit, in the graphics area.</summary>
    void OnAssemblyTreeHover(string? key)
    {
        if (Asm is not { } a) return;
        var plane = key is "origin:Front" or "origin:Top" or "origin:Right" ? key["origin:".Length..] : null;
        Viewport.Hover = plane != null ? new SelItem { Kind = SelKind.Plane, Name = plane } : null;
        var comp = key != null && a.Model.Find(key) != null ? key : null;
        if (comp != _asmHover) { _asmHover = comp; RefreshAssemblyScene(); }
        else Viewport.Redraw();
    }
}
