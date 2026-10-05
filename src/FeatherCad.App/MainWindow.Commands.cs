using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FeatherCad.App.Session;
using FeatherCad.App.Sketcher;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.App;

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

    static MenuItem Sub(string header, params object[] items)
    {
        var m = new MenuItem { Header = header };
        foreach (var i in items) m.Items.Add(i is string ? new Separator() : i);
        return m;
    }

    void BuildMenu()
    {
        MainMenu.Items.Add(Sub("_File",
            Item("_New", () => _ = NewFile(), "Ctrl+N", "new"),
            Item("_Open...", () => _ = OpenFileDialog(), "Ctrl+O", "open"),
            Item("_Save", () => _ = Save(), "Ctrl+S", "save"),
            Item("Save _As...", () => _ = SaveAs(), "Ctrl+Shift+S"),
            "-",
            Item("Export _STEP...", () => _ = Export("step"), null, "export"),
            Item("Export S_TL...", () => _ = Export("stl"), null, "export"),
            "-",
            Item("Recover Autosave", () => RecoverAutosave()),
            "-",
            Item("E_xit", Close)));
        MainMenu.Items.Add(Sub("_Edit",
            Item("_Undo", Undo, "Ctrl+Z", "undo"),
            Item("_Redo", Redo, "Ctrl+Y", "redo"),
            "-",
            Item("_Delete", DeleteSelected, "Delete"),
            Item("Re_build", () => Session.RequestRebuild(), "Ctrl+B", "rebuild"),
            Item("Force Rebuild", () => Session.ForceRebuild(), "Ctrl+Q"),
            "-",
            Item("Suppress", () => SetSuppressed(true)),
            Item("Unsuppress", () => SetSuppressed(false)),
            Item("Roll Forward to End", RollForward)));
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
                Item("Shaded With Edges", () => SetStyle(DisplayStyle.ShadedWithEdges)),
                Item("Shaded", () => SetStyle(DisplayStyle.Shaded)),
                Item("Hidden Lines Visible", () => SetStyle(DisplayStyle.HiddenLinesVisible)),
                Item("Wireframe", () => SetStyle(DisplayStyle.Wireframe))),
            Item("Perspective", () => { Viewport.Camera.Perspective = !Viewport.Camera.Perspective; Viewport.Redraw(); }),
            "-",
            Item("Planes", TogglePlanes, null, "planes"),
            Item("Origins", () => { Viewport.ShowOrigin = !Viewport.ShowOrigin; Viewport.Redraw(); }, null, "origin"),
            Item("Sketch Relations", () => { if (_sketch != null) { _sketch.ShowRelations = !_sketch.ShowRelations; _sketch.UpdateOverlay(Viewport); } })));
        MainMenu.Items.Add(Sub("_Insert",
            Item("_Sketch", () => StartSketchCommand(), null, "sketch"),
            "-",
            Sub("_Boss/Base",
                Item("_Extrude...", () => ExtrudeCommand(false), null, "extrude"),
                Item("_Revolve...", () => RevolveCommand(false), null, "revolve")),
            Sub("_Cut",
                Item("_Extrude...", () => ExtrudeCommand(true), null, "cut"),
                Item("_Revolve...", () => RevolveCommand(true), null, "cutrevolve")),
            Sub("_Features",
                Item("_Fillet...", () => BlendCommand(true), null, "fillet"),
                Item("_Chamfer...", () => BlendCommand(false), null, "chamfer"),
                Item("_Hole Wizard...", HoleCommand, null, "hole"),
                Item("_Shell...", ShellCommand, null, "shell"),
                Item("C_ombine...", CombineCommand, null, "combine")),
            Sub("_Pattern/Mirror",
                Item("_Linear Pattern...", LinearPatternCommand, null, "lpattern"),
                Item("_Circular Pattern...", CircularPatternCommand, null, "cpattern"),
                Item("_Mirror...", MirrorCommand, null, "mirror")),
            Sub("_Reference Geometry",
                Item("_Plane...", PlaneCommand, null, "plane"))));
        MainMenu.Items.Add(Sub("_Tools",
            Item("_Equations...", () => _ = Dialogs.Equations(this), null, "equations"),
            Item("_Mass Properties...", () => _ = Dialogs.MassProperties(this), null, "mass"),
            "-",
            Item("_Options...", () => _ = Dialogs.Options(this), null, "options")));
        MainMenu.Items.Add(Sub("_Help",
            Item("_Keyboard Shortcuts", () => _ = Dialogs.Shortcuts(this), null, "help"),
            Item("_About FeatherCAD", () => _ = Dialogs.About(this))));
    }

    void BuildQuickAccess()
    {
        void B(string icon, string tip, Action a)
        {
            var b = new Button { Classes = { "tb" }, Content = Icons.Make(icon, 18) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            QuickAccess.Children.Add(b);
        }
        B("new", "New (Ctrl+N)", () => _ = NewFile());
        B("open", "Open (Ctrl+O)", () => _ = OpenFileDialog());
        B("save", "Save (Ctrl+S)", () => _ = Save());
        B("undo", "Undo (Ctrl+Z)", Undo);
        B("redo", "Redo (Ctrl+Y)", Redo);
        B("rebuild", "Rebuild (Ctrl+B)", () => Session.RequestRebuild());
        B("options", "Options", () => _ = Dialogs.Options(this));
    }

    // ------------------------------------------------------------------------------------------
    // CommandManager

    void BuildCommandManager()
    {
        foreach (var name in new[] { "Features", "Sketch", "Evaluate" })
        {
            var t = new ToggleButton { Content = name, Classes = { "tab" } };
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
            case "Features":
                Cmd("extrude", "Extruded\nBoss/Base", () => ExtrudeCommand(false));
                Cmd("revolve", "Revolved\nBoss/Base", () => RevolveCommand(false));
                Sep();
                Cmd("cut", "Extruded\nCut", () => ExtrudeCommand(true));
                Cmd("cutrevolve", "Revolved\nCut", () => RevolveCommand(true));
                Cmd("hole", "Hole\nWizard", HoleCommand);
                Sep();
                Cmd("fillet", "Fillet", () => BlendCommand(true));
                Cmd("chamfer", "Chamfer", () => BlendCommand(false));
                Cmd("shell", "Shell", ShellCommand);
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
                Cmd("convert", "Convert\nEntities", ConvertEntities);
                Cmd("construction", "Construction\nGeometry", ToggleConstruction);
                Cmd("normalto", "Normal\nTo", NormalTo);
                break;
            case "Evaluate":
                Cmd("mass", "Mass\nProperties", () => _ = Dialogs.MassProperties(this));
                Cmd("equations", "Equations", () => _ = Dialogs.Equations(this));
                Sep();
                Cmd("export", "Export\nSTEP", () => _ = Export("step"));
                Cmd("export", "Export\nSTL", () => _ = Export("stl"));
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
        ToolTip.SetTip(b, label.Replace('\n', ' '));
        b.Click += (_, _) => click();
        if (tool != null) _toolButtons[tool.Value] = b;
        CommandBar.Children.Add(b);
    }

    void Sep() => CommandBar.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(210, 216, 225)), Margin = new Thickness(4, 6) });

    void UpdateToolButtons()
    {
        foreach (var (t, b) in _toolButtons)
        {
            b.IsEnabled = _sketch != null;
            if (_sketch != null && _sketch.Tool == t) b.Classes.Add("active"); else b.Classes.Remove("active");
        }
    }

    // ------------------------------------------------------------------------------------------
    // panel tabs (FeatureManager / PropertyManager)

    Button? _fmTab, _pmTab;

    void BuildPanelTabs()
    {
        _fmTab = new Button { Classes = { "tb" }, Content = Icons.Make("part", 18) };
        ToolTip.SetTip(_fmTab, "FeatureManager Design Tree");
        _fmTab.Click += (_, _) => { if (_page != null) { Tree.IsVisible = !Tree.IsVisible; PmHost.IsVisible = !Tree.IsVisible; } };
        _pmTab = new Button { Classes = { "tb" }, Content = Icons.Make("options", 18) };
        ToolTip.SetTip(_pmTab, "PropertyManager");
        _pmTab.Click += (_, _) => { if (_page != null) { Tree.IsVisible = false; PmHost.IsVisible = true; } };
        PanelTabs.Children.Add(_fmTab);
        PanelTabs.Children.Add(_pmTab);
        UpdatePanelTabs();
    }

    void UpdatePanelTabs()
    {
        if (_pmTab != null) _pmTab.IsEnabled = _page != null;
    }

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
        df.Items.Add(Item("Shaded With Edges", () => SetStyle(DisplayStyle.ShadedWithEdges)));
        df.Items.Add(Item("Shaded", () => SetStyle(DisplayStyle.Shaded)));
        df.Items.Add(Item("Hidden Lines Visible", () => SetStyle(DisplayStyle.HiddenLinesVisible)));
        df.Items.Add(Item("Wireframe", () => SetStyle(DisplayStyle.Wireframe)));
        disp.Flyout = df;
        H("planes", "Hide/Show Planes", TogglePlanes);
    }

    Flyout OrientationFlyout()
    {
        var grid = new WrapPanel { Width = 250 };
        foreach (var v in new[] { "Front", "Back", "Left", "Right", "Top", "Bottom", "Isometric", "Trimetric", "Dimetric" })
        {
            var b = new Button { Content = v, Width = 78, Margin = new Thickness(2), FontSize = 11 };
            b.Click += (_, _) => { Viewport.StandardView(v); _orientationFlyout?.Hide(); };
            grid.Children.Add(b);
        }
        var nb = new Button { Content = "Normal To", Width = 78, Margin = new Thickness(2), FontSize = 11 };
        nb.Click += (_, _) => { NormalTo(); _orientationFlyout?.Hide(); };
        grid.Children.Add(nb);
        var f = new Flyout { Content = grid, Placement = PlacementMode.Bottom };
        return f;
    }

    Flyout? _orientationFlyout;

    void ShowOrientationFlyout()
    {
        _orientationFlyout ??= OrientationFlyout();
        _orientationFlyout.Placement = PlacementMode.Pointer;
        _orientationFlyout.ShowAt(Viewport, true);
    }

    void SetStyle(DisplayStyle s) { Viewport.Style = s; Viewport.Redraw(); }

    void TogglePlanes() { _showPlanes = !_showPlanes; RefreshScene(); }

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

    void OnViewportContext(PointerInfo p)
    {
        var menu = new ContextMenu();
        if (_sketch != null)
        {
            menu.Items.Add(Item("Select", () => SetSketchTool(SketchTool.Select)));
            menu.Items.Add(Item("End Chain / Cancel", () => _sketch.CancelOperation()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Line", () => SetSketchTool(SketchTool.Line), "L", "line"));
            menu.Items.Add(Item("Rectangle", () => SetSketchTool(SketchTool.Rectangle), "R", "rect"));
            menu.Items.Add(Item("Circle", () => SetSketchTool(SketchTool.Circle), "C", "circle"));
            menu.Items.Add(Item("Smart Dimension", () => SetSketchTool(SketchTool.SmartDimension), "D", "dimension"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Exit Sketch", () => ExitSketch(true), null, "exitsketch"));
            menu.Items.Add(Item("Discard Changes", () => ExitSketch(false), null, "cancel"));
        }
        else
        {
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
                if (sk != null) menu.Items.Add(Item("Edit Sketch", () => EditFeature(sk)));
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
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
            menu.Items.Add(Item(f is SketchFeature ? "Edit Sketch" : "Edit Feature", () => EditFeature(id), null, f is SketchFeature ? "sketch" : null));
            var sk = f.Consumes.FirstOrDefault(c => Session.Doc.Find(c) is SketchFeature);
            if (sk != null && f is not HoleFeature) menu.Items.Add(Item("Edit Sketch", () => EditFeature(sk), null, "sketch"));
            if (f is HoleFeature hf) menu.Items.Add(Item("Edit Hole Positions", () => EditFeature(hf.SketchId), null, "sketch"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(f.Suppressed ? "Unsuppress" : "Suppress", () => SetSuppressed(!f.Suppressed)));
            menu.Items.Add(Item("Rename", () => _ = RenameFeature(f)));
            menu.Items.Add(Item("Roll Back", () => RollbackTo(id, false), null, "rollback"));
            menu.Items.Add(Item("Delete", DeleteSelected));
        }
        if (Session.Doc.RollbackIndex != null) menu.Items.Add(Item("Roll to End", RollForward));
        if (menu.Items.Count > 0) menu.Open(row);
    }

    // ------------------------------------------------------------------------------------------
    // file commands

    static readonly FilePickerFileType FcadType = new("FeatherCAD Part") { Patterns = new[] { "*.fcad" } };

    async Task<bool> ConfirmDiscard()
    {
        if (!Session.Dirty) return true;
        var r = await Dialogs.YesNoCancel(this, "FeatherCAD", $"Save changes to {Session.Title.TrimEnd(' ', '*')}?");
        if (r == null) return false;
        if (r == true) return await Save();
        return true;
    }

    async Task NewFile()
    {
        if (!await ConfirmDiscard()) return;
        LeaveModes();
        Session.New();
        Viewport.StandardView("Trimetric", fit: true);
    }

    async Task OpenFileDialog()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Part",
            FileTypeFilter = new[] { FcadType },
            AllowMultiple = false,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenFile(path);
    }

    public void OpenFile(string path)
    {
        try
        {
            LeaveModes();
            Session.Load(path);
            Status("Opened " + path);
            _fitAfterLoad = true;
        }
        catch (Exception ex) { _ = Dialogs.Message(this, "Open failed", ex.Message); }
    }
    bool _fitAfterLoad;

    async Task<bool> Save()
    {
        if (Session.FilePath == null) return await SaveAs();
        try { Session.Save(Session.FilePath); Status("Saved " + Session.FilePath); return true; }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); return false; }
    }

    async Task<bool> SaveAs()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Part As",
            DefaultExtension = "fcad",
            SuggestedFileName = Session.FilePath != null ? Path.GetFileName(Session.FilePath) : "Part1.fcad",
            FileTypeChoices = new[] { FcadType },
        });
        var path = f?.TryGetLocalPath();
        if (path == null) return false;
        try { Session.Save(path); Status("Saved " + path); return true; }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); return false; }
    }

    async Task Export(string kind)
    {
        var r = Session.Result;
        if (r == null || r.State.Bodies.Count == 0) { await Dialogs.Message(this, "Export", "There is no solid body to export."); return; }
        var type = kind == "step"
            ? new FilePickerFileType("STEP AP214") { Patterns = new[] { "*.step", "*.stp" } }
            : new FilePickerFileType("STL (binary)") { Patterns = new[] { "*.stl" } };
        var baseName = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : "Part1";
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
            using var fs = File.Create(path);
            if (kind == "step") StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), baseName, scale);
            else StlWriter.Write(fs, r.State.Bodies.Select(b => b.Solid), scale);
            Status("Exported " + path);
        }
        catch (Exception ex) { await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    void RecoverAutosave()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD", "autosave" + CadDocument.FileExtension);
        if (!File.Exists(p)) { Status("No autosave found."); return; }
        OpenFile(p);
        Session.FilePath = null;
    }

    // ------------------------------------------------------------------------------------------
    // edit commands

    void Undo()
    {
        if (_sketch != null) { if (_sketch.Undo()) Status("Undo"); return; }
        if (_page != null) return;
        Session.Undo();
    }

    void Redo()
    {
        if (_sketch != null) { _sketch.Redo(); return; }
        if (_page != null) return;
        Session.Redo();
    }

    void DeleteSelected()
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
        Session.Change(() => { foreach (var v in victims) Session.Doc.Features.Remove(v); FixRollback(); });
        Selection.Clear();
        Status($"Deleted {string.Join(", ", victims.Select(v => v.Name))}");
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
