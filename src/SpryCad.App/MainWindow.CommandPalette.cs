using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// The command palette (S at the cursor, Ctrl+K anywhere): a search box over the graphics area listing the commands
/// of the mode (a part, a sketch, an assembly): with nothing typed the ones used most (this session's first, then the
/// usual ones), typed into, every command whose name has the words. Up and Down pick, Enter runs, Esc closes. And
/// Enter with nothing open runs the last command again, as SolidWorks does.
/// </summary>
public partial class MainWindow
{
    /// <summary>A command the palette can run: its name, icon, and what it does.</summary>
    sealed record PaletteCommand(string Name, string Icon, Action Run);

    // the last command run (Enter runs it again) and how often each has been run this session
    (string name, string icon, Action run)? _lastCommand;
    readonly Dictionary<string, int> _commandUse = new();

    /// <summary>A command run (from the toolbar or the palette): it is the one Enter repeats, and counted as used.</summary>
    void NoteCommand(string name, string icon, Action run)
    {
        _lastCommand = (name, icon, run);
        _commandUse[name] = _commandUse.GetValueOrDefault(name) + 1;
    }

    /// <summary>Enter with nothing open: the last command again. False when there is none.</summary>
    bool RepeatLastCommand()
    {
        if (_lastCommand is not { } c || _page != null || _edit != null) return false;
        Status($"Repeating {c.name}.");
        c.run();
        return true;
    }

    /// <summary>The commands of the mode the window is in.</summary>
    List<PaletteCommand> PaletteCommands()
    {
        var l = new List<PaletteCommand>();
        void C(string icon, string name, Action run) => l.Add(new PaletteCommand(name, icon, run));
        void T(string icon, string name, SketchTool t) => C(icon, name, () => SetSketchTool(t));
        if (Asm != null)
        {
            C("insertcomp", "Insert Components", InsertComponentCommand);
            C("mates", "Mate", MateCommand);
            C("motor", "Motor", MotorCommand);
            C("thread", "Insert Fastener", InsertFastenerCommand);
            C("lpattern", "Linear Component Pattern", () => PatternPage(Core.Assembly.PatternKind.Linear));
            C("cpattern", "Circular Component Pattern", () => PatternPage(Core.Assembly.PatternKind.Circular));
            C("mirror", "Mirror Components", () => PatternPage(Core.Assembly.PatternKind.Mirror));
            C("formsub", "Form Sub-assembly", () => FormSubAssemblyPage());
            C("replace", "Replace Component", () => _ = ReplaceComponent());
            C("fix", "Fix", () => SetFixed(true));
            C("float", "Float", () => SetFixed(false));
            C("lightweight", "Lightweight", ToggleLightweight);
            C("hide", "Hide / Show", ToggleHidden);
            C("isolate", "Isolate", () => Isolate());
            C("explode", "Exploded View", ExplodeCommand);
            C("explode", "Explode / Collapse", ToggleExplode);
            C("measure", "Measure", MeasureCommand);
            C("mass", "Mass Properties", MassPropertiesCommand);
        }
        else if (_sketch != null)
        {
            C("exitsketch", "Exit Sketch", () => ExitSketch(true));
            T("dimension", "Smart Dimension", SketchTool.SmartDimension);
            T("line", "Line", SketchTool.Line);
            T("centerline", "Centerline", SketchTool.Centerline);
            T("rect", "Corner Rectangle", SketchTool.Rectangle);
            T("centerrect", "Center Rectangle", SketchTool.CenterRectangle);
            T("circle", "Circle", SketchTool.Circle);
            T("centerarc", "Centerpoint Arc", SketchTool.CenterArc);
            T("arc3", "3 Point Arc", SketchTool.ThreePointArc);
            T("tangentarc", "Tangent Arc", SketchTool.TangentArc);
            T("polygon", "Polygon", SketchTool.Polygon);
            T("slot", "Straight Slot", SketchTool.Slot);
            T("spline", "Spline", SketchTool.Spline);
            T("point", "Point", SketchTool.Point);
            T("trim", "Trim Entities", SketchTool.Trim);
            C("sketchfillet", "Sketch Fillet", () => { _sketch?.StartFillet(); UpdateToolButtons(); BuildSketchPage(); });
            C("sketchmirror", "Mirror Entities", MirrorEntities);
            C("offset", "Offset Entities", OffsetEntities);
            C("convert", "Convert Entities", ConvertEntities);
            C("construction", "Construction Geometry", ToggleConstruction);
            C("relations", "Add Relations", AddRelationCommand);
            C("sketchpicture", "Sketch Picture", SketchPictureCommand);
            C("axis", "Sketch Axes", SketchAxesCommand);
        }
        else
        {
            C("sketch", "Sketch", () => StartSketchCommand());
            C("extrude", "Extruded Boss/Base", () => ExtrudeCommand(false));
            C("revolve", "Revolved Boss/Base", () => RevolveCommand(false));
            C("sweep", "Swept Boss/Base", () => SweepCommand(false));
            C("loft", "Lofted Boss/Base", () => LoftCommand(false));
            C("helical", "Helical Boss/Base", () => HelicalCommand(false));
            C("cut", "Extruded Cut", () => ExtrudeCommand(true));
            C("cutrevolve", "Revolved Cut", () => RevolveCommand(true));
            C("cutsweep", "Swept Cut", () => SweepCommand(true));
            C("cutloft", "Lofted Cut", () => LoftCommand(true));
            C("cuthelical", "Helical Cut", () => HelicalCommand(true));
            C("hole", "Hole Wizard", HoleCommand);
            C("thread", "Thread", ThreadCommand);
            C("fillet", "Fillet", () => BlendCommand(true));
            C("chamfer", "Chamfer", () => BlendCommand(false));
            C("shell", "Shell", ShellCommand);
            C("draft", "Draft", DraftCommand);
            C("lpattern", "Linear Pattern", LinearPatternCommand);
            C("cpattern", "Circular Pattern", CircularPatternCommand);
            C("mirror", "Mirror", MirrorCommand);
            C("plane", "Reference Plane", PlaneCommand);
            C("axis", "Reference Axis", AxisCommand);
            C("combine", "Combine", CombineCommand);
            C("baseflange", "Base Flange", BaseFlangeCommand);
            C("edgeflange", "Edge Flange", EdgeFlangeCommand);
            C("hem", "Hem", HemCommand);
            C("sheetcut", "Sheet Cut", SheetCutCommand);
            C("corners", "Corners", CornersCommand);
            C("flatten", "Flatten", ToggleFlattenCommand);
            C("export-dxf", "Export Flat Pattern", () => _ = ExportFlatCommand());
            C("measure", "Measure", MeasureCommand);
            C("mass", "Mass Properties", MassPropertiesCommand);
            C("material", "Edit Material", EditMaterialCommand);
            C("appearance", "Appearance", () => _ = PartAppearanceCommand());
            C("equations", "Parameters", () => _ = Dialogs.Equations(this));
            C("table", "Design Table", () => _ = Dialogs.DesignTables(this));
            C("configure", "Configure", () => _ = Dialogs.ConfigurePart(this));
            C("import", "Import STEP", () => _ = ImportDialog());
        }
        // the view, in any mode
        C("normalto", "Normal To", NormalTo);
        C("fit", "Zoom to Fit", () => Viewport.ZoomToFit());
        C("section", "Section View", SectionCommand);
        C("colorbyfeature", Asm != null ? "Color by Part" : "Color by Feature", ToggleColorByFeature);
        C("planes", "Hide / Show Planes", TogglePlanes);
        foreach (var v in new[] { "Front", "Back", "Left", "Right", "Top", "Bottom", "Isometric", "Trimetric", "Dimetric" })
            C("views", v + " View", () => Viewport.StandardView(v));
        C("export-step", "Export STEP", () => _ = Export("step"));
        C("export-stl", "Export STL", () => _ = Export("stl"));
        C("rebuild", "Rebuild", () => Rebuild());
        return l;
    }

    // the usual ones of each mode, shown (after this session's) when nothing is typed
    static readonly string[] UsualPart = { "Sketch", "Extruded Boss/Base", "Extruded Cut", "Fillet", "Hole Wizard", "Reference Plane", "Measure", "Section View" };
    static readonly string[] UsualSketch = { "Line", "Corner Rectangle", "Circle", "Smart Dimension", "Trim Entities", "Exit Sketch" };
    static readonly string[] UsualAssembly = { "Insert Components", "Mate", "Hide / Show", "Isolate", "Measure", "Exploded View" };

    Border? _cmdPalette;

    void CloseCommandPalette()
    {
        if (_cmdPalette == null) return;
        Overlay.Children.Remove(_cmdPalette);
        _cmdPalette = null;
        Viewport.Focus();
    }

    /// <summary>The palette: at the cursor (S) or at the top of the graphics area (Ctrl+K).</summary>
    void OpenCommandPalette(bool atCursor)
    {
        CloseCommandPalette();
        var all = PaletteCommands();
        var usual = Asm != null ? UsualAssembly : _sketch != null ? UsualSketch : UsualPart;
        var search = new TextBox { Watermark = "Type a command…", MinWidth = 240, FontSize = TypeScale.Body };
        var list = new ListBox { MaxHeight = 260, FontSize = TypeScale.Body, Background = Brushes.Transparent };
        List<PaletteCommand> shown = new();
        void Fill()
        {
            var q = (search.Text ?? "").Trim();
            if (q.Length == 0)
                shown = all.Where(c => _commandUse.ContainsKey(c.Name)).OrderByDescending(c => _commandUse[c.Name])
                    .Concat(usual.Select(n => all.FirstOrDefault(c => c.Name == n)).OfType<PaletteCommand>())
                    .DistinctBy(c => c.Name).Take(8).ToList();
            else
            {
                var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                shown = all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    // names starting with what was typed first
                    .OrderBy(c => c.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Name).Take(12).ToList();
            }
            list.ItemsSource = shown.Select(c => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { Icons.Make(c.Icon, 18), new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center } },
            }).ToList();
            list.SelectedIndex = shown.Count > 0 ? 0 : -1;
        }
        void Run(int i)
        {
            if (i < 0 || i >= shown.Count) return;
            var c = shown[i];
            CloseCommandPalette();
            NoteCommand(c.Name, c.Icon, c.Run);
            c.Run();
        }
        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, shown.Count - 1); e.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); e.Handled = true; break;
                case Key.Enter: Run(list.SelectedIndex); e.Handled = true; break;
                case Key.Escape: CloseCommandPalette(); e.Handled = true; break;
            }
        };
        list.Tapped += (_, _) => Run(list.SelectedIndex);
        search.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // (clicked away: gone; clicked on its list: kept to run the click)
            if (_cmdPalette != null && !list.IsKeyboardFocusWithin && !search.IsFocused && !list.IsPointerOver) CloseCommandPalette();
        });
        Fill();
        _cmdPalette = new Border
        {
            Child = new StackPanel { Spacing = 2, Children = { search, list } },
            Background = Palette.Surface, BorderBrush = Palette.BorderStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Width = 270,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 14, Color = Color.FromArgb(56, 30, 50, 80) }),
        };
        Overlay.Children.Add(_cmdPalette);
        var at = atCursor ? Viewport.LastPointer : new Point(Overlay.Bounds.Width / 2 - 135, 12);
        Canvas.SetLeft(_cmdPalette, Math.Clamp(at.X - 20, 4, Math.Max(4, Overlay.Bounds.Width - 274)));
        Canvas.SetTop(_cmdPalette, Math.Clamp(at.Y - 16, 4, Math.Max(4, Overlay.Bounds.Height - 310)));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
