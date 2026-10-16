using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App.UI;

public static class Dialogs
{
    internal static Window MakeWindow(string title, Control content, double width = 420)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = Spacing.Dialog, Child = content },
            ShowInTaskbar = false,
        };
        Compact(w);
        return w;
    }

    /// <summary>
    /// A dialog window as dense as the panels: its text and the theme's controls (buttons, boxes, lists) in the body
    /// size; its OK / Cancel buttons as a card's (the keyboard's ring), the default one (Enter) accent-styled.
    /// </summary>
    internal static void Compact(Window w)
    {
        w.FontSize = TypeScale.Body;
        w.Resources["ControlContentThemeFontSize"] = TypeScale.Body;
        // found once the window is up, so buttons added any way (Buttons below or the dialog's own) get it
        w.Opened += (_, _) =>
        {
            foreach (var b in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Button>())
            {
                if (!b.IsDefault && !b.IsCancel) continue;
                if (!b.Classes.Contains("card")) b.Classes.Add("card");
                if (b.IsDefault && !b.Classes.Contains("accent")) b.Classes.Add("accent");
            }
        };
    }

    /// <summary>A small delete (✕) button for a list row or column: the Cancel icon, in a target big enough to hit.</summary>
    internal static Button DeleteButton(string tip)
    {
        var b = new Button
        {
            Content = Icons.Make("cancel", 12), Padding = new Thickness(3), MinWidth = 22, MinHeight = 22,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "tb" },
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A dialog's buttons, one size (at least 80 wide, the text centered), in a row at the right.</summary>
    static StackPanel ButtonStrip(Button[] b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.M, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var x in b) { x.MinWidth = 80; x.HorizontalContentAlignment = HorizontalAlignment.Center; sp.Children.Add(x); }
        return sp;
    }

    /// <summary>
    /// A dialog's button row, under its body: the buttons at the right, <paramref name="left"/> (a status, a total, an
    /// option) at the left when there is one. Every dialog's the same: the gap above it, the buttons' size and spacing.
    /// </summary>
    internal static Control ButtonRow(Control? left, params Button[] b)
    {
        var strip = ButtonStrip(b);
        if (left == null) { strip.Margin = new Thickness(0, Spacing.L, 0, 0); return strip; }
        var row = new DockPanel { Margin = new Thickness(0, Spacing.L, 0, 0) };
        DockPanel.SetDock(strip, Dock.Right);
        left.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(strip); row.Children.Add(left);
        return row;
    }

    internal static Control ButtonRow(params Button[] b) => ButtonRow(null, b);

    /// <summary>A dialog's body with its button row under it (the row's gap its own, not the body's spacing).</summary>
    internal static Control WithButtons(Control body, Control? left, params Button[] b) =>
        new StackPanel { Children = { body, ButtonRow(left, b) } };

    /// <summary>
    /// A large dialog's footer (Options, the library editor): a rule across the window and the buttons under it, at the
    /// right, <paramref name="left"/> at the left. For a window whose content runs to its edges (no dialog padding).
    /// </summary>
    internal static Border FooterBar(Control? left, params Button[] b)
    {
        var strip = ButtonStrip(b);
        var row = new DockPanel { Margin = Spacing.FooterBar };
        DockPanel.SetDock(strip, Dock.Right);
        row.Children.Add(strip);
        if (left != null) { left.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(left); }
        return new Border { Child = row, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    /// <summary>A message (a problem when the title says something failed): a card in the window, else a dialog window.</summary>
    public static async Task Message(Window owner, string title, string text)
    {
        var kind = title.Contains("fail", StringComparison.OrdinalIgnoreCase) || title.Contains("error", StringComparison.OrdinalIgnoreCase) ? CardKind.Error : CardKind.Info;
        if (Card.ShowAsync(owner, kind, title, new[] { Card.Text(text) }, new[] { "OK" }, 0, 0) is { } shown) { await shown; return; }
        var ok = new Button { Content = "OK", IsDefault = true };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var w = MakeWindow(title, WithButtons(sp, null, ok));
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
        var w = MakeWindow(title, WithButtons(sp, null, yes, no, cancel));
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
        var w = MakeWindow(title, WithButtons(sp, null, yes, no));
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
        var w = MakeWindow(title, WithButtons(sp, null, ok, cancel), 360);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>The part's parameters, on its equations (<see cref="ParametersDialog"/>).</summary>
    public static Task Equations(MainWindow owner) => ParametersDialog.Show(owner);

    /// <summary>
    /// A feature's Suppress When condition: suppressed while it holds ("Teeth &lt; 30"), with what it gives now.
    /// Blank removes the condition.
    /// </summary>
    public static async Task SuppressWhen(MainWindow owner, Feature f)
    {
        var session = owner.Session;
        var lookup = session.Lookup;
        var tb = new TextBox { Text = f.SuppressWhen ?? "", Watermark = "e.g. Teeth < 30 or Size == 2" };
        var now = new TextBlock { FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap };
        void Update()
        {
            var text = tb.Text?.Trim() ?? "";
            if (text.Length == 0) { now.Text = "No condition set. The feature is built unless manually suppressed."; now.Foreground = Palette.TextMuted; return; }
            if (!Expr.TryEvaluate(text, lookup, out var v, out var err)) { now.Text = err; now.Foreground = Palette.Danger; return; }
            now.Text = Expr.Truthy(v.Value) ? "Currently true: the feature and its dependents are suppressed." : "Currently false: the feature is built.";
            now.Foreground = Palette.TextMuted;
        }
        tb.TextChanged += (_, _) => Update();
        Update();
        var names = Regenerator.EvaluateEquations(session.Doc).values.Keys.ToList();
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = $"Suppress {f.Name} when", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(tb);
        sp.Children.Add(now);
        sp.Children.Add(new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap,
            Text = "Operators: < <= > >= == !=, combined with and, or, not. "
                + (names.Count > 0 ? "Variables: " + string.Join(", ", names) + "." : "Define variables in Equations first."),
        });
        var w = MakeWindow("Suppress When", WithButtons(sp, null, ok, cancel), 420);
        ok.Click += (_, _) =>
        {
            var text = tb.Text?.Trim() ?? "";
            if (text.Length > 0 && !Expr.TryEvaluate(text, lookup, out _, out _)) return; // (the error shows under the box)
            w.Close();
            var value = text.Length == 0 ? null : text;
            if (value != f.SuppressWhen) session.Change(() => f.SuppressWhen = value);
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
    }

    /// <summary>The part's parameters, on its first design table (<see cref="ParametersDialog"/>).</summary>
    public static Task DesignTables(MainWindow owner) => ParametersDialog.Show(owner, tables: true);

    /// <summary>
    /// The Configure dialog: the part's user-facing parameters (design tables as lists of their rows, variables as
    /// values, 0..1 ones as checkboxes) set to <paramref name="current"/> (else the part's own). Returns every
    /// parameter's value by name, or null when cancelled; with <paramref name="allText"/>, a checkbox (whether to set
    /// them for all of the part's instances).
    /// </summary>
    public static async Task<(Dictionary<string, string> values, bool all)?> Configure(Window owner, CadDocument part, IReadOnlyDictionary<string, string>? current,
        string title, string? note = null, string? allText = null)
    {
        var ps = part.UserParameters();
        var values = part.Defaults();
        if (current != null) foreach (var (k, v) in current) if (values.ContainsKey(k)) values[k] = v;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*"), RowDefinitions = new RowDefinitions(string.Join(",", ps.Select(_ => "Auto,Auto"))) };
        var errors = new Dictionary<string, TextBlock>();
        var summary = new TextBlock { FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap };
        void Summary()
        {
            // what the choices give: the internal values too, so a size can be checked before OK
            var (vals, _) = Regenerator.EvaluateEquations(part.Configured(values));
            var shown = vals.Where(kv => !ps.Any(p => p.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))).Take(12).Select(kv => $"{kv.Key} {part.Units.Show(kv.Value)}");
            summary.Text = string.Join(", ", shown);
        }
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            var label = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
            if (p.Description != null) ToolTip.SetTip(label, p.Description);
            Control input;
            if (p.IsChoice)
            {
                var cb = new ComboBox { ItemsSource = p.Choices, HorizontalAlignment = HorizontalAlignment.Stretch };
                cb.SelectedItem = p.Choices!.FirstOrDefault(c => c.Equals(values[p.Name], StringComparison.OrdinalIgnoreCase)) ?? p.Default;
                cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) { values[p.Name] = s; Summary(); } };
                input = cb;
            }
            else if (p.IsToggle)
            {
                var on = Expr.TryEval(values[p.Name], null, out var b, out _) && Expr.Truthy(b);
                var chk = new CheckBox { IsChecked = on, Content = p.Description ?? "" };
                chk.IsCheckedChanged += (_, _) => { values[p.Name] = chk.IsChecked == true ? "1" : "0"; Summary(); };
                input = chk;
            }
            else
            {
                var tb = new TextBox { Text = values[p.Name] };
                if (p.Min != null || p.Max != null) tb.Watermark = $"{(p.Min is { } lo ? Expr.Format(lo) : "")} to {(p.Max is { } hi ? Expr.Format(hi) : "")}";
                var err = new TextBlock { FontSize = TypeScale.Small, Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap };
                errors[p.Name] = err;
                tb.TextChanged += (_, _) =>
                {
                    values[p.Name] = tb.Text?.Trim() ?? "";
                    err.Text = part.Check(p.Name, values[p.Name]) ?? "";
                    if (err.Text.Length == 0) Summary();
                };
                Grid.SetRow(err, 2 * i + 1); Grid.SetColumn(err, 2);
                grid.Children.Add(err);
                input = tb;
            }
            if (p.Description != null && !p.IsToggle) ToolTip.SetTip(input, p.Description);
            input.Margin = new Thickness(0, 3);
            Grid.SetRow(label, 2 * i); Grid.SetRow(input, 2 * i); Grid.SetColumn(input, 2);
            grid.Children.Add(label); grid.Children.Add(input);
        }
        Summary();
        (Dictionary<string, string>, bool)? result = null;
        var all = new CheckBox { Content = allText ?? "", IsVisible = allText != null };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var sp = new StackPanel { Spacing = 8 };
        if (note != null) sp.Children.Add(new TextBlock { Text = note, FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(grid);
        sp.Children.Add(summary);
        sp.Children.Add(all);
        var w = MakeWindow(title, WithButtons(sp, null, ok, cancel), 400);
        ok.Click += (_, _) =>
        {
            foreach (var p in ps)
                if (part.Check(p.Name, values[p.Name]) is { } e) { if (errors.TryGetValue(p.Name, out var tb)) tb.Text = e; return; }
            result = (values, all.IsChecked == true);
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Configure for the part open: its own values (the defaults assemblies start from) set.</summary>
    public static async Task ConfigurePart(MainWindow owner)
    {
        if (owner.PartOnly("Configure", "for an assembly, right-click its top row in the design tree; for a component, select it and click Configure")) return;
        var session = owner.Session;
        if (session.Doc.UserParameters().Count == 0)
        {
            await Message(owner, "Configure", "This part has no user parameters. Mark variables as User in Equations, or add a design table (Tools > Design Table).");
            return;
        }
        var chosen = await Configure(owner, session.Doc, null, "Configure",
            "Default values for the part, also used by new instances in assemblies.");
        if (chosen is not { } c) return;
        var values = c.values;
        if (session.Doc.Normalize(values) != null) session.Change(() => session.Doc.Apply(values));
    }

    /// <summary>Tools > Options: System Options and Document Properties (UI/OptionsDialog.cs).</summary>
    public static Task Options(MainWindow owner) => OptionsDialog.Show(owner);

    /// <summary>Help > Keyboard Shortcuts (UI/ShortcutsDialog.cs).</summary>
    public static Task Shortcuts(Window owner) => ShortcutsDialog.Show(owner);

    const string ProjectUrl = "https://github.com/SpryCAD/SpryCAD";

    /// <summary>
    /// About SpryCAD: the app's mark and name, its version (with a button copying it and the system for a bug report),
    /// what it reads and writes, and links to the project and its license.
    /// </summary>
    public static async Task About(Window owner)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        // from git at build time (SpryCadGitVersion in the .csproj): "0.0.1-dev.3+aeb382f[.dirty]"
        var info = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var (ver, build) = plus < 0 ? (info, "") : (info[..plus], info[(plus + 1)..]);

        // the mark (the app icon's largest frame) beside the name and what it is
        Control mark;
        try { mark = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SpryCAD/Assets/SpryCAD.ico"))), Width = 56, Height = 56 }; }
        catch { mark = new Control { Width = 56, Height = 56 }; }
        mark.VerticalAlignment = VerticalAlignment.Top;
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "SpryCAD", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Palette.Brand },
            new TextBlock { Text = "Fast, lightweight, open source parametric 3D CAD.", TextWrapping = TextWrapping.Wrap },
        } };
        var head = new DockPanel { Children = { mark, title } };
        DockPanel.SetDock(mark, Dock.Left);
        title.Margin = new Thickness(14, 0, 0, 0);

        // the version, and a copy of it with the system for a bug report
        var verText = new SelectableTextBlock
        {
            Text = $"Version {ver}" + (build.Length > 0 ? $"  (commit {build.Replace(".dirty", ", uncommitted changes")})" : ""),
            Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(8, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(copy, "Copies version and system details for bug reports.");
        copy.Click += async (_, _) =>
        {
            var details = $"SpryCAD {info}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})\n.NET {Environment.Version}";
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } cb) { await cb.SetTextAsync(details); copy.Content = "Copied"; }
        };
        var verRow = new DockPanel { Children = { copy, verText } };
        DockPanel.SetDock(copy, Dock.Right);

        TextBlock Muted(string text) => new() { Text = text, Foreground = Palette.TextMuted, FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap };
        Button Link(string text, string url)
        {
            var b = new HyperlinkButton { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Foreground = Palette.Accent };
            ToolTip.SetTip(b, url);
            return b;
        }
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children =
        {
            Link("github.com/SpryCAD/SpryCAD", ProjectUrl),
            Link("MIT License", ProjectUrl + "/blob/master/LICENSE"),
        } };

        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(head);
        sp.Children.Add(new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 4) });
        sp.Children.Add(verRow);
        sp.Children.Add(new StackPanel { Spacing = 3, Children =
        {
            Muted("Parts (.sprt) and assemblies (.sasm). Imports STEP; exports STEP AP214, STL and flat patterns as DXF."),
            Muted("Native B-rep modeling kernel written in C#. Built with Avalonia, Silk.NET (OpenGL) and .NET."),
        } });
        sp.Children.Add(links);
        var w = MakeWindow("About SpryCAD", WithButtons(sp, null, ok), 460);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }
}
