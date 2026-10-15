using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Model;

namespace SpryCad.App;

/// <summary>
/// Selection breadcrumbs, as SolidWorks': a face, edge or vertex picked in a part shows a small trail at the graphics
/// area's top left (Face › the feature that made it › its body); a click on the feature or the body selects that
/// instead, without looking for it in the tree.
/// </summary>
public partial class MainWindow
{
    Border? _crumbs;

    void HideBreadcrumbs()
    {
        if (_crumbs == null) return;
        Overlay.Children.Remove(_crumbs);
        _crumbs = null;
    }

    void UpdateBreadcrumbs()
    {
        HideBreadcrumbs();
        if (Asm != null || _sketch != null || _page != null || Selection.Count != 1 || Selection.First is not { } s) return;
        if (s.Kind is not (SelKind.Face or SelKind.Edge or SelKind.Vertex)) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        void Sep() => row.Children.Add(new TextBlock { Text = "›", Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) });
        void Crumb(string icon, string text, Action? click)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Icons.Make(icon, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Small } } };
            if (click == null) { row.Children.Add(new Border { Child = content, Padding = new Thickness(4, 2) }); return; }
            var b = new Button { Classes = { "tb" }, Content = content, Padding = new Thickness(4, 2) };
            ToolTip.SetTip(b, $"Select {text}");
            b.Click += (_, _) => click();
            row.Children.Add(b);
        }
        Crumb(s.Kind == SelKind.Face ? "part" : s.Kind == SelKind.Edge ? "line" : "point", s.Kind.ToString(), null);
        // the feature that made it (a face's name, an edge's or vertex's first face, starts with its id)
        var owner = s.Kind == SelKind.Face ? s.OwningFeatureId : FeatureColors.FeatureOf(s.Edge?.A ?? "");
        if (owner != null && Session.Doc.Find(owner) is { } f)
        {
            Sep();
            Crumb(FeatureTree.IconFor(f), f.Name, () => { Tree.Select(f.Id); OnTreeSelected(f.Id); });
        }
        if (s.BodyId is { } body)
        {
            Sep();
            var bodies = Session.Result?.State.Bodies.Count ?? 1;
            Crumb("part", bodies > 1 ? $"Body {body}" : "Body", () => Selection.Set(new SelItem { Kind = SelKind.Body, BodyId = body }));
        }
        _crumbs = new Border
        {
            Child = row, Background = Palette.Hud, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(2),
        };
        Overlay.Children.Add(_crumbs);
        Canvas.SetLeft(_crumbs, 8); Canvas.SetTop(_crumbs, 8);
    }
}
