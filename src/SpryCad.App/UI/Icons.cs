using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace SpryCad.App.UI;

/// <summary>Vector line icons (24x24 path data), drawn in a light SolidWorks-like style.</summary>
public static class Icons
{
    static readonly Dictionary<string, (string path, string? accent)> Data = new()
    {
        ["new"] = ("M6 2h8l4 4v16H6z M14 2v4h4", null),
        ["open"] = ("M2 7h7l2 2h10v11H2z M2 9V5h6l2 2", null),
        ["save"] = ("M4 3h13l4 4v14H4z M7 3v6h9V3 M7 21v-7h10v7", null),
        ["undo"] = ("M9 5L4 10l5 5 M4 10h11a5 5 0 0 1 0 10h-4", null),
        ["redo"] = ("M15 5l5 5-5 5 M20 10H9a5 5 0 0 0 0 10h4", null),
        ["rebuild"] = ("M19 12a7 7 0 1 1-2-4.95 M19 3v5h-5", null),
        ["options"] = ("M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z M12 2v3 M12 19v3 M2 12h3 M19 12h3 M5 5l2 2 M17 17l2 2 M19 5l-2 2 M5 19l2-2", null),
        ["sketch"] = ("M3 21l3.5-1 11-11-2.5-2.5-11 11z M14 5.5l2.5 2.5", "M3 3h8v6H3z"),
        ["exitsketch"] = ("M3 21l3.5-1 11-11-2.5-2.5-11 11z", "M13 17l3 3 6-7"),
        ["extrude"] = ("M3 15l9 4 9-4-9-4z M3 15v3l9 4 9-4v-3", "M12 2v8 M9 5l3-3 3 3"),
        ["cut"] = ("M3 9l9 4 9-4-9-4z M3 9v8l9 4 9-4V9", "M12 6v10 M9 13l3 3 3-3"),
        ["revolve"] = ("M12 2v20 M12 5c5 0 8 3 8 7s-3 7-8 7", "M16 19l-4 0 2-3"),
        ["cutrevolve"] = ("M12 2v20 M12 5c5 0 8 3 8 7s-3 7-8 7 M4 12h5", null),
        ["sweep"] = ("M6 17c0-7 5-12 15-12 M9 20c0-5 4-9 12-9", "M3 18.5a4.5 2 0 1 0 9 0a4.5 2 0 1 0-9 0z"),
        ["helical"] = ("M6 5h12 M6 9h12 M6 13h12 M6 17h12 M6 5l12 4 M6 9l12 4 M6 13l12 4", "M12 2v20"),
        ["cuthelical"] = ("M6 5h12 M6 9h12 M6 13h12 M6 17h12 M6 5l12 4 M6 9l12 4 M6 13l12 4 M3 21h18", "M12 2v20"),
        ["thread"] = ("M8 3v18 M16 3v18 M8 5l8 2 M8 9l8 2 M8 13l8 2 M8 17l8 2", "M4 21h16"),
        ["cutsweep"] = ("M6 17c0-7 5-12 15-12 M9 20c0-5 4-9 12-9 M3 18.5a4.5 2 0 1 0 9 0a4.5 2 0 1 0-9 0z", "M15 2v6 M12 5l3 3 3-3"),
        ["fillet"] = ("M4 21V11a7 7 0 0 1 7-7h10", "M4 21h17V4"),
        ["chamfer"] = ("M4 21V10l6-6h11", "M4 21h17V4"),
        ["hole"] = ("M5 6h14v15H5z", "M9 6v15 M15 6v15 M9 10h6"),
        ["shell"] = ("M3 8l9-5 9 5v10l-9 5-9-5z", "M6.5 9.5l5.5-3 5.5 3v7l-5.5 3-5.5-3z"),
        ["lpattern"] = ("M3 3h5v5H3z M10 3h5v5h-5z M17 3h5v5h-5z M3 10h5v5H3z M10 10h5v5h-5z M17 10h5v5h-5z", "M3 20h19 M19 18l3 2-3 2"),
        ["cpattern"] = ("M12 2a2 2 0 1 0 0.01 0z M20 10a2 2 0 1 0 0.01 0z M12 18a2 2 0 1 0 0.01 0z M4 10a2 2 0 1 0 0.01 0z", "M12 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0-2 0"),
        ["mirror"] = ("M3 18L10 6v12z M21 18L14 6v12z", "M12 2v20"),
        // assemblies: two blocks stacked; a block with an arrow in; a block with arrows out (floating); a block with a light outline
        ["assembly"] = ("M3 13h9v8H3z M12 9h9v8h-9z", "M7 5h9v4"),
        ["insertcomp"] = ("M10 10h11v11H10z", "M2 3l6 6 M8 4v5H3"),
        ["float"] = ("M8 8h8v8H8z", "M12 2v4 M12 18v4 M2 12h4 M18 12h4"),
        ["lightweight"] = ("M4 8h12v12H4z", "M14 4h6v6"),
        ["plane"] = ("M2 17l5-10h15l-5 10z", null),
        ["combine"] = ("M9 12m-6 0a6 6 0 1 0 12 0a6 6 0 1 0-12 0 M15 12m-6 0a6 6 0 1 0 12 0a6 6 0 1 0-12 0", null),
        // sheet metal: a bent sheet unrolled flat, its bend line dashed; flatten: folded and flat with an arrow
        ["flatpattern"] = ("M2 17h20v3H2z", "M8 17v3 M16 17v3 M4 4v7a3 3 0 0 0 3 3h10a3 3 0 0 0 3-3V4"),
        ["flatten"] = ("M3 20h18 M3 4v6a3 3 0 0 0 3 3h3", "M14 6l5 5 M19 6v5h-5"),
        ["equations"] = ("M4 8h16 M4 16h16", "M9 3l-2 18 M17 3l-2 18"),
        ["mass"] = ("M5 8h14l2 13H3z M9 8a3 3 0 0 1 6 0", null),
        // Measure: a ruler with ticks
        ["measure"] = ("M3 15l12-12 6 6-12 12z", "M7 11l2 2 M10 8l2 2 M13 5l2 2"),
        ["export"] = ("M4 14v6h16v-6 M12 3v12 M8 11l4 4 4-4", null),
        ["import"] = ("M4 14v6h16v-6 M12 15V3 M8 7l4-4 4 4", null),
        // sketch tools
        ["line"] = ("M4 20L20 4", "M2.5 18.5h3v3h-3z M18.5 2.5h3v3h-3z"),
        ["centerline"] = ("M3 21l2-2 M7 17l2-2 M11 13l2-2 M15 9l2-2 M19 5l2-2", null),
        ["rect"] = ("M3 6h18v12H3z", null),
        ["centerrect"] = ("M3 6h18v12H3z", "M12 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M3 6l18 12"),
        ["circle"] = ("M12 12m-8 0a8 8 0 1 0 16 0a8 8 0 1 0-16 0", "M11 11h2v2h-2z"),
        ["centerarc"] = ("M4 16a8 8 0 0 1 16 0", "M11 15h2v2h-2z M12 16l-8 0"),
        ["arc3"] = ("M3 17C5 7 19 7 21 17", "M2 16h2v2H2z M20 16h2v2h-2z M11 8.5h2v2h-2z"),
        ["tangentarc"] = ("M2 18h9 M11 18a6 6 0 0 0 6-6V6", "M10 17h2v2h-2z"),
        ["point"] = ("M12 12m-2.5 0a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0", null),
        ["polygon"] = ("M7 4h10l5 8-5 8H7l-5-8z", null),
        ["slot"] = ("M7 7h10a5 5 0 0 1 0 10H7a5 5 0 0 1 0-10z", "M7 12h10"),
        ["dimension"] = ("M3 6v12 M21 6v12 M3 12h18 M6 9l-3 3 3 3 M18 9l3 3-3 3", null),
        ["trim"] = ("M6 6l12 12 M18 6L6 18", "M3 12h18"),
        // offset entities: a shape and a copy of it a little way out
        ["offset"] = ("M6 18V10a4 4 0 0 1 4-4h8", "M2 18V10a8 8 0 0 1 8-8h8"),
        // sketch fillet: two lines and the rounded corner between them
        ["sketchfillet"] = ("M4 20V12 M12 4h8", "M4 12a8 8 0 0 1 8-8"),
        ["convert"] = ("M3 3h12v12H3z", "M10 10l11 11 M21 15v6h-6"),
        ["construction"] = ("M3 21l2-2 M7 17l2-2 M11 13l2-2 M15 9l2-2 M19 5l2-2", "M12 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0-6 0"),
        ["relations"] = ("M4 20V4 M4 20h16", "M8 16l8-8"),
        ["fix"] = ("M12 3v12 M7 9h10 M5 15c2 4 12 4 14 0", null),
        // view
        ["fit"] = ("M3 8V3h5 M16 3h5v5 M21 16v5h-5 M8 21H3v-5", "M8 8h8v8H8z"),
        ["views"] = ("M12 2l9 5v10l-9 5-9-5V7z M12 12l9-5 M12 12v10 M12 12L3 7", null),
        ["normalto"] = ("M3 6h18v12H3z", "M12 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0-4 0"),
        ["display"] = ("M12 3l9 5v8l-9 5-9-5V8z", "M12 13l9-5 M12 13v8 M12 13L3 8"),
        ["planes"] = ("M2 17l5-10h15l-5 10z", "M7 7l5 10"),
        ["section"] = ("M3 8l9-5 9 5v8l-9 5-9-5z", "M2 14l20-4"),
        ["ok"] = ("M4 12l5 5L20 6", null),
        ["cancel"] = ("M5 5l14 14 M19 5L5 19", null),
        ["help"] = ("M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .9-1 1.7 M12 16.5v.5", null),
        ["origin"] = ("M12 4v16 M4 12h16", "M12 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0-6 0"),
        ["part"] = ("M3 8l9-5 9 5v8l-9 5-9-5z M3 8l9 5 9-5 M12 13v8", null),
        // PropertyManager: a settings sheet, labels and their value fields
        ["propsheet"] = ("M5 3h14v18H5z M8 8h2.5 M8 12h2.5 M8 16h2.5", "M13 7h3v2h-3z M13 11h3v2h-3z M13 15h3v2h-3z"),
        // pop out: a window with an arrow leaving it at the corner (the list of open parts to a window of its own)
        ["popout"] = ("M11 4H4v16h16v-7", "M14 3h7v7 M21 3l-9 9"),
        // task pane: open parts (sheets stacked), the pin (upright: pinned open; leaning: slides away), collapse
        ["parts"] = ("M8 3h12v14H8z", "M5 6v15h12"),
        ["pinned"] = ("M9 3h6 M10 3v6l-3 4h10l-3-4V3 M12 13v8", null),
        ["unpinned"] = ("M15 3l6 6 M16 4l-4 4-5 0 9 9 0-5 4-4 M8 16l-5 5", null),
        ["collapse"] = ("M6 6l6 6-6 6 M12 6l6 6-6 6", null),
        // dock right: a window with a panel at its right, an arrow going into it (the floating list back to the dock)
        ["dockright"] = ("M3 4h18v16H3z M15 4v16", "M5 12h7 M9 9l3 3-3 3"),
        ["folder"] =("M3 6h6l2 2h10v11H3z", null),
        ["rollback"] = ("M3 12h18", "M8 8l-4 4 4 4"),
        ["warning"] = ("M12 3L2 21h20z M12 10v5 M12 18v.5", null),
    };

    static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(45, 55, 72));
    static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(30, 110, 220));

    public static Control Make(string name, double size = 20, IBrush? stroke = null, IBrush? accent = null)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (Data.TryGetValue(name, out var d))
        {
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(d.path),
                Stroke = stroke ?? Stroke,
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            });
            if (d.accent != null)
                canvas.Children.Add(new Path
                {
                    Data = Geometry.Parse(d.accent),
                    Stroke = accent ?? Accent,
                    StrokeThickness = 1.8,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                });
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
}
