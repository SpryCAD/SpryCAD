using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Model;

namespace SpryCad.App;

/// <summary>
/// The context palette, as SolidWorks' context toolbar: an item clicked in the graphics area brings a small
/// see-through toolbar beside the cursor with what can be done with it (a flat face: sketch on it, look at it, fillet
/// its edges...; a component: mate, hide, isolate...). It fades as the cursor moves away, and goes when the
/// selection does, a command starts, or Esc.
/// </summary>
public partial class MainWindow
{
    Border? _palette;
    Point _paletteAt;

    /// <summary>A palette entry: its icon, its name (the tooltip), and what it does.</summary>
    readonly record struct PaletteItem(string Icon, string Tip, Action Run);

    void HidePalette()
    {
        if (_palette == null) return;
        Overlay.Children.Remove(_palette);
        _palette = null;
    }

    /// <summary>The palette, beside where the graphics area was last clicked (kept inside it).</summary>
    void ShowPalette(IReadOnlyList<PaletteItem> items)
    {
        HidePalette();
        if (items.Count == 0) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        foreach (var it in items)
        {
            var b = new Button { Classes = { "tb" }, Content = Icons.Make(it.Icon, 20), Padding = new Thickness(3) };
            ToolTip.SetTip(b, it.Tip);
            var run = it.Run;
            b.Click += (_, _) => { HidePalette(); run(); };
            row.Children.Add(b);
        }
        _palette = new Border
        {
            Child = row, Background = Palette.Hud, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.FromArgb(46, 30, 50, 80) }),
        };
        Overlay.Children.Add(_palette);
        // above and to the right of the click, as SolidWorks puts it (inside the graphics area)
        var at = Viewport.LastPress;
        var w = items.Count * 27 + 6;
        var x = Math.Clamp(at.X + 14, 4, Math.Max(4, Overlay.Bounds.Width - w - 4));
        var y = Math.Clamp(at.Y - 46, 4, Math.Max(4, Overlay.Bounds.Height - 36));
        Canvas.SetLeft(_palette, x); Canvas.SetTop(_palette, y);
        _paletteAt = new Point(x + w / 2.0, y + 15);
    }

    /// <summary>The palette for what was just clicked in a part (one item, picked in the graphics area, no command open).</summary>
    void UpdatePalette()
    {
        HidePalette();
        if (Asm != null || _page != null || _sketch != null || Selection.Count != 1 || Selection.First is not { } s) return;
        // (picked in the graphics area just now, not in the tree)
        if ((DateTime.UtcNow - Viewport.LastPressAt).TotalSeconds > 3) return; // (a first pick can take a moment)
        var items = new List<PaletteItem>();
        void Add(string icon, string tip, Action run) => items.Add(new PaletteItem(icon, tip, run));
        var owner = s.Kind == SelKind.Edge ? FeatureColors.FeatureOf(s.Edge?.A ?? "") : s.OwningFeatureId;
        void EditOwner() { if (owner != null && Session.Doc.Find(owner) is { } f) Add(FeatureTree.IconFor(f), $"Edit {f.Name}", () => EditFeature(f.Id)); }
        switch (s.Kind)
        {
            case SelKind.Face when s.Planar:
                Add("sketch", "Sketch on this face", () => StartSketchCommand());
                Add("normalto", "Normal To", NormalTo);
                EditOwner();
                Add("fillet", "Fillet its edges", () => BlendCommand(true));
                Add("shell", "Shell (this face open)", ShellCommand);
                Add("measure", "Measure", MeasureCommand);
                break;
            case SelKind.Face:
                EditOwner();
                Add("fillet", "Fillet its edges", () => BlendCommand(true));
                Add("measure", "Measure", MeasureCommand);
                break;
            case SelKind.Edge:
                Add("fillet", "Fillet", () => BlendCommand(true));
                Add("chamfer", "Chamfer", () => BlendCommand(false));
                EditOwner();
                Add("measure", "Measure", MeasureCommand);
                break;
            case SelKind.Plane:
                Add("sketch", "Sketch on this plane", () => StartSketchCommand());
                Add("plane", "Reference Plane from it", PlaneCommand);
                Add("normalto", "Normal To", NormalTo);
                if (s.FeatureId is { } pf && Session.Doc.Find(pf) is PlaneFeature pfe)
                {
                    Add("plane", $"Edit {pfe.Name}", () => EditFeature(pf));
                    Add("hide", "Hide", () => SetSketchShown(pf, false));
                }
                break;
            case SelKind.Sketch when s.FeatureId is { } sk:
                Add("sketch", "Edit Sketch", () => EditFeature(sk));
                Add("extrude", "Extruded Boss/Base", () => ExtrudeCommand(false));
                Add("cut", "Extruded Cut", () => ExtrudeCommand(true));
                Add("revolve", "Revolved Boss/Base", () => RevolveCommand(false));
                Add("hide", "Hide", () => SetSketchShown(sk, false));
                break;
            case SelKind.Vertex:
                Add("measure", "Measure", MeasureCommand);
                break;
        }
        ShowPalette(items);
    }

    /// <summary>The palette for a component just clicked in an assembly's graphics area.</summary>
    void ComponentPalette(string path)
    {
        if (Asm is not { } a || _page != null || a.Model.Find(path) is not { } o) return;
        var items = new List<PaletteItem>
        {
            new("mates", "Mate", MateCommand),
            new(o.Hidden ? "show" : "hide", o.Hidden ? "Show" : "Hide", ToggleHidden),
            new("isolate", "Isolate", () => Isolate()),
            new("open", o.Component.IsAssembly ? "Open Sub-assembly" : "Open Part", () => OpenComponent(path)),
        };
        if (!path.Contains('/'))
        {
            var fixedNow = o.Component.Fixed;
            items.Add(new(fixedNow ? "float" : "fix", fixedNow ? "Float" : "Fix", () => SetFixed(!fixedNow)));
            items.Add(new("rename", "Rename", () => _ = RenameComponent(path)));
        }
        ShowPalette(items);
    }

    /// <summary>The palette fades as the cursor moves away from it, and goes past a little way.</summary>
    void FadePalette(Point cursor)
    {
        if (_palette == null) return;
        var d = Math.Sqrt((cursor.X - _paletteAt.X) * (cursor.X - _paletteAt.X) + (cursor.Y - _paletteAt.Y) * (cursor.Y - _paletteAt.Y));
        if (d > 260) { HidePalette(); return; }
        _palette.Opacity = Math.Clamp(1 - (d - 90) / 170, 0.15, 1);
    }
}
