using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Model;
using SpryCad.Core.Library;
using SpryCad.Core.Storage;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App;

public partial class MainWindow
{
    bool _showPlanes = false;
    string _activeTab = "Features";
    readonly Dictionary<string, ToggleButton> _tabs = new();
    readonly Dictionary<SketchTool, Button> _toolButtons = new();

    // ------------------------------------------------------------------------------------------
    // menu

    // menu items for parts only (equations, sketches, import...): greyed in an assembly (ApplyDocumentKind)
    readonly List<MenuItem> _partOnlyMenu = new();
    MenuItem PartMenu(MenuItem m) { _partOnlyMenu.Add(m); return m; }

    static MenuItem Item(string header, Action click, string? gesture = null, string? icon = null)
    {
        var m = new MenuItem { Header = header };
        if (gesture != null) m.InputGesture = Avalonia.Input.KeyGesture.Parse(gesture);
        // Avalonia shows a number key by its name ("Ctrl+D1"): the menu's gesture text put as the key reads (Ctrl+1)
        if (m.InputGesture is { } g && g.Key is >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9)
            m.TemplateApplied += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var raw = Avalonia.Controls.Converters.PlatformKeyGestureConverter.ToPlatformString(g);
                var digit = (char)('0' + (g.Key - Avalonia.Input.Key.D0));
                foreach (var tb in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(m).OfType<TextBlock>())
                    if (tb.Text == raw && raw.EndsWith("D" + digit)) tb.Text = raw[..^2] + digit;
            });
        if (icon != null) m.Icon = Icons.Make(icon, 16);
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>
    /// A menu item for an on/off setting (<paramref name="radio"/>: one of a set of choices), marked while it is
    /// on. The mark is read from the setting each time the menu shows, so it is right however it was changed.
    /// </summary>
    static MenuItem Toggle(string header, Func<bool> isOn, Action click, bool radio = false)
    {
        // drawn in the icon spot as the app's check boxes / radio buttons look, so the kind of control and its
        // state show at a glance, unchecked too (a menu's own check mark shows nothing when off)
        // the theme's own check box colors (they follow the system accent), so these match the app's check boxes
        static Avalonia.Media.IBrush Theme(string key, Avalonia.Media.IBrush fallback)
            => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var r) && r is Avalonia.Media.IBrush b ? b : fallback;
        var accent = Theme("CheckBoxCheckBackgroundFillChecked", Palette.Accent);
        var glyph = Theme("CheckBoxCheckGlyphForegroundChecked", Avalonia.Media.Brushes.White);
        var offBorder = Theme("CheckBoxCheckBackgroundStrokeUnchecked", Avalonia.Media.Brushes.Gray);
        var offFill = Theme("CheckBoxCheckBackgroundFillUnchecked", Avalonia.Media.Brushes.White);
        var mark = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(radio ? "M 3,6 A 3,3 0 1 1 9,6 A 3,3 0 1 1 3,6 Z" : "M 2.5,6.5 L 5,9 L 9.5,3.5"),
            Stroke = radio ? null : glyph, StrokeThickness = 1.8,
            Fill = radio ? accent : null,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = 14, Height = 14, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radio ? 7 : 3),
            Child = new Avalonia.Controls.Viewbox { Width = 12, Height = 12, Child = new Canvas { Width = 12, Height = 12, Children = { mark } } },
        };
        var m = new MenuItem { Header = header, Icon = box };
        void Sync()
        {
            var on = isOn();
            mark.IsVisible = on;
            box.BorderBrush = on ? accent : offBorder;
            box.Background = on && !radio ? accent : offFill;
        }
        Sync();
        m.AttachedToVisualTree += (_, _) => Sync();
        m.Click += (_, _) => { click(); Sync(); };
        return m;
    }

    MenuItem StyleItem(string header, DisplayStyle s) => Toggle(header, () => Viewport.Style == s, () => SetStyle(s), radio: true);

    static MenuItem Sub(string header, params object[] items)
    {
        var m = new MenuItem { Header = header };
        foreach (var i in items) m.Items.Add(i is string ? new Separator() : i);
        return m;
    }

    /// <summary>File > Recent Files, refreshed each time it opens.</summary>
    MenuItem RecentMenu()
    {
        var m = new MenuItem { Header = "Recent _Files" };
        void Fill()
        {
            m.Items.Clear();
            var files = RecentFiles.Load();
            if (files.Count == 0) { m.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false }); return; }
            int n = 1;
            foreach (var path in files)
            {
                var p = path;
                var item = new MenuItem { Header = $"_{n++} {Path.GetFileName(p)}" };
                ToolTip.SetTip(item, p);
                item.Click += (_, _) => OpenFile(p);
                m.Items.Add(item);
            }
        }
        Fill();
        m.SubmenuOpened += (_, _) => Fill();
        return m;
    }

    void BuildMenu()
    {
        MainMenu.Items.Add(Sub("_File",
            Item("_New", () => _ = NewFile(), "Ctrl+N", "new"),
            Item("New _Assembly", NewAssembly, null, "new"),
            Item("_Open…", () => _ = OpenFileDialog(), "Ctrl+O", "open"),
            Item("_Save", () => _ = Save(), "Ctrl+S", "save"),
            Item("Save _As…", () => _ = SaveAs(), "Ctrl+Shift+S"),
            Item("_Close", () => _ = CloseTab(_tab), "Ctrl+W"),
            RecentMenu(),
            "-",
            PartMenu(Item("_Import STEP…", () => _ = ImportDialog(), "Ctrl+I", "import")),
            Item("Export _STEP…", () => _ = Export("step"), null, "export-step"),
            Item("Export S_TL…", () => _ = Export("stl"), null, "export-stl"),
            "-",
            Item("Re_visions", () => ShowTaskPane(_revisionsPane, focus: true), null, "revisions"),
            Item("_History…", HistoryCommand),
            Item("Recover Autosave", () => OfferRecovery(atStart: false)),
            "-",
            Item("E_xit", Close)));
        MainMenu.Items.Add(Sub("_Edit",
            Item("_Undo", Undo, "Ctrl+Z", "undo"),
            Item("_Redo", Redo, "Ctrl+Y", "redo"),
            "-",
            Item("_Delete", DeleteSelected, "Delete", "delete"),
            Item("Re_build", () => Rebuild(), "Ctrl+B", "rebuild"),
            Item("Force Rebuild", () => Rebuild(force: true), "Ctrl+Q", "rebuild"),
            "-",
            Item("Suppress", () => SetSuppressed(true), null, "suppress"),
            Item("Unsuppress", () => SetSuppressed(false), null, "part"),
            PartMenu(Item("Roll Forward to End", RollForward, null, "rollforward"))));
        MainMenu.Items.Add(Sub("_View",
            Sub("_Orientation",
                Item("Front", () => Viewport.StandardView("Front"), "Ctrl+D1"),
                Item("Back", () => Viewport.StandardView("Back"), "Ctrl+D2"),
                Item("Left", () => Viewport.StandardView("Left"), "Ctrl+D3"),
                Item("Right", () => Viewport.StandardView("Right"), "Ctrl+D4"),
                Item("Top", () => Viewport.StandardView("Top"), "Ctrl+D5"),
                Item("Bottom", () => Viewport.StandardView("Bottom"), "Ctrl+D6"),
                Item("Isometric", () => Viewport.StandardView("Isometric"), "Ctrl+D7"),
                Item("Trimetric", () => Viewport.StandardView("Trimetric")),
                Item("Dimetric", () => Viewport.StandardView("Dimetric")),
                Item("Normal To", NormalTo, "Ctrl+D8")),
            Item("Zoom to _Fit", () => Viewport.ZoomToFit(), "F", "fit"),
            "-",
            Sub("_Display Style",
                StyleItem("Shaded With Edges", DisplayStyle.ShadedWithEdges),
                StyleItem("Shaded", DisplayStyle.Shaded),
                StyleItem("Hidden Lines Visible", DisplayStyle.HiddenLinesVisible),
                StyleItem("Wireframe", DisplayStyle.Wireframe)),
            _colorByMenu = Toggle("Color by _Feature", () => _colorByFeature, ToggleColorByFeature),
            Toggle("_Section View", () => _sectionOn, SectionCommand),
            Toggle("Perspective", () => Viewport.Camera.Perspective, () => { Viewport.Camera.Perspective = !Viewport.Camera.Perspective; Viewport.Redraw(); }),
            Sub("_Threads",
                Toggle("As Set in Each Thread", () => ThreadMode == ThreadDisplay.AsSet, () => _ = SetThreadMode(ThreadDisplay.AsSet), radio: true),
                Toggle("All Modeled", () => ThreadMode == ThreadDisplay.Modeled, () => _ = SetThreadMode(ThreadDisplay.Modeled), radio: true),
                Toggle("All Cosmetic (lightweight)", () => ThreadMode == ThreadDisplay.Cosmetic, () => _ = SetThreadMode(ThreadDisplay.Cosmetic), radio: true)),
            "-",
            Toggle("Planes", () => _showPlanes, TogglePlanes),
            Toggle("Origins", () => Viewport.ShowOrigin, () => { Viewport.ShowOrigin = !Viewport.ShowOrigin; Viewport.Redraw(); }),
            PartMenu(Toggle("Sketch Relations", () => _sketch?.ShowRelations ?? true, () => { if (_sketch != null) { _sketch.ShowRelations = !_sketch.ShowRelations; _sketch.UpdateOverlay(Viewport); } }))));
        MainMenu.Items.Add(Sub("_Insert",
            Item("_Sketch", () => StartSketchCommand(), null, "sketch"),
            "-",
            Sub("_Boss/Base",
                Item("_Extrude…", () => ExtrudeCommand(false), null, "extrude"),
                Item("_Revolve…", () => RevolveCommand(false), null, "revolve"),
                Item("S_wept…", () => SweepCommand(false), null, "sweep"),
                Item("_Lofted…", () => LoftCommand(false), null, "loft"),
                Item("_Helical…", () => HelicalCommand(false), null, "helical")),
            Sub("_Cut",
                Item("_Extrude…", () => ExtrudeCommand(true), null, "cut"),
                Item("_Revolve…", () => RevolveCommand(true), null, "cutrevolve"),
                Item("S_wept…", () => SweepCommand(true), null, "cutsweep"),
                Item("_Lofted…", () => LoftCommand(true), null, "cutloft"),
                Item("_Helical…", () => HelicalCommand(true), null, "cuthelical")),
            Sub("_Features",
                Item("_Fillet…", () => BlendCommand(true), null, "fillet"),
                Item("_Chamfer…", () => BlendCommand(false), null, "chamfer"),
                Item("_Hole Wizard…", HoleCommand, null, "hole"),
                Item("_Thread…", ThreadCommand, null, "thread"),
                Item("_Shell…", ShellCommand, null, "shell"),
                Item("_Draft…", DraftCommand, null, "draft"),
                Item("C_ombine…", CombineCommand, null, "combine")),
            Sub("_Pattern/Mirror",
                Item("_Linear Pattern…", LinearPatternCommand, null, "lpattern"),
                Item("_Circular Pattern…", CircularPatternCommand, null, "cpattern"),
                Item("_Mirror…", MirrorCommand, null, "mirror")),
            Sub("_Reference Geometry",
                Item("_Plane…", PlaneCommand, null, "plane"),
                Item("_Axis…", AxisCommand, null, "axis")),
            Sub("Sheet _Metal",
                Item("_Base Flange…", BaseFlangeCommand, null, "baseflange"),
                Item("_Edge Flange…", EdgeFlangeCommand, null, "edgeflange"),
                Item("_Hem…", HemCommand, null, "hem"),
                Item("Sheet C_ut…", SheetCutCommand, null, "sheetcut"),
                Item("_Corners…", CornersCommand, null, "corners"),
                Item("Fla_tten", ToggleFlattenCommand, null, "flatten"),
                Item("E_xport Flat Pattern…", () => _ = ExportFlatCommand(), null, "export-dxf"))));
        MainMenu.Items.Add(Sub("_Tools",
            // (equations and design tables in one window)
            PartMenu(Item("_Parameters…", () => _ = Dialogs.Equations(this), null, "equations")),
            PartMenu(Item("_Configure…", () => _ = Dialogs.ConfigurePart(this), null, "configure")),
            Item("Mea_sure…", MeasureCommand, null, "measure"),
            Item("_Mass Properties…", MassPropertiesCommand, null, "mass"),
            PartMenu(Item("Edit Ma_terial…", EditMaterialCommand, null, "material")),
            PartMenu(Item("_Appearance…", () => _ = PartAppearanceCommand(), null, "appearance")),
            PartMenu(Item("_Library Updates…", () => _ = LibraryUpdatesCommand(), null, "table")),
            Sub("Li_braries",
                Item("_Edit Library…", () => _ = LibraryEditor.Open(this), null, "table"),
                Item("_New Company Library…", () => _ = LibraryEditor.New(this))),
            "-",
            Item("_Options…", () => _ = Dialogs.Options(this), null, "options")));
        MainMenu.Items.Add(Sub("_Help",
            Item("_Keyboard Shortcuts", () => _ = Dialogs.Shortcuts(this), null, "help"),
            Item("Check for _Updates…", () => _ = Updater.CheckNow(this)),
            Item("_Change Log…", () => _ = Updater.ShowChangelog(this)),
            Item("_About SpryCAD", () => _ = Dialogs.About(this))));
    }

    Button? _undoButton, _redoButton;

    /// <summary>Greys out Undo/Redo when there is nothing to undo/redo (sketch mode keeps its own stack).</summary>
    void UpdateUndoButtons()
    {
        if (_undoButton == null || _redoButton == null) return;
        _undoButton.IsEnabled = _sketch != null || (Asm?.CanUndo ?? Session.CanUndo);
        _redoButton.IsEnabled = _sketch != null || (Asm?.CanRedo ?? Session.CanRedo);
    }

    void BuildQuickAccess()
    {
        Button B(string icon, string tip, Action a)
        {
            var b = new Button { Classes = { "tb" }, Content = Icons.Make(icon, 18) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            QuickAccess.Children.Add(b);
            return b;
        }
        B("new", "New (Ctrl+N)", () => _ = NewFile());
        B("open", "Open (Ctrl+O)", () => _ = OpenFileDialog());
        B("save", "Save (Ctrl+S)", () => _ = Save());
        _undoButton = B("undo", "Undo (Ctrl+Z)", Undo);
        _redoButton = B("redo", "Redo (Ctrl+Y)", Redo);
        B("rebuild", "Rebuild (Ctrl+B)", () => Rebuild());
        B("options", "Options", () => _ = Dialogs.Options(this));
    }

    // ------------------------------------------------------------------------------------------
    // CommandManager

    void BuildCommandManager()
    {
        // right-click the bar: its layout
        if (CommandBar.Parent is Control barHost)
            barHost.ContextRequested += (_, e) =>
            {
                var menu = new ContextMenu();
                foreach (var (layout, text) in new[] { (CommandBarLayout.Large, "Large Buttons"), (CommandBarLayout.Compact, "Compact Buttons") })
                {
                    var mi = new MenuItem { Header = text, ToggleType = MenuItemToggleType.Radio, IsChecked = UserSettings.CommandBar == layout };
                    mi.Click += (_, _) => SetCommandBarLayout(layout);
                    menu.Items.Add(mi);
                }
                menu.Open(barHost);
                e.Handled = true;
            };
        foreach (var name in new[] { "Assembly", "Features", "Sketch", "Sheet Metal", "Evaluate" })
        {
            // the Assembly tab only while an assembly is active (ApplyDocumentKind)
            var t = new ToggleButton { Content = name, Classes = { "tab", "below" }, IsVisible = name != "Assembly" };
            t.Click += (_, _) => ShowTab(name);
            _tabs[name] = t;
            CommandTabs.Children.Add(t);
        }
        ShowTab("Features");
    }

    public void ShowTab(string name)
    {
        _activeTab = name;
        foreach (var (n, t) in _tabs) t.IsChecked = n == name;
        CommandBar.Children.Clear();
        _toolButtons.Clear();
        _cmdLabels.Clear();
        _dropGroups.Clear();
        _barCommands.Clear();
        Command T(string icon, string label, SketchTool tool) => new(icon, label, () => SetSketchTool(tool), tool);
        switch (name)
        {
            case "Assembly":
                AssemblyCommands();
                break;
            case "Features":
                // the families on split buttons (each shows the one used last)
                Family("Boss",
                    new("extrude", "Extruded Boss/Base", () => ExtrudeCommand(false)),
                    new("revolve", "Revolved Boss/Base", () => RevolveCommand(false)),
                    new("sweep", "Swept Boss/Base", () => SweepCommand(false)),
                    new("loft", "Lofted Boss/Base", () => LoftCommand(false)),
                    new("helical", "Helical Boss/Base", () => HelicalCommand(false)));
                Family("Cut",
                    new("cut", "Extruded Cut", () => ExtrudeCommand(true)),
                    new("cutrevolve", "Revolved Cut", () => RevolveCommand(true)),
                    new("cutsweep", "Swept Cut", () => SweepCommand(true)),
                    new("cutloft", "Lofted Cut", () => LoftCommand(true)),
                    new("cuthelical", "Helical Cut", () => HelicalCommand(true)));
                Family("Hole", new("hole", "Hole Wizard", HoleCommand), new("thread", "Thread", ThreadCommand));
                Sep();
                Family("Fillet", new("fillet", "Fillet", () => BlendCommand(true)), new("chamfer", "Chamfer", () => BlendCommand(false)));
                Family("Shell", new("shell", "Shell", ShellCommand), new("draft", "Draft", DraftCommand));
                Sep();
                Family("Pattern",
                    new("lpattern", "Linear Pattern", LinearPatternCommand),
                    new("cpattern", "Circular Pattern", CircularPatternCommand),
                    new("mirror", "Mirror", MirrorCommand));
                Family("Reference", new("plane", "Reference Plane", PlaneCommand), new("axis", "Reference Axis", AxisCommand));
                Cmd("combine", "Combine", CombineCommand);
                break;
            case "Sketch":
                if (_sketch == null) Cmd("sketch", "Sketch", () => StartSketchCommand());
                else Cmd("exitsketch", "Exit Sketch", () => ExitSketch(true));
                Cmd("dimension", "Smart Dimension", () => SetSketchTool(SketchTool.SmartDimension), SketchTool.SmartDimension);
                Sep();
                // the kinds of a shape on one button, named by the kind shown
                Family(null, T("line", "Line", SketchTool.Line), T("centerline", "Centerline", SketchTool.Centerline), T("spline", "Spline", SketchTool.Spline));
                Family(null, T("rect", "Corner Rectangle", SketchTool.Rectangle), T("centerrect", "Center Rectangle", SketchTool.CenterRectangle));
                Cmd("circle", "Circle", () => SetSketchTool(SketchTool.Circle), SketchTool.Circle);
                Family(null, T("centerarc", "Centerpoint Arc", SketchTool.CenterArc), T("arc3", "3 Point Arc", SketchTool.ThreePointArc),
                    T("tangentarc", "Tangent Arc", SketchTool.TangentArc));
                Family(null, T("slot", "Straight Slot", SketchTool.Slot), T("polygon", "Polygon", SketchTool.Polygon));
                Cmd("point", "Point", () => SetSketchTool(SketchTool.Point), SketchTool.Point);
                Sep();
                Cmd("trim", "Trim Entities", () => SetSketchTool(SketchTool.Trim), SketchTool.Trim);
                Cmd("sketchfillet", "Sketch Fillet", () => { if (_sketch == null) { SetSketchTool(SketchTool.Fillet); return; } _sketch.StartFillet(); UpdateToolButtons(); BuildSketchPage(); }, SketchTool.Fillet);
                Family(null, new("offset", "Offset Entities", OffsetEntities), new("sketchmirror", "Mirror Entities", MirrorEntities),
                    new("convert", "Convert Entities", ConvertEntities));
                Cmd("construction", "Construction Geometry", ToggleConstruction);
                Cmd("relations", "Add Relations", AddRelationCommand);
                Cmd("normalto", "Normal To", NormalTo);
                break;
            case "Sheet Metal":
                Cmd("baseflange", "Base\nFlange", BaseFlangeCommand);
                Cmd("edgeflange", "Edge\nFlange", EdgeFlangeCommand);
                Cmd("hem", "Hem", HemCommand);
                Cmd("sheetcut", "Sheet\nCut", SheetCutCommand);
                Cmd("corners", "Corners", CornersCommand);
                Cmd("flatten", "Flatten", ToggleFlattenCommand);
                Sep();
                Cmd("export-dxf", "Export\nFlat", () => _ = ExportFlatCommand());
                break;
            case "Evaluate":
                // (in an assembly only what works on one: measuring, its mass properties and its export)
                Cmd("measure", "Measure", MeasureCommand);
                Cmd("mass", "Mass\nProperties", MassPropertiesCommand);
                if (Asm == null)
                {
                    Cmd("material", "Edit\nMaterial", EditMaterialCommand);
                    Cmd("equations", "Parameters", () => _ = Dialogs.Equations(this));
                    Cmd("table", "Design\nTable", () => _ = Dialogs.DesignTables(this));
                    Cmd("configure", "Configure", () => _ = Dialogs.ConfigurePart(this));
                }
                Sep();
                Cmd("export-step", "Export\nSTEP", () => _ = Export("step"));
                Cmd("export-stl", "Export\nSTL", () => _ = Export("stl"));
                break;
        }
        UpdateToolButtons();
        FitCommandRow();
    }

    /// <summary>A command's button on the row: its icon and its name (a "\n" in the name is a space).</summary>
    void Cmd(string icon, string label, Action click, SketchTool? tool = null)
    {
        var name = label.Replace('\n', ' ');
        var b = new Button { Classes = { "cmd", LargeBar ? "big" : "compact" }, Content = RowContent(icon, name) };
        var key = tool is { } st && SketchToolKeys.TryGetValue(st, out var k) ? $" ({k})" : "";
        ToolTip.SetTip(b, CommandHelp.TryGetValue(name, out var help) ? $"{name}{key}\n{help}" : name + key);
        b.Click += (_, _) => { NoteCommand(name, icon, click); click(); };
        if (tool != null) _toolButtons[tool.Value] = b;
        _barCommands[b] = new[] { new Command(icon, name, click, tool) };
        CommandBar.Children.Add(b);
    }

    internal static readonly Dictionary<SketchTool, string> SketchToolKeys = new()
    {
        [SketchTool.Line] = "L", [SketchTool.Rectangle] = "R", [SketchTool.Circle] = "C", [SketchTool.CenterArc] = "A",
        [SketchTool.SmartDimension] = "D", [SketchTool.Point] = "P", [SketchTool.Trim] = "T", [SketchTool.Spline] = "S",
    };

    /// <summary>One-line descriptions shown under the command name in toolbar tooltips.</summary>
    static readonly Dictionary<string, string> CommandHelp = new()
    {
        ["Extruded Boss/Base"] = "Extrudes a sketch profile to add material.",
        ["Extruded Cut"] = "Extrudes a sketch profile to remove material.",
        ["Revolved Boss/Base"] = "Revolves a sketch profile about a centerline to add material.",
        ["Revolved Cut"] = "Revolves a sketch profile about a centerline to remove material.",
        ["Swept Boss/Base"] = "Sweeps a closed profile along a path of lines and arcs to add material.",
        ["Swept Cut"] = "Sweeps a closed profile along a path of lines and arcs to remove material.",
        ["Lofted Boss/Base"] = "Adds material by lofting between profiles, optionally along guide curves.",
        ["Lofted Cut"] = "Removes material by lofting between profiles, optionally along guide curves.",
        ["Hole Wizard"] = "Creates simple, counterbore or countersink holes on a planar face.",
        ["Helical Boss/Base"] = "Sweeps a closed profile along a helix about a sketch centerline to add material.",
        ["Helical Cut"] = "Sweeps a closed profile along a helix about a sketch centerline to remove material.",
        ["Thread"] = "Adds a modeled or cosmetic metric thread to a cylindrical face.",
        ["Fillet"] = "Rounds the selected edges or faces.",
        ["Chamfer"] = "Bevels the selected edges or faces.",
        ["Shell"] = "Hollows the part, leaving the selected faces open.",
        ["Draft"] = "Tapers faces by an angle from a neutral plane for mold release.",
        ["Linear Pattern"] = "Copies features along one or two directions.",
        ["Circular Pattern"] = "Copies features around an axis.",
        ["Mirror"] = "Mirrors features about a plane or planar face.",
        ["Reference Plane"] = "Creates a plane offset from a plane or planar face.",
        ["Combine"] = "Adds, subtracts or intersects bodies.",
        ["Base Flange"] = "Creates a sheet metal part from a closed sketch (a plate) or open lines (a bent profile), using the shop profile's material and gauge.",
        ["Edge Flange"] = "Adds flanges to sheet edges, measured to the outside or inside mold line or the tangent, with a bend angle and direction.",
        ["Hem"] = "Folds sheet edges back on themselves (closed, open or teardrop), checked against the shop's hem rules.",
        ["Sheet Cut"] = "Cuts closed sketch profiles normal to the sheet, through every parallel panel they lie on.",
        ["Corners"] = "Lists where flanges meet, how each corner is made and its gap check; each corner's treatment and gap can be overridden.",
        ["Flatten"] = "Toggles the sheet metal between flattened and folded.",
        ["Export Flat"] = "Exports the flat pattern as DXF or SVG on the shop profile's layers, with bend notes and a bend table.",
        ["Sketch"] = "Starts a sketch on the selected plane or planar face.",
        ["Smart Dimension"] = "Dimensions sketch entities and model edges or vertices.",
        ["Line"] = "Sketches lines, adding relations when they snap to entities, model edges, midpoints and centers.",
        ["Convert Entities"] = "Projects the selected model edges into the sketch.",
        ["Trim Entities"] = "Trims sketch entities at their intersections.",
        ["Sketch Fillet"] = "Rounds the corner where two lines meet, keeping the lines as construction geometry.",
        ["Offset Entities"] = "Offsets the selected sketch chain by a dimensioned distance, linked to the original.",
        ["Mirror Entities"] = "Mirrors the selected entities about a centerline, keeping the copies linked.",
        ["Construction Geometry"] = "Toggles the selected sketch entities to construction.",
        ["Add Relations"] = "Adds relations such as coincident, horizontal or tangent between the selected entities.",
        ["Measure"] = "Measures distance, angle, radius, length and area of the selected faces, edges and vertices.",
        ["Mass Properties"] = "Calculates mass, volume, surface area, center of mass and moments of inertia from the part's material.",
        ["Edit Material"] = "Assigns a material from the libraries: its density for mass properties, its properties and its appearance.",
        ["Equations"] = "Defines global variables that drive dimensions and conditions that suppress features.",
        ["Design Table"] = "Defines rows of variable values; selecting a row applies them all.",
        ["Configure"] = "Sets the part's design table rows and the variables marked User.",
    };

    void Sep() => CommandBar.Children.Add(new Border { Width = 1, Background = Palette.Border, Margin = new Thickness(4, 6), Classes = { "barsep" } });

    void UpdateToolButtons()
    {
        UpdateDropGroups();
        // tool buttons stay enabled: picking a sketch tool outside a sketch starts one (then pick a plane/face)
        foreach (var (t, b) in _toolButtons)
            if (_sketch != null && _sketch.Tool == t) b.Classes.Add("active"); else b.Classes.Remove("active");
        UpdateUndoButtons();
    }

    // ------------------------------------------------------------------------------------------
    // panel tabs (FeatureManager / PropertyManager)

    /// <summary>A tab of the left panel: its header and the panel it shows.</summary>
    sealed record PanelTab(string Key, TabStripItem Header, Control Content, Func<bool> Available);

    readonly List<PanelTab> _panelTabs = new();
    // the tab shown before a page opened, shown again when it closes
    string _tabBeforePage = TreeTab;
    bool _syncingTabs;
    const string TreeTab = "tree", PropertyTab = "properties";

    void BuildPanelTabs()
    {
        AddPanelTab(TreeTab, "part", "FeatureManager Design Tree", TreePanel());
        AddPanelTab(PropertyTab, "propsheet", "PropertyManager", PmHost,
            available: () => _page != null);
        PanelTabs.SelectionChanged += (_, _) =>
        {
            if (_syncingTabs) return;
            if (PanelTabs.SelectedItem is TabStripItem { Tag: string key }) ShowPanel(key);
        };
        ShowPanel(TreeTab);
    }

    /// <summary>
    /// Adds a tab to the left panel (e.g. a ConfigurationManager later): an icon header with a tooltip, and its
    /// panel. <paramref name="available"/>: whether the tab is offered right now (always, by default).
    /// </summary>
    void AddPanelTab(string key, string icon, string tip, Control content, Func<bool>? available = null)
    {
        var header = new TabStripItem { Content = Icons.Make(icon, 18), Tag = key };
        ToolTip.SetTip(header, tip);
        if (content.Parent == null) PanelHost.Children.Add(content);
        _panelTabs.Add(new PanelTab(key, header, content, available ?? (() => true)));
        PanelTabs.Items.Add(header);
    }

    /// <summary>Shows a tab of the left panel (if it is offered now; otherwise the design tree).</summary>
    void ShowPanel(string key)
    {
        var tab = _panelTabs.FirstOrDefault(t => t.Key == key && t.Available()) ?? _panelTabs[0];
        foreach (var t in _panelTabs)
        {
            t.Header.IsVisible = t.Available();
            t.Content.IsVisible = t == tab;
        }
        _syncingTabs = true;
        PanelTabs.SelectedItem = tab.Header;
        _syncingTabs = false;
        UpdateFlyoutTree();
    }

    /// <summary>
    /// A page opened or closed: the PropertyManager tab comes and goes with it, and is shown while the page is
    /// open; on close, the tab shown before it returns.
    /// </summary>
    void UpdatePanelTabs()
    {
        if (_panelTabs.Count == 0) return;
        var current = _panelTabs.FirstOrDefault(t => t.Content.IsVisible)?.Key ?? TreeTab;
        var opened = _page != null && !_pageShown;
        _pageShown = _page != null;
        if (opened)
        {
            // a page opened: show it. A page rebuilt while open (the sketch page, on each selection) keeps the
            // tab the user chose, so the tree can stay up mid-edit
            if (current != PropertyTab) _tabBeforePage = current;
            ShowPanel(PropertyTab);
        }
        else if (_page == null) ShowPanel(current == PropertyTab ? _tabBeforePage : current);
        else ShowPanel(current);
    }

    bool _pageShown;

    // ------------------------------------------------------------------------------------------
    // heads-up view toolbar

    void BuildHud()
    {
        Button H(string icon, string tip, Action a)
        {
            var b = new Button { Classes = { "tb" }, Content = Icons.Make(icon, 18) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            HudBar.Children.Add(b);
            return b;
        }
        H("fit", "Zoom to Fit (F)", () => Viewport.ZoomToFit());
        var views = H("views", "View Orientation (Spacebar)", () => { });
        views.Flyout = OrientationFlyout();
        H("normalto", "Normal To (Ctrl+8)", NormalTo);
        var disp = H("display", "Display Style", () => { });
        var df = new MenuFlyout();
        df.Items.Add(StyleItem("Shaded With Edges", DisplayStyle.ShadedWithEdges));
        df.Items.Add(StyleItem("Shaded", DisplayStyle.Shaded));
        df.Items.Add(StyleItem("Hidden Lines Visible", DisplayStyle.HiddenLinesVisible));
        df.Items.Add(StyleItem("Wireframe", DisplayStyle.Wireframe));
        disp.Flyout = df;
        _planesButton = H("planes", "Hide/Show Planes", TogglePlanes);
        _colorByButton = H("colorbyfeature", "Color by Feature", ToggleColorByFeature);
        _sectionButton = H("section", "Section View", SectionCommand);
    }

    Flyout OrientationFlyout()
    {
        var grid = new WrapPanel { Width = 250 };
        var f = new Flyout { Content = grid, Placement = PlacementMode.Bottom };
        foreach (var v in new[] { "Front", "Back", "Left", "Right", "Top", "Bottom", "Isometric", "Trimetric", "Dimetric" })
        {
            var b = new Button { Content = v, Width = 78, Margin = new Thickness(2), FontSize = UI.TypeScale.Small };
            b.Click += (_, _) => { Viewport.StandardView(v); f.Hide(); };
            grid.Children.Add(b);
        }
        var nb = new Button { Content = "Normal To", Width = 78, Margin = new Thickness(2), FontSize = UI.TypeScale.Small };
        nb.Click += (_, _) => { NormalTo(); f.Hide(); };
        grid.Children.Add(nb);
        var sel = new Button { Content = "View Selector (Spacebar)", Margin = new Thickness(2), FontSize = UI.TypeScale.Small, HorizontalAlignment = HorizontalAlignment.Stretch };
        sel.Click += (_, _) => { f.Hide(); _viewSelector.Open(); };
        grid.Children.Add(sel);
        return f;
    }

    void SetStyle(DisplayStyle s) { Viewport.Style = s; Viewport.Redraw(); }

    void TogglePlanes() { _showPlanes = !_showPlanes; RefreshScene(); }

    /// <summary>View > Color by Feature: each face in its feature's color, with a swatch of it on the feature's tree row.</summary>
    bool _colorByFeature;
    // the view bar's toggles, shown pressed while on
    Button? _planesButton, _sectionButton;

    void UpdateHudStates()
    {
        _planesButton?.Classes.Set("active", _showPlanes);
        _colorByButton?.Classes.Set("active", _colorByFeature);
        _sectionButton?.Classes.Set("active", _sectionOn);
    }

    Button? _colorByButton;
    MenuItem? _colorByMenu;

    /// <summary>Color by Feature in a part, Color by Part in an assembly: the button's tip and the menu item say which.</summary>
    void UpdateColorByLabels()
    {
        if (_colorByButton != null) ToolTip.SetTip(_colorByButton, Asm != null ? "Color by Part" : "Color by Feature");
        if (_colorByMenu != null) _colorByMenu.Header = Asm != null ? "Color by _Part" : "Color by _Feature";
    }

    void ToggleColorByFeature()
    {
        _colorByFeature = !_colorByFeature;
        RebuildTree();
        RefreshScene();
    }

    /// <summary>The feature tree's color swatches (Color by Feature on, a part shown), else null.</summary>
    IReadOnlyDictionary<string, Avalonia.Media.Color>? FeatureSwatches() => _colorByFeature && Asm == null
        ? FeatureColors.Of(Session.Doc).ToDictionary(kv => kv.Key, kv => Avalonia.Media.Color.FromRgb((byte)Math.Round(kv.Value[0] * 255), (byte)Math.Round(kv.Value[1] * 255), (byte)Math.Round(kv.Value[2] * 255)))
        : null;

    void NormalTo()
    {
        Frame? f = null;
        if (_sketch != null && Selection.First is not { Kind: SelKind.Face, Planar: true }) f = _sketch.Frame;
        else if (Selection.First is { } s)
        {
            if (s.Kind == SelKind.Face && s.FacePlane != null) f = Frame.FromPlane(s.FacePlane);
            else if (s.Frame != null) f = s.Frame;
        }
        // an assembly: an origin plane picked in the tree, else the next flat face of a component clicked
        if (f == null && Asm != null)
        {
            if (_asmOriginSel is "Front" or "Top" or "Right") f = Resolver.StdPlane(_asmOriginSel);
            else
            {
                _normalToPick = true;
                RefreshAssemblyScene();
                Status("Normal To: select a planar face of a component (Esc to cancel).");
                return;
            }
        }
        if (f == null) { Status("Select a planar face, plane or sketch for Normal To."); return; }
        Viewport.NormalTo(f.Value);
    }

    // Normal To in an assembly, waiting for the face to look at
    bool _normalToPick;

    /// <summary>The face clicked for Normal To in an assembly: a flat one is looked at straight on.</summary>
    void NormalToFace(string component, string face)
    {
        if (Asm is not { } a) return;
        if (a.Model.RefTo(component, face) is not { } r || a.Model.World(r) is not { Kind: Core.Assembly.MateGeomKind.Plane } g)
        { Status($"The selected face of {a.Model.NameAt(component)} is not planar. Select a planar face (Esc to cancel)."); return; }
        _normalToPick = false;
        RefreshAssemblyScene();
        Viewport.NormalTo(Frame.FromPlane(Plane.FromPointNormal(g.P, g.D)));
        Status($"Normal to a face of {a.Model.NameAt(component)}.");
    }

    // ------------------------------------------------------------------------------------------
    // context menus

    // ------------------------------------------------------------------------------------------
    // mouse gestures (right-drag, as SolidWorks)

    GestureWheel? _gestures;

    void BuildGestures()
    {
        _gestures = new GestureWheel();
        GraphicsArea.Children.Add(_gestures);
        Viewport.GestureBegan += p => _gestures.Open(p, GestureCommands());
        Viewport.GestureMoved += p => _gestures.Move(p);
        Viewport.GestureEnded += p => _gestures.End(p);
    }

    /// <summary>The wheel's commands (right, up, left, down): sketch tools while sketching, else model ones.</summary>
    GestureWheel.Command?[] GestureCommands() => _sketch != null
        ? new GestureWheel.Command?[]
        {
            new("dimension", "Smart Dimension", () => SetSketchTool(SketchTool.SmartDimension)),
            new("circle", "Circle", () => SetSketchTool(SketchTool.Circle)),
            new("line", "Line", () => SetSketchTool(SketchTool.Line)),
            new("rect", "Rectangle", () => SetSketchTool(SketchTool.Rectangle)),
        }
        // (an assembly: its own commands where a part has Measure and Sketch)
        : Asm != null
        ? new GestureWheel.Command?[]
        {
            new("mates", "Mate", MateCommand),
            new("normalto", "Normal To", NormalTo),
            new("fit", "Zoom to Fit", () => Viewport.ZoomToFit()),
            new("insertcomp", "Insert Components", InsertComponentCommand),
        }
        : new GestureWheel.Command?[]
        {
            new("measure", "Measure", MeasureCommand),
            new("normalto", "Normal To", NormalTo),
            new("fit", "Zoom to Fit", () => Viewport.ZoomToFit()),
            new("sketch", "Sketch", () => StartSketchCommand()),
        };

    /// <summary>
    /// An axis or center chosen from the right-click menu: into the open page's selection box, as a click would put
    /// it (the box must take one); else selected.
    /// </summary>
    void PickSpecial(SelItem item, string what)
    {
        if (_page?.ActiveBox != null)
        {
            if (_page.HandlePick(item)) UpdateEmphasis();
            else Status($"No selection box on this page accepts {what}.");
        }
        else Selection.Set(item);
        Viewport.Redraw();
    }

    void OnViewportContext(PointerInfo p)
    {
        var menu = new ContextMenu();
        if (_sketch != null)
        {
            menu.Items.Add(Item("Select", () => SetSketchTool(SketchTool.Select), null, "select"));
            menu.Items.Add(Item("End Chain / Cancel", () => _sketch.CancelOperation(), null, "cancel"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Line", () => SetSketchTool(SketchTool.Line), "L", "line"));
            menu.Items.Add(Item("Rectangle", () => SetSketchTool(SketchTool.Rectangle), "R", "rect"));
            menu.Items.Add(Item("Circle", () => SetSketchTool(SketchTool.Circle), "C", "circle"));
            menu.Items.Add(Item("Smart Dimension", () => SetSketchTool(SketchTool.SmartDimension), "D", "dimension"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Exit Sketch", () => ExitSketch(true), null, "exitsketch"));
            menu.Items.Add(Item("Discard Changes", DiscardSketch, null, "cancel"));
        }
        else
        {
            // picking mate references: a round face's axis (as a click on it), or the center of its circle at the end
            // nearer the pointer (a hole's, a boss's)
            if (Asm is { } asm && PickingFaces && Viewport.PickInstance(p.X, p.Y) is { face: { } mf } mh
                && asm.Model.FaceGeom(mh.id, mf) is { Kind: Core.Assembly.MateGeomKind.Axis })
            {
                menu.Items.Add(Item("Select Axis", () => PickFace(mh.id, mf)));
                menu.Items.Add(Item("Select Center", () => PickRef(asm.Model.RefTo(mh.id, mf, asm.Model.NearerEnd(mh.id, mf, mh.point)), "The selected face has no circular edge.")));
                menu.Items.Add(new Separator());
            }
            // a component right-clicked in an assembly: selected, as a click selects it, and its menu as its row in the
            // tree has it (the view's commands after)
            else if (Asm is { } ca && _page == null && Viewport.PickInstance(p.X, p.Y) is { } ch)
            {
                SelectComponent(ch.id);
                if (ca.Selected is { } sel && _asmTree.ComponentMenu(sel) is { } cm) menu = cm;
            }
            var hit = Viewport.Pick(p.X, p.Y, SelFilter.Any);
            if (hit != null && !Selection.Contains(hit)) Selection.Set(hit);
            if (hit?.Kind == SelKind.Face && hit.Planar || hit?.Kind == SelKind.Plane)
            {
                // (sketches and holes are a part's: not offered on an assembly's origin plane)
                if (Asm == null) menu.Items.Add(Item("Sketch", () => StartSketchCommand(), null, "sketch"));
                if (Asm == null) menu.Items.Add(Item("Hole Wizard", HoleCommand, null, "hole"));
                menu.Items.Add(Item("Normal To", NormalTo, null, "normalto"));
            }
            if (hit?.Kind == SelKind.Edge)
            {
                menu.Items.Add(Item("Fillet", () => BlendCommand(true), null, "fillet"));
                menu.Items.Add(Item("Chamfer", () => BlendCommand(false), null, "chamfer"));
            }
            // a round face or circular edge: its axis or the center of its circle, picked as anything else is
            var axis = hit != null ? Viewport.AxisOf(hit) : null;
            var center = hit != null ? Viewport.CenterOf(hit) : null;
            if (axis != null || center != null)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                if (axis != null) menu.Items.Add(Item("Select Axis", () => PickSpecial(axis, "an axis")));
                if (center != null) menu.Items.Add(Item("Select Center", () => PickSpecial(center, "a center")));
            }
            if (hit?.Kind == SelKind.Sketch)
            {
                menu.Items.Add(Item("Edit Sketch", () => EditFeature(hit.FeatureId!), null, "sketch"));
                menu.Items.Add(Item("Extruded Boss/Base", () => ExtrudeCommand(false), null, "extrude"));
                menu.Items.Add(Item("Extruded Cut", () => ExtrudeCommand(true), null, "cut"));
                menu.Items.Add(Item("Revolve", () => RevolveCommand(false), null, "revolve"));
            }
            var fid = hit?.OwningFeatureId;
            if (fid != null && Session.Doc.Find(fid) is { } owner)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(Item($"Edit Feature ({owner.Name})", () => EditFeature(owner.Id)));
                var sk = owner.Consumes.FirstOrDefault(c => Session.Doc.Find(c) is SketchFeature);
                if (sk != null) menu.Items.Add(Item("Edit Sketch", () => EditFeature(sk), null, "sketch"));
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            // measures what was right-clicked (and the rest of the selection), as SolidWorks
            if (_page != _measurePage || _measurePage == null) menu.Items.Add(Item("Measure", MeasureCommand, null, "measure"));
            menu.Items.Add(Item("Zoom to Fit", () => Viewport.ZoomToFit(), "F", "fit"));
            menu.Items.Add(Sub("View Orientation",
                Item("Front", () => Viewport.StandardView("Front")),
                Item("Top", () => Viewport.StandardView("Top")),
                Item("Right", () => Viewport.StandardView("Right")),
                Item("Isometric", () => Viewport.StandardView("Isometric"))));
        }
        menu.Open(Viewport);
    }

    void OnTreeContext(string id, Point pos, Control row)
    {
        var menu = new ContextMenu();
        var f = Session.Doc.Find(id);
        // the part's material: edit, remove, a recent one, and the library's change when there is one
        if (id is FeatureTree.MaterialRow or "__part")
        {
            menu.Items.Add(Item("Edit Material...", EditMaterialCommand, null, "material"));
            menu.Items.Add(Item("Appearance...", () => _ = PartAppearanceCommand(), null, "appearance"));
            foreach (var item in _recentMaterials.Where(i => i != Session.Doc.MaterialItem && LibraryLink.Current.Snapshot(i) != null).Take(5))
            {
                var it = item;
                menu.Items.Add(Item(LibrarySnapshot.Split(it).row, () => SetPartMaterial(it), null, "material"));
            }
            if (Session.Doc.MaterialItem is { } mi)
            {
                if (LibraryLink.Updates(Session.Doc).Any(u => u.Item == mi)) menu.Items.Add(Item("Update from Library...", () => _ = LibraryUpdatesCommand(), null, "table"));
                menu.Items.Add(Item("Remove Material", () => SetPartMaterial(null), null, "delete"));
            }
            if (id == "__part" && Session.Doc.RollbackIndex != null) menu.Items.Add(new Separator());
        }
        if (id is "Front" or "Top" or "Right")
        {
            menu.Items.Add(Item("Sketch", () => StartSketchCommand(), null, "sketch"));
            menu.Items.Add(Item("Normal To", NormalTo, null, "normalto"));
        }
        if (f != null)
        {
            menu.Items.Add(Item(f is SketchFeature ? "Edit Sketch" : "Edit Feature", () => EditFeature(id), null, FeatureTree.IconFor(f)));
            var sk = f.Consumes.FirstOrDefault(c => Session.Doc.Find(c) is SketchFeature);
            if (sk != null && f is not HoleFeature) menu.Items.Add(Item("Edit Sketch", () => EditFeature(sk), null, "sketch"));
            if (f is HoleFeature hf) menu.Items.Add(Item("Edit Hole Positions", () => EditFeature(hf.SketchId), null, "sketch"));
            // a sketch, plane or axis shown or hidden in the graphics area (e.g. one a feature uses, to pick it again)
            if (f is SketchFeature or PlaneFeature or AxisFeature)
            {
                var shown = Session.Doc.SketchShown(id, UsedByActive().Contains(id));
                menu.Items.Add(Item(shown ? "Hide" : "Show", () => SetSketchShown(id, !shown), null, shown ? "hide" : "show"));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(f.Suppressed ? "Unsuppress" : "Suppress", () => SetSuppressed(!f.Suppressed), null, f.Suppressed ? "part" : "suppress"));
            menu.Items.Add(Item(string.IsNullOrWhiteSpace(f.SuppressWhen) ? "Suppress When…" : "Edit Suppress Condition…", () => _ = Dialogs.SuppressWhen(this, f), null, "equations"));
            menu.Items.Add(Item("Rename", () => _ = RenameFeature(f), "F2", "rename"));
            menu.Items.Add(Item("Roll Back", () => RollbackTo(id, false), null, "rollback"));
            menu.Items.Add(Item("Delete", DeleteSelected, null, "delete"));
        }
        if (Session.Doc.RollbackIndex != null) menu.Items.Add(Item("Roll to End", RollForward, null, "rollforward"));
        if (menu.Items.Count > 0) menu.Open(row);
    }

    // ------------------------------------------------------------------------------------------
    // file commands

    static readonly FilePickerFileType FcadType = new("SpryCAD Part") { Patterns = new[] { "*.sprt" } };
    static readonly FilePickerFileType StepType = new("STEP model") { Patterns = new[] { "*.step", "*.stp" } };
    static readonly FilePickerFileType OpenableType = new("Parts, assemblies and STEP models") { Patterns = new[] { "*.sprt", "*.sasm", "*.step", "*.stp" } };

    static bool IsStep(string path) => Path.GetExtension(path).ToLowerInvariant() is ".step" or ".stp";

    /// <summary>Imports a STEP file into the current part (as SolidWorks' Insert > Imported body).</summary>
    async Task ImportDialog()
    {
        if (PartOnly("Import STEP", "to bring a STEP model into the assembly, open it as a part, save it, and insert it as a component")) return;
        if (!await ResolvePendingEdit()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import STEP",
            FileTypeFilter = new[] { StepType },
            AllowMultiple = false,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) ImportFile(path);
    }

    /// <summary>
    /// Embeds the file in the part and adds an Imported feature that reads it (the part then saves as a container,
    /// so it keeps working when the original file moves).
    /// </summary>
    void ImportFile(string path)
    {
        if (ImportInto(_tab, path) is { } err) { _ = Dialogs.Message(this, "Import Failed", err); return; }
        Status("Imported " + Path.GetFileName(path) + ".");
    }

    /// <summary>Imports a STEP file into a tab's part (shown or not); the problem when it cannot, else null.</summary>
    string? ImportInto(PartTab t, string path)
    {
        var s = t.Session;
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { App.Log(ex, "Import " + path); return ex.Message; }
        if (!System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(64, data.Length)).TrimStart().StartsWith("ISO-10303-21"))
            return Path.GetFileName(path) + " is not a STEP file.";
        var hash = DocumentContainer.Hash(data);
        var baseName = Path.GetFileName(path);
        // same name, different content (another version of the file): keep both
        var name = "imports/" + baseName;
        for (int k = 2; s.Doc.Files.TryGetValue(name, out var existing) && DocumentContainer.Hash(existing) != hash; k++)
            name = $"imports/{Path.GetFileNameWithoutExtension(baseName)} ({k}){Path.GetExtension(baseName)}";
        // the file's color (most of its solids'), for a part with no appearance yet (read only when it has one)
        double[]? color = null;
        if (s.Doc.Appearance == null && System.Text.Encoding.Latin1.GetString(data) is var text
            && (text.Contains("COLOUR_RGB", StringComparison.Ordinal) || text.Contains("PRE_DEFINED_COLOUR", StringComparison.Ordinal)))
        {
            try { color = StepColors.Dominant(StepFile.Parse(data)); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException) { color = null; }
        }
        s.Change(() =>
        {
            s.Doc.AddFile(name, data);
            s.Doc.AddFeature(new ImportFeature { Name = Path.GetFileNameWithoutExtension(baseName), File = name, Sha256 = hash });
            if (color != null) s.Doc.Appearance = Appearance.FromRgba(color);
        });
        t.FitAfterLoad = s.RequestedRevision; // the rebuild with the import in it
        return null;
    }

    /// <summary>A sketch or feature edit that is not in the document yet.</summary>
    bool HasPendingEdit => (_sketch != null && (_sketch.Modified || _sketchIsNew)) || _edit != null;

    /// <summary>
    /// Before saving/closing/opening: keep (Yes) or discard (No) the sketch or feature being edited, or abort
    /// (Cancel). Returns false to abort.
    /// </summary>
    async Task<bool> ResolvePendingEdit()
    {
        if (!HasPendingEdit) return true;
        var what = _sketch != null ? _sketch.Feature.Name : _edit!.Feature.Name;
        var r = await Dialogs.YesNoCancel(this, "Keep Changes", $"{what} is still being edited. Keep the changes?", "Keep", "Discard");
        if (r == null) return false;
        if (_sketch != null) ExitSketch(r == true);
        else if (r == true) CommitEdit(); else CancelEdit();
        return true;
    }

    /// <summary>Discard the sketch being edited, asking first when it has changes.</summary>
    async void DiscardSketch()
    {
        if (_sketch == null) return;
        if (_sketch.Modified && !await Dialogs.YesNo(this, "Discard Changes", $"Discard the changes to {_sketch.Feature.Name}?", "Discard", "Keep Editing")) return;
        ExitSketch(false);
    }

    async Task<bool> ConfirmDiscard()
    {
        if (!await ResolvePendingEdit()) return false;
        if (Asm != null) return await ConfirmDiscardAssembly();
        if (!Session.Dirty) return true;
        var r = await Dialogs.YesNoCancel(this, "Save Changes", $"Save changes to {Session.Title.TrimEnd(' ', '*')}?");
        if (r == null) return false;
        if (r == true) return await Save();
        return true;
    }

    /// <summary>A new part, in a tab of its own.</summary>
    async Task NewFile()
    {
        if (await NewTab() is not { } t) return;
        t.Session.New(); // the view is fitted when its first rebuild arrives
    }

    async Task OpenFileDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Part",
            FileTypeFilter = new[] { OpenableType, FcadType, AssemblyType, StepType },
            AllowMultiple = true,
        });
        await OpenFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    public void OpenFile(string path) => _ = OpenFileAsync(path);

    /// <summary>
    /// Opens files. Several get their tabs (and rows in the list of open parts) at once, without reading them; the
    /// first is shown, each other part is read when it is first shown, and the list's previews are made in the
    /// background, one part at a time. One file opens as <see cref="OpenFileAsync"/>.
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var a in paths.Where(OpensAsAssembly).ToList()) await OpenAnyAssembly(a);
        paths = paths.Where(p => !OpensAsAssembly(p)).ToList();
        if (paths.Count == 0) return;
        if (paths.Count == 1) { await OpenFileAsync(paths[0]); return; }
        var blank = IsUntouched(_tab) ? _tab : null; // the empty part the window started with: not needed
        PartTab? first = null;
        foreach (var path in paths)
        {
            if (FindOpen(path) is { } open) { if (open.Error != null) RetryLoad(open); first ??= open; continue; }
            var t = AddTab(new DocumentSession());
            t.Source = path;
            t.Session.Defer(path);
            first ??= t;
        }
        if (first == null) return;
        if (await SwitchTo(first) && blank != null && blank != first && _tab != blank) RemoveTab(blank);
        Status($"Opened {paths.Count} files. Each part loads when its tab is shown.");
        StartPreviews();
    }

    /// <summary>The tab of a file already open (a part's own file, or the STEP model it was made from).</summary>
    PartTab? FindOpen(string path) =>
        _parts.FirstOrDefault(t => (t.Session.SourcePath ?? t.Source) is { } p && SamePath(p, path));

    /// <summary>
    /// Opens a part (or a STEP model, as a new part holding it) in a tab of its own: the tab already showing it
    /// if it is open, else the active tab if that is an untouched new part, else a new tab. False if cancelled.
    /// </summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        if (OpensAsAssembly(path)) { await OpenAnyAssembly(path); return true; }
        if (FindOpen(path) is { } open)
        {
            // opened again after it could not be: read it again (the file may have been fixed or replaced)
            if (open.Error != null) RetryLoad(open);
            return await SwitchTo(open);
        }
        if (IsUntouched(_tab)) { if (!await ResolvePendingEdit()) return false; LeaveModes(); }
        else if (await NewTab() is null) return false;
        if (IsStep(path))
        {
            // the STEP model, imported into a new part named after it (a later Save As suggests that name)
            Session.UntitledName = Path.GetFileNameWithoutExtension(path);
            _tab.Source = path;
            BeginLoading(_tab, "Importing " + Path.GetFileName(path));
            Session.New();
            // a file that cannot be read stays open, marked, saying why (as in a batch)
            if (ImportInto(_tab, path) is { } err) { _tab.LoadError = err; EndLoading(_tab); UpdateTabHeader(_tab); }
            else Status("Imported " + Path.GetFileName(path) + ".");
            Session.MarkClean();
            UpdateLoadBanner();
            return true;
        }
        var tab = _tab;
        try
        {
            Viewport.ClearPivot();
            // read off the UI thread (a big part takes a moment), its progress shown over the view meanwhile
            BeginLoading(tab, "Opening " + Path.GetFileName(path), reading: true);
            var opened = await Task.Run(() => DocumentSession.Read(path));
            if (!_parts.Contains(tab)) { opened.Copy.Close(); return false; } // (its tab closed meanwhile)
            tab.Session.Load(opened);
            BeginLoading(tab, "Opening " + Path.GetFileName(path)); // (read: now rebuilding)
            if (!IsAutosave(path) && !IsEmbeddedCopy(path)) RecentFiles.Add(path);
            Status("Opened " + path);
            NoteLibraryUpdates();
            TakeProfileUpdates();
            tab.FitAfterLoad = tab.Session.RequestedRevision;
            UpdateSupersededBanner();
            // someone else has it open: read-only (saving makes a copy elsewhere), unless the user says otherwise
            if (tab.Session.ForeignLock is { } l)
            {
                tab.Session.ReadOnly = await Dialogs.YesNo(this, "File in Use",
                    $"{Path.GetFileName(path)} has been open by {l.User} on {l.Machine} since {l.SinceUtc.ToLocalTime():g}.\n\n" +
                    "Open it read-only, or open it anyway? If opened anyway, the last save overwrites the other user's changes.",
                    "Read-Only", "Open Anyway");
                if (tab.Session.ReadOnly) Status($"Opened {Path.GetFileName(path)} read-only. {l.User} has it open.");
            }
            return true;
        }
        catch (Exception ex)
        {
            // the part stays open, marked, saying why (Retry reads it again)
            App.Log(ex, "Open " + path);
            tab.Source = path;
            tab.Session.UntitledName = Path.GetFileNameWithoutExtension(path);
            tab.LoadError = ex.Message;
            EndLoading(tab);
            UpdateTabHeader(tab);
            UpdateLoadBanner();
            return true;
        }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static string AutosaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpryCAD");

    static bool IsAutosave(string path) =>
        SamePath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", AutosaveDir) && Path.GetFileName(path).StartsWith("autosave", StringComparison.OrdinalIgnoreCase);

    async Task<bool> Save()
    {
        if (!await ResolvePendingEdit()) return false;
        if (Asm != null) return await SaveAssembly(false);
        // an embedded part being edited: saved back into its assembly (and the assembly saved)
        if (_tab.EmbeddedName != null)
        {
            try { if (await SaveEmbedded(_tab) is { } done) return done; }
            catch (Exception ex) { App.Log(ex, "Save embedded"); await Dialogs.Message(this, "Save Failed", "Could not save: " + ex.Message); return false; }
        }
        if (Session.FilePath == null || Session.ReadOnly) return await SaveAs();
        try { Session.Save(Session.FilePath); RecentFiles.Add(Session.FilePath); Status("Saved " + Session.FilePath + "."); return true; }
        catch (SaveConflictException ex)
        {
            // someone else saved it since it was opened: keep both (a copy), or overwrite theirs
            var r = await Dialogs.YesNoCancel(this, "Save Conflict", ex.Message + "\n\nSave your version as a copy, or overwrite theirs?", "Save as Copy", "Overwrite");
            if (r == null) return false;
            if (r == true) return await SaveAs();
            try { Session.Save(Session.FilePath, overwrite: true); RecentFiles.Add(Session.FilePath); Status("Saved " + Session.FilePath + "."); return true; }
            catch (Exception ex2) { App.Log(ex2, "Save failed"); await Dialogs.Message(this, "Save Failed", $"Could not save {Path.GetFileName(Session.FilePath)}: {ex2.Message}"); return false; }
        }
        catch (Exception ex) { App.Log(ex, "Save failed"); await Dialogs.Message(this, "Save Failed", $"Could not save {Path.GetFileName(Session.FilePath)}: {ex.Message}"); return false; }
    }

    async Task<bool> SaveAs()
    {
        if (!await ResolvePendingEdit()) return false;
        if (Asm != null) return await SaveAssembly(true);
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Part As",
            DefaultExtension = "sprt",
            SuggestedFileName = Session.FilePath != null ? Path.GetFileName(Session.FilePath) : Session.UntitledName + CadDocument.FileExtension,
            FileTypeChoices = new[] { FcadType },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return false;
        try
        {
            Session.Save(path); RecentFiles.Add(path); Status("Saved " + path + ".");
            // (an embedded part saved as a file of its own: no longer tied to the assembly)
            _tab.EmbeddedIn = null; _tab.EmbeddedName = null; _tab.EmbeddedPath = null; UpdateTabHeader(_tab);
            return true;
        }
        catch (Exception ex) { App.Log(ex, "Save failed"); await Dialogs.Message(this, "Save Failed", $"Could not save {Path.GetFileName(path)}: {ex.Message}"); return false; }
    }

    async Task Export(string kind)
    {
        if (Asm != null) { await ExportAssembly(kind); return; }
        var r = Session.Result;
        if (r == null || r.State.Bodies.Count == 0) { await Dialogs.Message(this, "Export", "There is no solid body to export."); return; }
        var type = kind == "step"
            ? new FilePickerFileType("STEP AP214") { Patterns = new[] { "*.step", "*.stp" } }
            : new FilePickerFileType("STL (binary)") { Patterns = new[] { "*.stl" } };
        var baseName = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : Session.UntitledName;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = kind == "step" ? "Export STEP" : "Export STL",
            DefaultExtension = kind == "step" ? "step" : "stl",
            SuggestedFileName = baseName + (kind == "step" ? ".step" : ".stl"),
            FileTypeChoices = new[] { type },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            // (written in millimetres, as the model is)
            var bodies = r.State.Bodies.ToList();
            Status($"Exporting {Path.GetFileName(path)}…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // cosmetic threads are modeled for export: the part rebuilt so (a copy, off the UI thread)
            var hasCosmetic = r.State.Cosmetics.Count > 0;
            var snapshot = hasCosmetic ? Session.Doc.Clone() : null;
            var session = Session;
            var look = session.Doc.EffectiveAppearance.ToRgba(); // (its color in the file, as other CAD shows it)
            // written off the UI thread: the exact B-rep / tessellation of big parts takes a moment
            await Task.Run(() =>
            {
                if (snapshot != null) bodies = session.RebuildForExport(snapshot).State.Bodies.ToList();
                using var fs = File.Create(path);
                if (kind == "step") StepWriter.Write(fs, bodies.Select(b => (b.Id, b.Solid)), baseName, color: look);
                else StlWriter.Write(fs, bodies.Select(b => b.Solid));
            });
            Status($"Exported {path} in {sw.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex) { App.Log(ex, "Export failed"); await Dialogs.Message(this, "Export Failed", $"Could not export {Path.GetFileName(path)}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------------------------------
    // edit commands

    void Undo()
    {
        if (_sketch != null) { if (_sketch.Undo()) Status("Undo"); return; }
        if (_page != null) return;
        if (Asm is { } a) { a.Undo(); return; }
        Session.Undo();
    }

    void Redo()
    {
        if (_sketch != null) { _sketch.Redo(); return; }
        if (_page != null) return;
        if (Asm is { } a) { a.Redo(); return; }
        Session.Redo();
    }

    void DeleteSelected()
    {
        if (Asm != null) { DeleteComponent(); return; }
        _ = DeleteSelectedAsync();
    }

    async Task DeleteSelectedAsync()
    {
        var id = Tree.SelectedId;
        var f = id != null ? Session.Doc.Find(id) : null;
        if (f == null && Selection.First is { Kind: SelKind.Sketch or SelKind.Feature } s) f = Session.Doc.Find(s.FeatureId!);
        if (f == null && Selection.First is { Kind: SelKind.Plane, FeatureId: { } pid }) f = Session.Doc.Find(pid);
        if (f == null) return;
        var victims = new List<Feature> { f };
        // also delete the absorbed sketch of the feature (like SolidWorks "Delete absorbed features")
        foreach (var c in f.Consumes)
            if (Session.Doc.Find(c) is SketchFeature sk && Session.Doc.ConsumersOf(sk.Id).All(x => x.Id == f.Id)) victims.Add(sk);
        // confirm, listing what goes and what depends on it (as SolidWorks' Confirm Delete)
        var deps = Session.Doc.DependentsOf(victims.Select(v => v.Id));
        var text = $"Delete {string.Join(", ", victims.Select(v => v.Name))}?";
        if (deps.Count > 0)
        {
            text += $"\n\nThe following dependent features will fail:\n  {string.Join(", ", deps.Select(d => d.Name))}";
            var r = await Dialogs.YesNoCancel(this, "Confirm Delete", text, "Delete", "Delete with Dependents");
            if (r == null) return;
            if (r == false) victims.AddRange(deps);
        }
        else if (!await Dialogs.YesNo(this, "Confirm Delete", text, "Delete", "Cancel")) return;
        Session.Change(() =>
        {
            // the rollback bar stays under the same feature: one place up for each feature deleted above it (it is an index)
            var d = Session.Doc;
            if (d.RollbackIndex is int rb) d.RollbackIndex = rb - victims.Count(v => d.IndexOf(v.Id) is var i && i >= 0 && i < rb);
            foreach (var v in victims) d.Features.Remove(v);
            FixRollback();
        });
        Selection.Clear();
        Status($"Deleted {string.Join(", ", victims.Select(v => v.Name))}. (Ctrl+Z to undo)");
    }

    void FixRollback()
    {
        var d = Session.Doc;
        if (d.RollbackIndex is int r && r >= d.Features.Count) d.RollbackIndex = null;
    }

    void SetSuppressed(bool on)
    {
        // (in an assembly: the selected component)
        if (Asm is { } sa) { if (sa.Selected is { } sp) SuppressComponent(sp, on); else Status("Select a component first."); return; }
        var id = Tree.SelectedId ?? Selection.First?.FeatureId;
        var f = id != null ? Session.Doc.Find(id) : null;
        if (f == null) return;
        Session.Change(() => f.Suppressed = on);
    }

    void RollbackTo(string id, bool after)
    {
        var idx = Session.Doc.IndexOf(id);
        if (idx < 0) return;
        var target = after ? idx + 1 : idx;
        Session.Change(() => Session.Doc.RollbackIndex = target >= Session.Doc.Features.Count ? null : target);
    }

    void RollForward() { if (!PartOnly("Roll forward")) Session.Change(() => Session.Doc.RollbackIndex = null); }

    /// <summary>
    /// Tree drag reorder: puts the feature (with the sketches only it absorbs) before/after the target feature or
    /// the rollback bar ("__rollback"). Refused when a feature would end up before something it refers to.
    /// </summary>
    void MoveFeature(string id, string target, bool after)
    {
        if (_sketch != null || _edit != null) { Status("Finish the current edit before reordering features."); return; }
        var d = Session.Doc;
        if (d.Find(id) is not { } f) return;
        var group = d.Features.Where(x => x == f || x is SketchFeature && f.Consumes.Contains(x.Id) && d.ConsumersOf(x.Id).All(c => c == f)).ToList();

        // the rollback bar rides along in the list as a marker, so moves across it keep what is rolled back right
        var bar = new object();
        var order = d.Features.Cast<object>().ToList();
        order.Insert(d.ActiveCount, bar);
        object anchor;
        if (target == "__rollback") anchor = bar;
        else if (d.Find(target) is { } t && !group.Contains(t)) anchor = t;
        else return;
        order.RemoveAll(o => o is Feature x && group.Contains(x));
        order.InsertRange(order.IndexOf(anchor) + (after ? 1 : 0), group);
        var moved = order.OfType<Feature>().ToList();
        var active = order.IndexOf(bar);
        if (moved.SequenceEqual(d.Features) && active == d.ActiveCount) return;

        var json = moved.ToDictionary(x => x.Id, CadDocument.JsonOf);
        for (int i = 0; i < moved.Count; i++)
            for (int j = i + 1; j < moved.Count; j++)
                if (CadDocument.Refers(json[moved[i].Id], moved[j].Id))
                {
                    Status($"Cannot move {f.Name} there. {moved[i].Name} references {moved[j].Name}, which would come after it.");
                    return;
                }
        Session.Change(() =>
        {
            d.Features.Clear();
            d.Features.AddRange(moved);
            d.RollbackIndex = active >= moved.Count ? null : active;
        });
        Status($"Moved {f.Name}. (Ctrl+Z to undo)");
    }

    async Task RenameFeature(Feature f)
    {
        var name = await Dialogs.Input(this, "Rename", "Feature name:", f.Name);
        if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
        Session.Change(() => f.Name = name.Trim());
    }

    void LeaveModes()
    {
        if (_sketch != null) ExitSketch(false);
        if (_page != null && _edit != null) CancelEdit();
        ShowPage(null);
    }

    void OnModelDoubleClick(SelItem? item)
    {
        var fid = item?.OwningFeatureId;
        if (fid != null && Session.Doc.Find(fid) != null) EditFeature(fid);
    }
}
