using SpryCad.App.UI;

namespace SpryCad.App;

public partial class MainWindow
{
    FeatureTree? _flyTree;

    /// <summary>
    /// The flyout design tree, as SolidWorks': while the left panel shows something else (a PropertyManager page,
    /// another tab), a copy of the part's tree at the graphics area's top left, on the heads-up toolbars' light backing,
    /// so a plane, feature or sketch out of view can be picked without going back to the tree. Closed to the part's
    /// row until its arrow opens it; its picks go where the tree's do (the open command's box, else the selection).
    /// </summary>
    void BuildFlyoutTree()
    {
        _flyTree = new FeatureTree { Flyout = true, Width = 240, IsVisible = false };
        _flyTree.SelectedChanged += id => { Tree.Select(id); OnTreeSelected(id); };
        _flyTree.HoverChanged += OnTreeHover;
        FlyoutHost.Child = _flyTree;
        // never taller than the graphics area, its rows scrolling inside
        GraphicsArea.SizeChanged += (_, e) => FlyoutHost.MaxHeight = Math.Max(80, e.NewSize.Height - 60);
    }

    /// <summary>Shows the flyout while the panel is not showing the part's tree (an assembly has none), else hides it.</summary>
    void UpdateFlyoutTree()
    {
        if (_flyTree == null) return;
        var treeShown = _panelTabs.FirstOrDefault(t => t.Key == TreeTab)?.Content.IsVisible == true;
        var show = Asm == null && !treeShown;
        if (show == FlyoutHost.IsVisible) return;
        if (show) RebuildFlyoutTree();
        else _flyTree.CloseFlyout(); // closed again for next time
        FlyoutHost.IsVisible = show;
        _flyTree.IsVisible = show;   // a hidden tree lets go of its hover highlight
    }

    void RebuildFlyoutTree()
    {
        if (_flyTree == null || Asm != null) return;
        _flyTree.Rebuild(Session.Doc, Session.Title.TrimEnd(' ', '*'), Session.Result);
        _flyTree.Select(Tree.SelectedId);
    }
}
