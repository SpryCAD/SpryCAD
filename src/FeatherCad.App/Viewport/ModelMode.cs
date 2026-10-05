using Avalonia.Input;
using FeatherCad.App.Session;

namespace FeatherCad.App.Viewport;

/// <summary>Default interaction: hover highlight and selection of faces, edges, planes and sketches.</summary>
public sealed class ModelMode : IViewportMode
{
    readonly SelectionSet _sel;

    /// <summary>What can be picked. Set by the active PropertyManager page.</summary>
    public SelFilter Filter { get; set; } = SelFilter.Any;
    /// <summary>When set, picks are offered here first (PropertyManager selection boxes).</summary>
    public Func<SelItem, bool>? PickHandler { get; set; }
    public event Action<SelItem?>? DoubleClicked;

    public ModelMode(SelectionSet sel) { _sel = sel; }

    public Cursor? Cursor => null;

    public void PointerMove(ViewportControl vp, PointerInfo p)
    {
        var h = vp.Pick(p.X, p.Y, Filter);
        if (h?.Key != vp.Hover?.Key) { vp.Hover = h; vp.Redraw(); }
    }

    public void PointerDown(ViewportControl vp, PointerInfo p)
    {
        var h = vp.Pick(p.X, p.Y, Filter);
        if (p.ClickCount >= 2) { DoubleClicked?.Invoke(h); return; }
        if (h != null && PickHandler != null && PickHandler(h)) { vp.Redraw(); return; }
        if (h == null) { if (!p.Ctrl) _sel.Clear(); }
        else if (p.Ctrl || p.Shift) _sel.Toggle(h);
        else _sel.Set(h);
        vp.Redraw();
    }

    public void PointerUp(ViewportControl vp, PointerInfo p) { }
    public bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods) => false;
    public void Draw(ViewportControl vp, DynamicGeometry g) { }
    public void UpdateOverlay(ViewportControl vp) { }
}
