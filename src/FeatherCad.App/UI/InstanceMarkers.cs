using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.App.Viewport;
using FeatherCad.Kernel;

namespace FeatherCad.App.UI;

/// <summary>
/// Numbered markers on the graphics area at each pattern instance while the pattern is edited (as SolidWorks'
/// "instances to skip"): click a marker to skip the instance or bring it back. The seed (1) is not clickable.
/// </summary>
public sealed class InstanceMarkers : IDisposable
{
    readonly ViewportControl _vp;
    readonly Func<List<(int number, Vec3 at)>> _positions;
    readonly Func<List<int>> _getSkip;
    readonly Action<List<int>> _setSkip;
    readonly List<(int number, Vec3 at, Border view)> _markers = new();
    bool _disposed;

    static readonly IBrush Live = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    static readonly IBrush LiveBorder = new SolidColorBrush(Color.FromRgb(46, 110, 210));
    static readonly IBrush Skipped = new SolidColorBrush(Color.FromRgb(250, 222, 222));
    static readonly IBrush SkippedBorder = new SolidColorBrush(Color.FromRgb(200, 40, 40));
    static readonly IBrush SeedFill = new SolidColorBrush(Color.FromRgb(225, 228, 234));
    static readonly IBrush SeedBorder = new SolidColorBrush(Color.FromRgb(120, 128, 140));

    public InstanceMarkers(ViewportControl vp, Func<List<(int number, Vec3 at)>> positions, Func<List<int>> getSkip, Action<List<int>> setSkip)
    {
        _vp = vp; _positions = positions; _getSkip = getSkip; _setSkip = setSkip;
        _vp.CameraChanged += Place;
        Rebuild();
    }

    /// <summary>Recomputes the instances (count, spacing or axis changed) and redraws the markers.</summary>
    public void Rebuild()
    {
        if (_disposed || _vp.Overlay is not { } overlay) return;
        foreach (var (_, _, v) in _markers) overlay.Children.Remove(v);
        _markers.Clear();
        List<(int number, Vec3 at)> pos;
        try { pos = _positions(); }
        catch (Exception) { return; } // invalid parameters: the page shows the error, no markers meanwhile
        var skip = _getSkip();
        foreach (var (n, at) in pos)
        {
            bool seed = n == 1, off = skip.Contains(n);
            var label = new TextBlock
            {
                Text = n.ToString(), FontSize = 10, FontWeight = FontWeight.SemiBold,
                Foreground = off ? SkippedBorder : seed ? SeedBorder : LiveBorder,
                TextDecorations = off ? TextDecorations.Strikethrough : null,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5),
                Background = off ? Skipped : seed ? SeedFill : Live, BorderBrush = off ? SkippedBorder : seed ? SeedBorder : LiveBorder,
                Child = label, IsHitTestVisible = !seed, Cursor = seed ? null : new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(b, seed ? "Instance 1: the seed" : off ? $"Instance {n} is skipped: click to bring it back" : $"Instance {n}: click to skip it");
            if (!seed)
            {
                int num = n;
                b.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
                    e.Handled = true; // not a pick on the model underneath
                    var s = _getSkip().ToList();
                    if (!s.Remove(num)) s.Add(num);
                    s.Sort();
                    _setSkip(s);
                    Rebuild();
                };
            }
            overlay.Children.Add(b);
            _markers.Add((n, at, b));
        }
        Place();
    }

    void Place()
    {
        if (_disposed) return;
        var cam = _vp.Camera;
        foreach (var (_, at, v) in _markers)
        {
            var p = cam.Project(at);
            // behind the eye (perspective) or off screen: hidden
            bool show = p.Z is > -1 and < 1 && p.X > -20 && p.Y > -20 && p.X < cam.Width + 20 && p.Y < cam.Height + 20;
            v.IsVisible = show;
            Canvas.SetLeft(v, p.X - 11);
            Canvas.SetTop(v, p.Y - 11);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vp.CameraChanged -= Place;
        if (_vp.Overlay is { } overlay) foreach (var (_, _, v) in _markers) overlay.Children.Remove(v);
        _markers.Clear();
    }
}
