using Avalonia;
using Avalonia.Input;
using FeatherCad.Core.Assembly;
using FeatherCad.Kernel;

namespace FeatherCad.App.Viewport;

/// <summary>What the assembly mode works on (the main window with an assembly tab active).</summary>
public interface IAssemblyHost
{
    void HoverComponent(string? id);
    void SelectComponent(string? id);
    void OpenComponent(string id);
    Component? FindComponent(string? id);
    /// <summary>Before a change (undo point).</summary>
    void BeginAssemblyChange();
    /// <summary>After a change (unsaved; tree and title updated).</summary>
    void EndAssemblyChange();
    /// <summary>Shows the components where they are now (while dragging: no tree update).</summary>
    void RefreshAssemblyScene();
}

/// <summary>
/// Picking and moving components: hover and click select; double-click opens the part; dragging a floating component
/// moves it in the view's plane through the point grabbed, Shift+drag turns it about its middle (left-right about the
/// view's up, up-down about its right). A fixed component is not dragged.
/// </summary>
public sealed class AssemblyMode(IAssemblyHost host) : IViewportMode
{
    Component? _drag;
    Vec3 _grab, _normal, _center;
    Point _last;
    bool _turn, _moved;

    public Cursor? Cursor => null;

    public void PointerMove(ViewportControl vp, PointerInfo p)
    {
        if (_drag == null) { host.HoverComponent(vp.PickInstance(p.X, p.Y)?.id); return; }
        if (!_moved) { host.BeginAssemblyChange(); _moved = true; }
        var t = _drag.Placement.ToTransform();
        if (_turn)
        {
            double dx = p.X - _last.X, dy = p.Y - _last.Y;
            var up = Camera.V(vp.Camera.Up); var right = Camera.V(vp.Camera.Right);
            var r = Transform.Rotation(_center, up, dx * 0.01) * Transform.Rotation(_center, right, dy * 0.01);
            _drag.Placement = Placement.From(r * t);
        }
        else
        {
            // the grabbed point follows the cursor in the plane through it, square to the view
            var (o, d) = vp.Camera.Ray(p.X, p.Y);
            var den = Vec3.Dot(d, _normal);
            if (Math.Abs(den) < 1e-12) return;
            var hit = o + d * (Vec3.Dot(_grab - o, _normal) / den);
            _drag.Placement = Placement.From(Transform.Translation(hit - _grab) * t);
            _grab = hit;
        }
        _last = new Point(p.X, p.Y);
        host.RefreshAssemblyScene();
    }

    public void PointerDown(ViewportControl vp, PointerInfo p)
    {
        var hit = vp.PickInstance(p.X, p.Y);
        if (p.ClickCount >= 2) { if (hit is { } dh) host.OpenComponent(dh.id); return; }
        host.SelectComponent(hit?.id);
        if (hit is not { } h || host.FindComponent(h.id) is not { Fixed: false } c) return;
        _drag = c; _grab = h.point; _moved = false; _turn = p.Shift;
        _normal = vp.Camera.Ray(p.X, p.Y).dir;
        _last = new Point(p.X, p.Y);
        // its middle: the placed box's center
        var mesh = vp.Instances.FirstOrDefault(i => i.Id == c.Id).Mesh;
        var b = mesh?.Bounds ?? Box3.Empty;
        _center = b.IsEmpty ? h.point : c.Placement.ToTransform().Point(b.Center);
    }

    public void PointerUp(ViewportControl vp, PointerInfo p)
    {
        if (_drag != null && _moved) host.EndAssemblyChange();
        _drag = null;
    }

    public bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods) => false;
    public void Draw(ViewportControl vp, DynamicGeometry g) { }
    public void UpdateOverlay(ViewportControl vp) { }
}
