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
        // options: a cog (teeth round a ring, the hole in the middle)
        ["options"] = ("M18.89 9.89L21.88 10.44 21.88 13.56 18.89 14.11 18.36 15.38 20.09 17.88 17.88 20.09 15.38 18.36 14.11 18.89 13.56 21.88 10.44 21.88 9.89 18.89 8.62 18.36 6.12 20.09 3.91 17.88 5.64 15.38 5.11 14.11 2.12 13.56 2.12 10.44 5.11 9.89 5.64 8.62 3.91 6.12 6.12 3.91 8.62 5.64 9.89 5.11 10.44 2.12 13.56 2.12 14.11 5.11 15.38 5.64 17.88 3.91 20.09 6.12 18.36 8.62z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z", null),
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
        // fillet / chamfer: a block's corner, the rounded or cut edge (what the command makes) in the accent
        ["fillet"] = ("M3 21V12 M12 3h9v18H3", "M3 12a9 9 0 0 1 9-9"),
        ["chamfer"] = ("M3 21V12 M12 3h9v18H3", "M3 12l9-9"),
        ["hole"] = ("M5 6h14v15H5z", "M9 6v15 M15 6v15 M9 10h6"),
        ["draft"] = ("M5 19L8.5 5h7L19 19z", "M2 19h20"),
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
        // design table: a grid with its header row; configure: sliders
        ["table"] = ("M3 4h18v16H3z M3 14h18 M9 4v16 M15 4v16", "M3 9h18"),
        ["configure"] = ("M4 6h16 M4 12h16 M4 18h16", "M9 4v4 M15 10v4 M7 16v4"),
        ["mass"] = ("M5 8h14l2 13H3z M9 8a3 3 0 0 1 6 0", null),
        // Measure: a ruler with ticks
        ["measure"] = ("M3 15l12-12 6 6-12 12z", "M7 11l2 2 M10 8l2 2 M13 5l2 2"),
        // export: an arrow leaving the tray; import: one coming into it. Per format: what goes, the arrow leaving
        // (STEP a solid, STL a triangle mesh, DXF a flat pattern with its bend lines)
        ["export"] = ("M4 14v6h16v-6 M12 15V3 M8 7l4-4 4 4", null),
        ["export-step"] = ("M2 12l6-3 6 3v7l-6 3-6-3z M2 12l6 3 6-3 M8 15v7", "M15 9l6-6 M16 3h5v5"),
        ["export-stl"] = ("M2 22l6.5-12 6.5 12z M5.25 16h6.5L8.5 22z", "M15 9l6-6 M16 3h5v5"),
        ["export-dxf"] = ("M2 12h12v10H2z", "M6 12v10 M10 12v10 M15 9l6-6 M16 3h5v5"),
        ["import"] = ("M4 14v6h16v-6 M12 3v12 M8 11l4 4 4-4", null),
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
        // assembly mates: the Mates folder and the Mate command (a paper clip, as SolidWorks'), then one per mate type
        ["mates"] = ("M16 8v9a4 4 0 0 1-8 0V6a2.5 2.5 0 0 1 5 0v10a1 1 0 0 1-2 0V8", null),
        ["mate-coincident"] = ("M4 4h16v8H4z", "M4 12h16v8H4z"),
        ["mate-parallel"] = ("M4 18L14 6", "M10 18L20 6"),
        ["mate-perpendicular"] = ("M4 20h16", "M12 20V4 M12 16h4v4"),
        ["mate-tangent"] = ("M12 9m-6 0a6 6 0 1 0 12 0a6 6 0 1 0-12 0", "M3 15h18"),
        ["mate-concentric"] = ("M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0-18 0", "M12 12m-4 0a4 4 0 1 0 8 0a4 4 0 1 0-8 0"),
        ["mate-lock"] = ("M5 11h14v10H5z", "M8 11V7a4 4 0 0 1 8 0v4"),
        ["mate-distance"] = ("M4 4v16 M20 4v16", "M6 12h12 M9 9l-3 3 3 3 M15 9l3 3-3 3"),
        ["mate-angle"] = ("M4 20h17 M4 20L15 5", "M12 20A8 8 0 0 0 8.7 13.5"),
        ["mate-gear"] = ("M12 12m-6 0a6 6 0 1 0 12 0a6 6 0 1 0-12 0 M12 3v3 M12 18v3 M3 12h3 M18 12h3 M5.6 5.6l2.1 2.1 M16.3 16.3l2.1 2.1 M5.6 18.4l2.1-2.1 M16.3 7.7l2.1-2.1",
            "M12 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0-4 0"),
        ["align-same"] = ("M7 20V5 M4 8l3-3 3 3", "M17 20V5 M14 8l3-3 3 3"),
        ["align-opposite"] = ("M7 20V5 M4 8l3-3 3 3", "M17 4v15 M14 16l3 3 3-3"),
        ["mate-screw"] = ("M8 2v20 M14 2v20 M8 5l6 2 M8 10l6 2 M8 15l6 2", "M19 8v10 M17 16l2 2 2-2"),
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
        ["rollback"] = ("M5 12h16", "M8 8l-4 4 4 4"),
        ["warning"] = ("M12 3L2 21h20z M12 10v5 M12 18v.5", null),
        // menu commands: show / hide (the tree's eye), suppress (a block struck through), rename (a name field and its cursor),
        // delete (a bin), roll forward (the rollback bar's arrow the other way), select (the pointer)
        ["show"] = ("M2 12C5 7 8.5 5 12 5s7 2 10 7c-3 5-6.5 7-10 7s-7-2-10-7z M12 9a3 3 0 1 0 0.01 0z", null),
        ["hide"] = ("M2 12C5 7 8.5 5 12 5s7 2 10 7c-3 5-6.5 7-10 7s-7-2-10-7z", "M4 4l16 16"),
        ["suppress"] = ("M3 8l9-5 9 5v8l-9 5-9-5z", "M3 21L21 3"),
        ["rename"] = ("M3 7h18v10H3z M6 12h5", "M14.5 9v6"),
        ["delete"] = ("M4 6h16 M9 6V3h6v3 M6 6l1 15h10l1-15", "M10 10v7 M14 10v7"),
        ["rollforward"] = ("M3 12h16", "M16 8l4 4-4 4"),
        ["select"] = ("M6 3l13 8-6 1.5-3.5 6.5z", null),
    };

    static IBrush Stroke => Palette.Text;
    static IBrush Accent => Palette.Accent;

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
