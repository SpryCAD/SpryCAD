using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// The visual diff (the file format spec, section 23, face mode): two states of a part in one view, faces matched by
/// their persistent names and compared by their exact geometry: unchanged ghosted, added green, removed red (the older
/// state's), changed yellow. Clicking a face says which feature made it and what changed in that feature.
/// </summary>
public partial class MainWindow
{
    static readonly Rgba DiffAdded = new(0.20f, 0.70f, 0.30f), DiffRemoved = new(0.85f, 0.20f, 0.20f), DiffChanged = new(0.95f, 0.75f, 0.10f);
    static readonly Rgba DiffGhost = new(0.70f, 0.72f, 0.76f, 0.22f);

    /// <param name="changes">What changed between the two (by feature name), for the face clicked.</param>
    async void VisualCompare(CadDocument older, string olderLabel, CadDocument newer, string newerLabel, IReadOnlyList<DescribedChange> changes, Window owner)
    {
        var vp = new ViewportControl();
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary, Text = $"Building {olderLabel} and {newerLabel}…" };
        Control Key(Rgba c, string text) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children =
            {
                new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(255, (byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255))), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var counts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextMuted, Margin = new Thickness(12, 0, 0, 0) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 6), Children = { Key(DiffAdded, "Added"), Key(DiffChanged, "Changed"), Key(DiffRemoved, $"Removed (as in {olderLabel})"), counts } };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(legend, Dock.Top); DockPanel.SetDock(close, Dock.Bottom); DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(legend); dock.Children.Add(close); dock.Children.Add(info);
        dock.Children.Add(new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Child = vp });
        var w = ToolWindow($"Compare: {olderLabel} → {newerLabel}", dock, 900, 680);
        close.Click += (_, _) => w.Close();
        w.Show(owner);

        // both built off the UI thread (the current state's last rebuild when it is the one shown)
        DisplayMesh Build(CadDocument d) => AssemblyModel.Merge(new Regenerator().Rebuild(d).State.Bodies.Select(b => b.Mesh));
        DisplayMesh a, b;
        try { (a, b) = await Task.Run(() => (Build(older), Build(newer))); }
        catch (Exception ex) { App.Log(ex, "Visual compare"); info.Text = "The two could not be built: " + ex.Message; return; }

        var faces = VisualDiff.Compare(a, b);
        IReadOnlyList<(string, Rgba)> Colored(FaceChange kind, Rgba color) => faces.Where(kv => kv.Value == kind).Select(kv => (kv.Key, color)).ToList();
        var newFaces = Colored(FaceChange.Added, DiffAdded).Concat(Colored(FaceChange.Changed, DiffChanged)).ToList();
        vp.Instances = new[]
        {
            new MeshInstance("new", b, System.Numerics.Matrix4x4.Identity, DiffGhost, newFaces),
            new MeshInstance("old", a, System.Numerics.Matrix4x4.Identity, new Rgba(0, 0, 0, 0), Colored(FaceChange.Removed, DiffRemoved)),
        };
        int n(FaceChange k) => faces.Values.Count(v => v == k);
        counts.Text = $"{n(FaceChange.Added)} faces added, {n(FaceChange.Changed)} changed, {n(FaceChange.Removed)} removed, {n(FaceChange.Unchanged)} unchanged";
        info.Text = n(FaceChange.Added) + n(FaceChange.Changed) + n(FaceChange.Removed) == 0
            ? "No face changed: the shape is the same." : "Click a face to see which feature made it and what changed in it.";
        vp.StandardView("Trimetric", fit: true, animate: false);

        // a face clicked: its feature, and what changed in it
        var byName = changes.Where(c => c.Change.ObjectId.Kind == ObjectKind.Feature).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Text);
        vp.Mode = new CompareMode(face =>
        {
            if (face is not { } f) return;
            var id = VisualDiff.FeatureOf(f);
            var feature = newer.Find(id) ?? older.Find(id);
            var what = faces.TryGetValue(f, out var k) ? k switch
            {
                FaceChange.Added => "added", FaceChange.Removed => $"removed (as in {olderLabel})", FaceChange.Changed => "changed", _ => "unchanged",
            } : "";
            var fname = feature != null ? DisplayNames.Face(feature == newer.Find(id) ? newer : older, f) : f;
            info.Text = feature == null ? $"Face {fname}: {what}."
                : $"Face {fname}, made by {feature.Name}: {what}." + (byName.TryGetValue(feature.Name, out var t) ? "  " + t : feature.Name != null && k != FaceChange.Unchanged ? $"  ({feature.Name} itself did not change: something it is built on did.)" : "");
        });
    }

    /// <summary>The compare view's clicks: the face under the pointer (null: none).</summary>
    sealed class CompareMode(Action<string?> picked) : IViewportMode
    {
        public void PointerMove(ViewportControl vp, PointerInfo p) { }
        public void PointerDown(ViewportControl vp, PointerInfo p) { }
        public void PointerUp(ViewportControl vp, PointerInfo p) => picked(vp.PickInstance(p.X, p.Y)?.face);
        public bool KeyDown(ViewportControl vp, Key key, KeyModifiers mods) => false;
        public void Draw(ViewportControl vp, DynamicGeometry g) { }
        public void UpdateOverlay(ViewportControl vp) { }
        public Cursor? Cursor => null;
    }
}
