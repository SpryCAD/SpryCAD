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

    static MenuItem Item(string header, Action click, string? gesture = null, string? icon = null)
    {
        var m = new MenuItem { Header = header };
        if (gesture != null) m.InputGesture = Avalonia.Input.KeyGesture.Parse(gesture);
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
            Item("_Open...", () => _ = OpenFileDialog(), "Ctrl+O", "open"),
            Item("_Save", () => _ = Save(), "Ctrl+S", "save"),
            Item("Save _As...", () => _ = SaveAs(), "Ctrl+Shift+S"),
            Item("_Close", () => _ = CloseTab(_tab), "Ctrl+W"),
            RecentMenu(),
            "-",
            Item("_Import STEP...", () => _ = ImportDialog(), "Ctrl+I", "import"),
            Item("Export _STEP...", () => _ = Export("step"), null, "export-step"),
            Item("Export S_TL...", () => _ = Export("stl"), null, "export-stl"),
            Item("Export Flat Pattern _DXF...", () => _ = ExportDxf(), null, "export-dxf"),
            "-",
            Item("Recover Autosave", () => RecoverAutosave()),
            "-",
            Item("E_xit", Close)));
        MainMenu.Items.Add(Sub("_Edit",
            Item("_Undo", Undo, "Ctrl+Z", "undo"),
            Item("_Redo", Redo, "Ctrl+Y", "redo"),
            "-",
            Item("_Delete", DeleteSelected, "Delete", "delete"),
            Item("Re_build", () => Session.RequestRebuild(), "Ctrl+B", "rebuild"),
            Item("Force Rebuild", () => Session.ForceRebuild(), "Ctrl+Q", "rebuild"),
            "-",
            Item("Suppress", () => SetSuppressed(true), null, "suppress"),
            Item("Unsuppress", () => SetSuppressed(false), null, "part"),
            Item("Roll Forward to End", RollForward, null, "rollforward")));
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
            Toggle("Color by _Feature", () => _colorByFeature, ToggleColorByFeature),
            Toggle("Perspective", () => Viewport.Camera.Perspective, () => { Viewport.Camera.Perspective = !Viewport.Camera.Perspective; Viewport.Redraw(); }),
            Sub("_Threads",
                Toggle("As Set in Each Thread", () => ThreadMode == ThreadDisplay.AsSet, () => _ = SetThreadMode(ThreadDisplay.AsSet), radio: true),
                Toggle("All Modeled", () => ThreadMode == ThreadDisplay.Modeled, () => _ = SetThreadMode(ThreadDisplay.Modeled), radio: true),
                Toggle("All Cosmetic (lightweight)", () => ThreadMode == ThreadDisplay.Cosmetic, () => _ = SetThreadMode(ThreadDisplay.Cosmetic), radio: true)),
            "-",
            Toggle("Planes", () => _showPlanes, TogglePlanes),
            Toggle("Origins", () => Viewport.ShowOrigin, () => { Viewport.ShowOrigin = !Viewport.ShowOrigin; Viewport.Redraw(); }),
            Toggle("Sketch Relations", () => _sketch?.ShowRelations ?? true, () => { if (_sketch != null) { _sketch.ShowRelations = !_sketch.ShowRelations; _sketch.UpdateOverlay(Viewport); } })));
        MainMenu.Items.Add(Sub("_Insert",
            Item("_Sketch", () => StartSketchCommand(), null, "sketch"),
            "-",
            Sub("_Boss/Base",
                Item("_Extrude...", () => ExtrudeCommand(false), null, "extrude"),
                Item("_Revolve...", () => RevolveCommand(false), null, "revolve"),
                Item("S_wept...", () => SweepCommand(false), null, "sweep"),
                Item("_Lofted...", () => LoftCommand(false), null, "loft"),
                Item("_Helical...", () => HelicalCommand(false), null, "helical")),
            Sub("_Cut",
                Item("_Extrude...", () => ExtrudeCommand(true), null, "cut"),
                Item("_Revolve...", () => RevolveCommand(true), null, "cutrevolve"),
                Item("S_wept...", () => SweepCommand(true), null, "cutsweep"),
                Item("_Lofted...", () => LoftCommand(true), null, "cutloft"),
                Item("_Helical...", () => HelicalCommand(true), null, "cuthelical")),
            Sub("_Features",
                Item("_Fillet...", () => BlendCommand(true), null, "fillet"),
                Item("_Chamfer...", () => BlendCommand(false), null, "chamfer"),
                Item("_Hole Wizard...", HoleCommand, null, "hole"),
                Item("_Thread...", ThreadCommand, null, "thread"),
                Item("_Shell...", ShellCommand, null, "shell"),
                Item("_Draft...", DraftCommand, null, "draft"),
                Item("C_ombine...", CombineCommand, null, "combine")),
            Sub("_Pattern/Mirror",
                Item("_Linear Pattern...", LinearPatternCommand, null, "lpattern"),
                Item("_Circular Pattern...", CircularPatternCommand, null, "cpattern"),
                Item("_Mirror...", MirrorCommand, null, "mirror")),
            Sub("_Reference Geometry",
                Item("_Plane...", PlaneCommand, null, "plane")),
            Sub("Sheet _Metal",
                Item("_Flat Pattern...", FlatPatternCommand, null, "flatpattern"),
                Item("Fla_tten", ToggleFlatten, null, "flatten"))));
        MainMenu.Items.Add(Sub("_Tools",
            Item("_Equations...", () => _ = Dialogs.Equations(this), null, "equations"),
            Item("_Design Table...", () => _ = Dialogs.DesignTables(this), null, "table"),
            Item("_Configure...", () => _ = Dialogs.ConfigurePart(this), null, "configure"),
            Item("Mea_sure...", MeasureCommand, null, "measure"),
            Item("_Mass Properties...", MassPropertiesCommand, null, "mass"),
            "-",
            Item("_Options...", () => _ = Dialogs.Options(this), null, "options")));
        MainMenu.Items.Add(Sub("_Help",
            Item("_Keyboard Shortcuts", () => _ = Dialogs.Shortcuts(this), null, "help"),
            Item("Check for _Updates...", () => _ = Updater.CheckNow(this)),
            Item("_Change Log...", () => _ = Updater.ShowChangelog(this)),
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
        B("rebuild", "Rebuild (Ctrl+B)", () => Session.RequestRebuild());
        B("options", "Options", () => _ = Dialogs.Options(this));
    }

    // ------------------------------------------------------------------------------------------
    // CommandManager

    void BuildCommandManager()
    {
        foreach (var name in new[] { "Assembly", "Features", "Sketch", "Sheet Metal", "Evaluate" })
        {
            // the Assembly tab only while an assembly is active (ApplyDocumentKind)
            var t = new ToggleButton { Content = name, Classes = { "tab" }, IsVisible = name != "Assembly" };
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
        switch (name)
        {
            case "Assembly":
                AssemblyCommands();
                break;
            case "Features":
                Cmd("extrude", "Extruded\nBoss/Base", () => ExtrudeCommand(false));
                Cmd("revolve", "Revolved\nBoss/Base", () => RevolveCommand(false));
                Cmd("sweep", "Swept\nBoss/Base", () => SweepCommand(false));
                Cmd("loft", "Lofted\nBoss/Base", () => LoftCommand(false));
                Cmd("helical", "Helical\nBoss/Base", () => HelicalCommand(false));
                Sep();
                Cmd("cut", "Extruded\nCut", () => ExtrudeCommand(true));
                Cmd("cutrevolve", "Revolved\nCut", () => RevolveCommand(true));
                Cmd("cutsweep", "Swept\nCut", () => SweepCommand(true));
                Cmd("cutloft", "Lofted\nCut", () => LoftCommand(true));
                Cmd("cuthelical", "Helical\nCut", () => HelicalCommand(true));
                Cmd("hole", "Hole\nWizard", HoleCommand);
                Cmd("thread", "Thread", ThreadCommand);
                Sep();
                Cmd("fillet", "Fillet", () => BlendCommand(true));
                Cmd("chamfer", "Chamfer", () => BlendCommand(false));
                Cmd("shell", "Shell", ShellCommand);
                Cmd("draft", "Draft", DraftCommand);
                Sep();
                Cmd("lpattern", "Linear\nPattern", LinearPatternCommand);
                Cmd("cpattern", "Circular\nPattern", CircularPatternCommand);
                Cmd("mirror", "Mirror", MirrorCommand);
                Sep();
                Cmd("plane", "Reference\nPlane", PlaneCommand);
                Cmd("combine", "Combine", CombineCommand);
                break;
            case "Sketch":
                if (_sketch == null) Cmd("sketch", "Sketch", () => StartSketchCommand());
                else Cmd("exitsketch", "Exit\nSketch", () => ExitSketch(true));
                Cmd("dimension", "Smart\nDimension", () => SetSketchTool(SketchTool.SmartDimension), SketchTool.SmartDimension);
                Sep();
                Cmd("line", "Line", () => SetSketchTool(SketchTool.Line), SketchTool.Line);
                Cmd("centerline", "Centerline", () => SetSketchTool(SketchTool.Centerline), SketchTool.Centerline);
                Cmd("rect", "Corner\nRectangle", () => SetSketchTool(SketchTool.Rectangle), SketchTool.Rectangle);
                Cmd("centerrect", "Center\nRectangle", () => SetSketchTool(SketchTool.CenterRectangle), SketchTool.CenterRectangle);
                Cmd("circle", "Circle", () => SetSketchTool(SketchTool.Circle), SketchTool.Circle);
                Cmd("centerarc", "Centerpoint\nArc", () => SetSketchTool(SketchTool.CenterArc), SketchTool.CenterArc);
                Cmd("arc3", "3 Point\nArc", () => SetSketchTool(SketchTool.ThreePointArc), SketchTool.ThreePointArc);
                Cmd("tangentarc", "Tangent\nArc", () => SetSketchTool(SketchTool.TangentArc), SketchTool.TangentArc);
                Cmd("polygon", "Polygon", () => SetSketchTool(SketchTool.Polygon), SketchTool.Polygon);
                Cmd("slot", "Straight\nSlot", () => SetSketchTool(SketchTool.Slot), SketchTool.Slot);
                Cmd("point", "Point", () => SetSketchTool(SketchTool.Point), SketchTool.Point);
                Sep();
                Cmd("trim", "Trim\nEntities", () => SetSketchTool(SketchTool.Trim), SketchTool.Trim);
                Cmd("sketchfillet", "Sketch\nFillet", () => { if (_sketch == null) { SetSketchTool(SketchTool.Fillet); return; } _sketch.StartFillet(); UpdateToolButtons(); BuildSketchPage(); }, SketchTool.Fillet);
                Cmd("mirror", "Mirror\nEntities", MirrorEntities);
                Cmd("offset", "Offset\nEntities", OffsetEntities);
                Cmd("convert", "Convert\nEntities", ConvertEntities);
                Cmd("construction", "Construction\nGeometry", ToggleConstruction);
                Cmd("relations", "Add\nRelations", AddRelationCommand);
                Cmd("normalto", "Normal\nTo", NormalTo);
                break;
            case "Sheet Metal":
                Cmd("flatpattern", "Flat\nPattern", FlatPatternCommand);
                Cmd("flatten", "Flatten", ToggleFlatten);
                Sep();
                Cmd("export-dxf", "Export\nDXF", () => _ = ExportDxf());
                break;
            case "Evaluate":
                Cmd("measure", "Measure", MeasureCommand);
                Cmd("mass", "Mass\nProperties", MassPropertiesCommand);
                Cmd("equations", "Equations", () => _ = Dialogs.Equations(this));
                Cmd("table", "Design\nTable", () => _ = Dialogs.DesignTables(this));
                Cmd("configure", "Configure", () => _ = Dialogs.ConfigurePart(this));
                Sep();
                Cmd("export-step", "Export\nSTEP", () => _ = Export("step"));
                Cmd("export-stl", "Export\nSTL", () => _ = Export("stl"));
                break;
        }
        UpdateToolButtons();
    }

    void Cmd(string icon, string label, Action click, SketchTool? tool = null)
    {
        var sp = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(Icons.Make(icon, 26));
        sp.Children.Add(new TextBlock { Text = label });
        var b = new Button { Classes = { "cmd" }, Content = sp };
        var name = label.Replace('\n', ' ');
        var key = tool is { } st && SketchToolKeys.TryGetValue(st, out var k) ? $" ({k})" : "";
        ToolTip.SetTip(b, CommandHelp.TryGetValue(name, out var help) ? $"{name}{key}\n{help}" : name + key);
        b.Click += (_, _) => click();
        if (tool != null) _toolButtons[tool.Value] = b;
        CommandBar.Children.Add(b);
    }

    internal static readonly Dictionary<SketchTool, string> SketchToolKeys = new()
    {
        [SketchTool.Line] = "L", [SketchTool.Rectangle] = "R", [SketchTool.Circle] = "C", [SketchTool.CenterArc] = "A",
        [SketchTool.SmartDimension] = "D", [SketchTool.Point] = "P", [SketchTool.Trim] = "T",
    };

    /// <summary>One-line descriptions shown under the command name in toolbar tooltips.</summary>
    static readonly Dictionary<string, string> CommandHelp = new()
    {
        ["Extruded Boss/Base"] = "Extends a sketch profile to add material.",
        ["Extruded Cut"] = "Extends a sketch profile to remove material.",
        ["Revolved Boss/Base"] = "Revolves a sketch profile about a centerline to add material.",
        ["Revolved Cut"] = "Revolves a sketch profile about a centerline to remove material.",
        ["Swept Boss/Base"] = "Sweeps a closed profile along a path of lines and arcs to add material.",
        ["Swept Cut"] = "Sweeps a closed profile along a path of lines and arcs to remove material.",
        ["Lofted Boss/Base"] = "Adds material between profiles in order (and through guide curves): a transition from one shape to the next.",
        ["Lofted Cut"] = "Removes material between profiles in order (and through guide curves).",
        ["Hole Wizard"] = "Simple, counterbored or countersunk holes placed on a face.",
        ["Helical Boss/Base"] = "Sweeps a closed profile along a helix about a centerline of its sketch (springs, coils).",
        ["Helical Cut"] = "Cuts a closed profile swept along a helix about a centerline of its sketch.",
        ["Thread"] = "Models a metric thread on a shaft or in a hole, with a fit clearance for 3D printing.",
        ["Fillet"] = "Rounds the selected edges (tangent edges are included).",
        ["Chamfer"] = "Bevels the selected edges.",
        ["Shell"] = "Hollows the part, leaving the selected faces open.",
        ["Draft"] = "Tapers faces by an angle about a neutral plane, so the part comes out of a mould (planar faces, and round faces along the pull direction).",
        ["Linear Pattern"] = "Copies features along one or two directions.",
        ["Circular Pattern"] = "Copies features around an axis.",
        ["Mirror"] = "Mirrors features about a plane or planar face.",
        ["Reference Plane"] = "Creates a plane offset from a plane or planar face.",
        ["Combine"] = "Adds, subtracts or intersects bodies.",
        ["Flat Pattern"] = "Unfolds the sheet metal part about a fixed face (bend allowance from the K-factor).",
        ["Flatten"] = "Shows the part flat or folded.",
        ["Export DXF"] = "Writes the flat pattern outline and bend lines as a DXF file.",
        ["Sketch"] = "Starts a sketch on the selected plane or planar face.",
        ["Smart Dimension"] = "Dimensions sketch entities and model edges/vertices.",
        ["Line"] = "Sketches lines. Snapping adds relations, also to model edges, midpoints and centers.",
        ["Convert Entities"] = "Projects the selected model edges into the sketch.",
        ["Trim Entities"] = "Trims sketch entities at their intersections.",
        ["Sketch Fillet"] = "Rounds the corner where two lines meet. The lines stay as construction geometry (the virtual sharp); deleting the arc restores the corner.",
        ["Offset Entities"] = "Offsets the selected sketch chain (lines, arcs, circles) by a distance. The copy follows the original; the distance is a dimension.",
        ["Mirror Entities"] = "Mirrors the selected entities about the selected centerline. The copies stay mirrored.",
        ["Construction Geometry"] = "Toggles the selected sketch entities to construction.",
        ["Add Relations"] = "Relates points and entities you click (coincident, horizontal, tangent…), no Ctrl needed.",
        ["Measure"] = "Distance, angle, radius, length and area of the faces, edges and vertices you click.",
        ["Mass Properties"] = "Mass, volume, surface area, center of mass and moments of inertia, with the part's material.",
        ["Equations"] = "Global variables that drive dimensions; conditions on them suppress features (right-click a feature: Suppress When).",
        ["Design Table"] = "Rows of values for sets of variables (a screw's sizes): choosing a row sets them all.",
        ["Configure"] = "Sets the part's user-facing parameters: its design tables' rows and the variables marked User.",
    };

    void Sep() => CommandBar.Children.Add(new Border { Width = 1, Background = Palette.Border, Margin = new Thickness(4, 6) });

    void UpdateToolButtons()
    {
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
        AddPanelTab(PropertyTab, "propsheet", "PropertyManager (the settings of the feature or sketch being edited)", PmHost,
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
        H("planes", "Hide/Show Planes", TogglePlanes);
        H("colorbyfeature", "Color by Feature", ToggleColorByFeature);
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
        if (f == null) { Status("Select a planar face, plane or sketch for Normal To."); return; }
        Viewport.NormalTo(f.Value);
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
            else Status($"This page has no box for {what}.");
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
                menu.Items.Add(Item("Select Center", () => PickRef(asm.Model.RefTo(mh.id, mf, asm.Model.NearerEnd(mh.id, mf, mh.point)), "That face has no circle to take the center of.")));
                menu.Items.Add(new Separator());
            }
            var hit = Viewport.Pick(p.X, p.Y, SelFilter.Any);
            if (hit != null && !Selection.Contains(hit)) Selection.Set(hit);
            if (hit?.Kind == SelKind.Face && hit.Planar || hit?.Kind == SelKind.Plane)
            {
                menu.Items.Add(Item("Sketch", () => StartSketchCommand(), null, "sketch"));
                menu.Items.Add(Item("Hole Wizard", HoleCommand, null, "hole"));
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
            // a sketch shown or hidden in the graphics area (e.g. one a feature uses, to pick it again for a sweep)
            if (f is SketchFeature)
            {
                var shown = Session.Doc.SketchShown(id, Session.Doc.ConsumedByActive().Contains(id));
                menu.Items.Add(Item(shown ? "Hide" : "Show", () => SetSketchShown(id, !shown), null, shown ? "hide" : "show"));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(f.Suppressed ? "Unsuppress" : "Suppress", () => SetSuppressed(!f.Suppressed), null, f.Suppressed ? "part" : "suppress"));
            menu.Items.Add(Item(string.IsNullOrWhiteSpace(f.SuppressWhen) ? "Suppress When..." : "Edit Suppress Condition...", () => _ = Dialogs.SuppressWhen(this, f), null, "equations"));
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
        if (ImportInto(_tab, path) is { } err) { _ = Dialogs.Message(this, "Import failed", err); return; }
        Status("Imported " + Path.GetFileName(path));
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
        s.Change(() =>
        {
            s.Doc.AddFile(name, data);
            s.Doc.AddFeature(new ImportFeature { Name = Path.GetFileNameWithoutExtension(baseName), File = name, Sha256 = hash });
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
        var r = await Dialogs.YesNoCancel(this, "SpryCAD", $"{what} is still being edited. Keep the changes?", "Keep", "Discard");
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
        var r = await Dialogs.YesNoCancel(this, "SpryCAD", $"Save changes to {Session.Title.TrimEnd(' ', '*')}?");
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
        Status($"Opened {paths.Count} files: each part is read when shown; previews are made in the background.");
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
            Session.New();
            // a file that cannot be read stays open, marked, saying why (as in a batch)
            if (ImportInto(_tab, path) is { } err) { _tab.LoadError = err; UpdateTabHeader(_tab); }
            else Status("Imported " + Path.GetFileName(path));
            Session.MarkClean();
            UpdateLoadBanner();
            return true;
        }
        try
        {
            Viewport.ClearPivot();
            Session.Load(path);
            if (!IsAutosave(path) && !IsEmbeddedCopy(path)) RecentFiles.Add(path);
            Status("Opened " + path);
            _tab.FitAfterLoad = Session.RequestedRevision;
            return true;
        }
        catch (Exception ex)
        {
            // the part stays open, marked, saying why (Retry reads it again)
            App.Log(ex, "Open " + path);
            _tab.Source = path;
            Session.UntitledName = Path.GetFileNameWithoutExtension(path);
            _tab.LoadError = ex.Message;
            UpdateTabHeader(_tab);
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
            catch (Exception ex) { App.Log(ex, "Save embedded"); await Dialogs.Message(this, "Save failed", ex.Message); return false; }
        }
        if (Session.FilePath == null) return await SaveAs();
        try { Session.Save(Session.FilePath); RecentFiles.Add(Session.FilePath); Status("Saved " + Session.FilePath); return true; }
        catch (Exception ex) { App.Log(ex, "Save failed"); await Dialogs.Message(this, "Save failed", ex.Message); return false; }
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
            Session.Save(path); RecentFiles.Add(path); Status("Saved " + path);
            // (an embedded part saved as a file of its own: no longer tied to the assembly)
            _tab.EmbeddedIn = null; _tab.EmbeddedName = null; _tab.EmbeddedPath = null; UpdateTabHeader(_tab);
            return true;
        }
        catch (Exception ex) { App.Log(ex, "Save failed"); await Dialogs.Message(this, "Save failed", ex.Message); return false; }
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
            // documents in IPS store inches; exports are written in millimetres
            var scale = Session.Doc.Units == UnitSystem.IPS ? 25.4 : 1.0;
            var bodies = r.State.Bodies.ToList();
            Status($"Exporting {Path.GetFileName(path)}...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // cosmetic threads are modeled for export: the part rebuilt so (a copy, off the UI thread)
            var hasCosmetic = r.State.Cosmetics.Count > 0;
            var snapshot = hasCosmetic ? Session.Doc.Clone() : null;
            // written off the UI thread: the exact B-rep / tessellation of big parts takes a moment
            await Task.Run(() =>
            {
                if (snapshot != null) bodies = new Regenerator { ThreadOverride = ThreadDisplay.Modeled }.Rebuild(snapshot).State.Bodies.ToList();
                using var fs = File.Create(path);
                if (kind == "step") StepWriter.Write(fs, bodies.Select(b => (b.Id, b.Solid)), baseName, scale);
                else StlWriter.Write(fs, bodies.Select(b => b.Solid), scale);
            });
            Status($"Exported {path} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) { App.Log(ex, "Export failed"); await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    /// <summary>Writes the flat pattern (of each body a Flat Pattern feature unfolded) as DXF, in millimetres.</summary>
    async Task ExportDxf()
    {
        var flats = Session.Result?.State.Flats;
        if (flats == null || flats.Count == 0)
        {
            await Dialogs.Message(this, "Export DXF", "There is no flat pattern: insert a Flat Pattern (Sheet Metal tab) first.");
            return;
        }
        var baseName = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : Session.UntitledName;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Flat Pattern DXF",
            DefaultExtension = "dxf",
            SuggestedFileName = baseName + ".dxf",
            FileTypeChoices = new[] { new FilePickerFileType("DXF") { Patterns = new[] { "*.dxf" } } },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var scale = Session.Doc.Units == UnitSystem.IPS ? 25.4 : 1.0;
            var written = new List<string>();
            foreach (var (bodyId, flat) in flats)
            {
                // one file per body: the chosen name for a single body, the body id added for several
                var p = flats.Count == 1 ? path : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{bodyId}.dxf");
                using var fs = File.Create(p);
                DxfWriter.Write(fs, flat, scale);
                written.Add(p);
            }
            Status($"Exported {string.Join(", ", written)}");
        }
        catch (Exception ex) { App.Log(ex, "Export failed"); await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    /// <summary>
    /// Opens the parts of the last autosave, each in its own tab: the files written in the same autosave tick as
    /// the newest one (older numbered files are left over from sessions with more parts open).
    /// </summary>
    async void RecoverAutosave()
    {
        var files = Directory.Exists(AutosaveDir)
            ? new DirectoryInfo(AutosaveDir).GetFiles("autosave*" + CadDocument.FileExtension).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : new List<FileInfo>();
        if (files.Count == 0) { Status("No autosave found."); return; }
        var newest = files[0].LastWriteTimeUtc;
        int n = 0;
        foreach (var f in files.Where(f => newest - f.LastWriteTimeUtc < TimeSpan.FromSeconds(10)).OrderBy(f => f.Name))
        {
            if (!await OpenFileAsync(f.FullName)) break;
            Session.FilePath = null;
            // recovered work is not saved anywhere yet: closing it must prompt
            Session.MarkDirty();
            n++;
        }
        if (n > 0) Status(n == 1 ? "Recovered the autosave: use Save As to keep it." : $"Recovered {n} autosaved parts: use Save As to keep them.");
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
            text += $"\n\nThese features depend on it and will fail without it:\n  {string.Join(", ", deps.Select(d => d.Name))}";
            var r = await Dialogs.YesNoCancel(this, "Confirm Delete", text, "Delete", "Delete with children");
            if (r == null) return;
            if (r == false) victims.AddRange(deps);
        }
        else if (!await Dialogs.YesNo(this, "Confirm Delete", text, "Delete", "Cancel")) return;
        Session.Change(() => { foreach (var v in victims) Session.Doc.Features.Remove(v); FixRollback(); });
        Selection.Clear();
        Status($"Deleted {string.Join(", ", victims.Select(v => v.Name))} (Ctrl+Z to undo)");
    }

    void FixRollback()
    {
        var d = Session.Doc;
        if (d.RollbackIndex is int r && r >= d.Features.Count) d.RollbackIndex = null;
    }

    void SetSuppressed(bool on)
    {
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

    void RollForward() => Session.Change(() => Session.Doc.RollbackIndex = null);

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
                    Status($"Can't move {f.Name} there: {moved[i].Name} uses {moved[j].Name}, which would come after it.");
                    return;
                }
        Session.Change(() =>
        {
            d.Features.Clear();
            d.Features.AddRange(moved);
            d.RollbackIndex = active >= moved.Count ? null : active;
        });
        Status($"Moved {f.Name} (Ctrl+Z to undo)");
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
