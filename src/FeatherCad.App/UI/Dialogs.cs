using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.Core;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.App.UI;

public static class Dialogs
{
    static Window MakeWindow(string title, Control content, double width = 420)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(14), Child = content },
            ShowInTaskbar = false,
        };
    }

    static StackPanel Buttons(params Button[] b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var x in b) { x.MinWidth = 80; x.HorizontalContentAlignment = HorizontalAlignment.Center; sp.Children.Add(x); }
        return sp;
    }

    /// <summary>A message (a problem when the title says something failed): a card in the window, else a dialog window.</summary>
    public static async Task Message(Window owner, string title, string text)
    {
        var kind = title.Contains("fail", StringComparison.OrdinalIgnoreCase) || title.Contains("error", StringComparison.OrdinalIgnoreCase) ? CardKind.Error : CardKind.Info;
        if (Card.ShowAsync(owner, kind, title, new[] { Card.Text(text) }, new[] { "OK" }, 0, 0) is { } shown) { await shown; return; }
        var ok = new Button { Content = "OK", IsDefault = true };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Buttons(ok));
        var w = MakeWindow(title, sp);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>Returns true (yes), false (no) or null (cancel).</summary>
    public static async Task<bool?> YesNoCancel(Window owner, string title, string text, string yesText = "Save", string noText = "Don't Save")
    {
        if (Card.ShowAsync(owner, CardKind.Question, text, Array.Empty<Control>(), new[] { yesText, noText, "Cancel" }, 0, 2) is { } shown)
            return await shown switch { 0 => true, 1 => false, _ => (bool?)null };
        bool? result = null;
        var yes = new Button { Content = yesText, IsDefault = true };
        var no = new Button { Content = noText };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Buttons(yes, no, cancel));
        var w = MakeWindow(title, sp);
        yes.Click += (_, _) => { result = true; w.Close(); };
        no.Click += (_, _) => { result = false; w.Close(); };
        cancel.Click += (_, _) => { result = null; w.Close(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Two-button question; true for the first button.</summary>
    public static async Task<bool> YesNo(Window owner, string title, string text, string yesText = "OK", string noText = "Cancel")
    {
        if (Card.ShowAsync(owner, CardKind.Question, title, new[] { Card.Text(text) }, new[] { yesText, noText }, 0, 1) is { } shown)
            return await shown == 0;
        bool result = false;
        var yes = new Button { Content = yesText, IsDefault = true };
        var no = new Button { Content = noText, IsCancel = true };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Buttons(yes, no));
        var w = MakeWindow(title, sp);
        yes.Click += (_, _) => { result = true; w.Close(); };
        no.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> Input(Window owner, string title, string label, string value)
    {
        string? result = null;
        var tb = new TextBox { Text = value };
        if (Card.ShowAsync(owner, CardKind.Info, title, new Control[] { Card.Text(label), tb }, new[] { "OK", "Cancel" }, 0, 1, focus: tb) is { } shown)
            return await shown == 0 ? tb.Text : null; // the card focuses the box with its text selected
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label });
        sp.Children.Add(tb);
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow(title, sp, 360);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    // ------------------------------------------------------------------------------------------

    public static async Task Equations(MainWindow owner)
    {
        var session = owner.Session;
        var rows = session.Doc.Equations.Select(e => new Equation { Name = e.Name, Expr = e.Expr }).ToList();
        var list = new StackPanel { Spacing = 3 };
        var info = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Text = "Global variables usable in any dimension or feature value, e.g. W = 100, H = W/2. Trig functions use degrees; units like 1in are converted to mm." };
        var valueCells = new List<(Equation row, TextBlock cell)>();
        // re-evaluate in place (rebuilding the rows would steal focus while tabbing between fields)
        void UpdateValues()
        {
            var (vals, errs) = Regenerator.EvaluateEquations(new CadDocument { Equations = rows });
            foreach (var (r, cell) in valueCells)
            {
                cell.Text = vals.TryGetValue(r.Name, out var val) ? Expr.Format(val) : errs.GetValueOrDefault(r.Name) ?? "";
                cell.Foreground = errs.ContainsKey(r.Name) ? Brushes.Red : Brushes.Black;
            }
        }
        void Refresh()
        {
            list.Children.Clear();
            valueCells.Clear();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,90,30") };
            header.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeight.SemiBold });
            var h2 = new TextBlock { Text = "Value / Equation", FontWeight = FontWeight.SemiBold }; Grid.SetColumn(h2, 1); header.Children.Add(h2);
            var h3 = new TextBlock { Text = "Evaluates to", FontWeight = FontWeight.SemiBold }; Grid.SetColumn(h3, 2); header.Children.Add(h3);
            list.Children.Add(header);
            var tmp = new CadDocument { Equations = rows };
            var (vals, errs) = Regenerator.EvaluateEquations(tmp);
            foreach (var r in rows)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,90,30") };
                var n = new TextBox { Text = r.Name, Margin = new Thickness(0, 0, 4, 0) };
                var e = new TextBox { Text = r.Expr, Margin = new Thickness(0, 0, 4, 0) };
                var v = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = vals.TryGetValue(r.Name, out var val) ? Expr.Format(val) : errs.GetValueOrDefault(r.Name) ?? "",
                    Foreground = errs.ContainsKey(r.Name) ? Brushes.Red : Brushes.Black,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var del = new Button { Content = "✖", Padding = new Thickness(4, 0) };
                var rr = r;
                valueCells.Add((rr, v));
                n.TextChanged += (_, _) => { rr.Name = n.Text?.Trim() ?? ""; UpdateValues(); };
                e.TextChanged += (_, _) => { rr.Expr = e.Text?.Trim() ?? "0"; UpdateValues(); };
                del.Click += (_, _) => { rows.Remove(rr); Refresh(); };
                Grid.SetColumn(e, 1); Grid.SetColumn(v, 2); Grid.SetColumn(del, 3);
                g.Children.Add(n); g.Children.Add(e); g.Children.Add(v); g.Children.Add(del);
                list.Children.Add(g);
            }
        }
        Refresh();
        var add = new Button { Content = "+ Add variable" };
        add.Click += (_, _) => { rows.Add(new Equation { Name = "Var" + (rows.Count + 1), Expr = "10" }); Refresh(); };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(info);
        sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 400 });
        sp.Children.Add(add);
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow("Equations, Global Variables", sp, 560);
        ok.Click += (_, _) =>
        {
            w.Close();
            session.Change(() => session.Doc.Equations = rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList());
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task Options(MainWindow owner)
    {
        var s = owner.Session;
        var units = new ComboBox { ItemsSource = new[] { "MMGS (millimeter, gram, second)", "IPS (inch, pound, second)" }, SelectedIndex = s.Doc.Units == UnitSystem.IPS ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var wheel = new CheckBox { Content = "Reverse mouse wheel zoom direction", IsChecked = owner.Viewport.ReverseWheel };
        var persp = new CheckBox { Content = "Perspective view", IsChecked = owner.Viewport.Camera.Perspective };
        var pivots = new[] { Session.RotateCenter.Selection, Session.RotateCenter.ModelCenter, Session.RotateCenter.UnderCursor };
        var pivot = new ComboBox
        {
            ItemsSource = new[] { "Rotate about the selection (default)", "Rotate about the model center", "Rotate about the point under the cursor" },
            SelectedIndex = Array.IndexOf(pivots, owner.Viewport.RotateAbout), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var container = new CheckBox { Content = "Always save parts as containers (with a preview image)", IsChecked = Session.UserSettings.SaveContainer };
        var color = new TextBox { Text = string.Join(", ", s.Doc.Color.Select(c => ((int)(c * 255)).ToString())) };
        var sheet = s.Doc.SheetMetal ?? new SheetMetalSettings();
        var bendMethod = new ComboBox { ItemsSource = new[] { "K-factor", "DIN 6935" }, SelectedIndex = sheet.Method == BendAllowanceMethod.Din6935 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var kFactor = new TextBox { Text = sheet.KFactor };
        bendMethod.SelectionChanged += (_, _) => kFactor.IsEnabled = bendMethod.SelectedIndex == 0;
        kFactor.IsEnabled = bendMethod.SelectedIndex == 0;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = "Document units", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(units);
        sp.Children.Add(new TextBlock { Text = "In IPS, values are inches; STEP/STL exports are converted to millimetres.", FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Part color (R, G, B 0-255)", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(color);
        sp.Children.Add(new TextBlock { Text = "Sheet metal bend allowance", FontWeight = FontWeight.SemiBold });
        var bendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto,4,70") };
        bendRow.Children.Add(bendMethod);
        var kLabel = new TextBlock { Text = "K-factor", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(kLabel, 2); bendRow.Children.Add(kLabel);
        Grid.SetColumn(kFactor, 4); bendRow.Children.Add(kFactor);
        sp.Children.Add(bendRow);
        sp.Children.Add(new TextBlock { Text = "For this part's flat patterns (a Flat Pattern can override it). K-factor 0 to 1, or an expression.", FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "View", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(wheel); sp.Children.Add(persp);
        sp.Children.Add(pivot);
        sp.Children.Add(new TextBlock { Text = "Middle-click a vertex, edge or face to rotate about that spot instead, until you middle-click empty space.", FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Files", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(container);
        sp.Children.Add(new TextBlock { Text = "Otherwise parts are saved as plain JSON, unless they embed other files (e.g. an imported model). Either kind opens normally.", FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow("Options", sp, 420);
        ok.Click += (_, _) =>
        {
            // view preferences belong to the user, not the document: remembered, no undo step, not "modified"
            owner.Viewport.ReverseWheel = wheel.IsChecked == true;
            owner.Viewport.Camera.Perspective = persp.IsChecked == true;
            Session.UserSettings.ReverseWheel = owner.Viewport.ReverseWheel;
            Session.UserSettings.Perspective = owner.Viewport.Camera.Perspective;
            if (pivot.SelectedIndex >= 0) owner.Viewport.RotateAbout = Session.UserSettings.RotateAbout = pivots[pivot.SelectedIndex];
            Session.UserSettings.SaveContainer = container.IsChecked == true;
            Session.UserSettings.Save();
            var newUnits = units.SelectedIndex == 1 ? UnitSystem.IPS : UnitSystem.MMGS;
            var parts = (color.Text ?? "").Split(',').Select(p => double.TryParse(p.Trim(), out var v) ? Math.Clamp(v / 255, 0, 1) : -1).ToArray();
            var newColor = parts.Length == 3 && parts.All(p => p >= 0) ? parts : s.Doc.Color;
            // sheet metal: kept as typed when it is a valid K-factor (0..1, an expression of the part's variables)
            var newSheet = new SheetMetalSettings { Method = bendMethod.SelectedIndex == 1 ? BendAllowanceMethod.Din6935 : BendAllowanceMethod.KFactor, KFactor = sheet.KFactor };
            var kText = (kFactor.Text ?? "").Trim();
            if (Expr.TryEval(kText, s.Lookup, out var kv, out _) && kv >= 0 && kv <= 1) newSheet.KFactor = kText;
            var oldSheet = s.Doc.SheetMetal ?? new SheetMetalSettings();
            bool sheetChanged = newSheet.Method != oldSheet.Method || newSheet.KFactor != oldSheet.KFactor;
            if (newUnits != s.Doc.Units || !newColor.SequenceEqual(s.Doc.Color) || sheetChanged)
                s.Change(() => { s.Doc.Units = newUnits; s.Doc.Color = newColor; if (sheetChanged) s.Doc.SheetMetal = newSheet; });
            owner.Viewport.Redraw();
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task Shortcuts(Window owner)
    {
        const string text = """
            VIEW
              Middle mouse drag ........ Rotate
              Middle click ............. Rotate about this spot (empty space: clear)
              Ctrl + middle drag ....... Pan
              Right mouse drag ......... Gestures: right Measure, up Normal To,
                                         left Zoom to Fit, down Sketch
                                         (sketching: Smart Dimension, Circle, Line, Rectangle)
              Shift + middle drag ...... Zoom
              Alt + middle drag ........ Roll
              Mouse wheel .............. Zoom at cursor
              F ........................ Zoom to fit
              Spacebar ................. View Selector (click a cube face/edge/corner)
              Ctrl+1..6 ................ Front/Back/Left/Right/Top/Bottom
              Ctrl+7 ................... Isometric
              Ctrl+8 ................... Normal To
              Arrow keys ............... Rotate 15°  (Shift: 90°)
              Ctrl + arrows ............ Pan

            SKETCH
              L Line  R Rectangle  C Circle  A Arc  P Point
              D Smart Dimension  T Trim   Esc  Select / cancel
              Double-click dimension ... Edit value
              Delete ................... Delete selection

            GENERAL
              Ctrl+N/O/S ............... New / Open / Save
              Ctrl+W ................... Close the part (its tab)
              Ctrl+Tab ................. Next part tab (Shift: previous)
              Ctrl+Z / Ctrl+Y .......... Undo / Redo
              Ctrl+B / Ctrl+Q .......... Rebuild / Force rebuild
              Enter / Esc .............. OK / Cancel in PropertyManager
            """;
        var ok = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 });
        sp.Children.Add(Buttons(ok));
        var w = MakeWindow("Keyboard & Mouse", sp, 520);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task About(Window owner)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = "FeatherCAD", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 42)) });
        // from git at build time (FeatherCadGitVersion in the .csproj): "0.0.1-dev.3+aeb382f[.dirty]"
        var info = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var (ver, build) = plus < 0 ? (info, "") : (info[..plus], info[(plus + 1)..]);
        var verText = new SelectableTextBlock { Text = $"Version {ver}" + (build.Length > 0 ? $"   (commit {build.Replace(".dirty", ", uncommitted changes")})" : ""), Foreground = Brushes.DimGray };
        ToolTip.SetTip(verText, info);
        sp.Children.Add(verText);
        sp.Children.Add(new TextBlock { Text = "Fast, lightweight, open source parametric 3D CAD.", TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Own plane-based B-rep kernel in C#, Avalonia UI, OpenGL viewport.\nNative formats: .fcprt parts (JSON), .fcasm assemblies. Export: STEP AP214, STL.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        sp.Children.Add(new TextBlock { Text = "License: MIT", Foreground = Brushes.DimGray });
        sp.Children.Add(Buttons(ok));
        var w = MakeWindow("About FeatherCAD", sp, 420);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }
}
