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
            Content = new Border { Padding = new Thickness(14), Child = content },
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
        var rows = session.Doc.Equations.Select(Serialization.Clone).ToList();
        var tables = session.Doc.Tables;
        var list = new StackPanel { Spacing = 3 };
        var info = new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap,
            Text = "Global variables usable in any dimension or feature value, e.g. W = 100, H = W/2. Trig functions use degrees; units like 1in are converted to mm. "
                + "Conditions (Teeth < 30 and not Slots) give 1 or 0, for Suppress When. "
                + "User-facing variables are what Configure shows (an assembly sets them per instance); the others are internal.",
        };
        // variables the design tables set (read only here)
        var fromTables = (tables ?? new()).SelectMany(t => new[] { t.Name }.Concat(t.Columns)).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        var tableInfo = new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap, IsVisible = fromTables.Count > 0,
            Text = "Set by design tables (Tools > Design Table): " + string.Join(", ", fromTables) + ".",
        };
        const string cols = "120,*,90,44,150,56,56,30";
        var valueCells = new List<(Equation row, TextBlock cell)>();
        (Dictionary<string, double>, Dictionary<string, string>) Evaluate() => Regenerator.EvaluateEquations(new CadDocument { Equations = rows, Tables = tables });
        void Show(TextBlock cell, Equation r, Dictionary<string, double> vals, Dictionary<string, string> errs)
        {
            var name = r.Name.Trim();
            // (an error of this row: one set twice has a value, from where it was set first)
            var err = rows.Count(x => x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) > 1 || fromTables.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"{name} is set twice" : errs.GetValueOrDefault(r.Name);
            cell.Text = err ?? (vals.TryGetValue(name, out var val) ? Expr.Format(val) : "");
            if (err != null) cell.Foreground = Palette.Danger; else cell.ClearValue(TextBlock.ForegroundProperty);
            ToolTip.SetTip(cell, err);
        }
        // re-evaluate in place (rebuilding the rows would steal focus while tabbing between fields)
        void UpdateValues()
        {
            var (vals, errs) = Evaluate();
            foreach (var (r, cell) in valueCells) Show(cell, r, vals, errs);
        }
        static double? Number(string? text) => Expr.TryEval(text, null, out var v, out _) ? v : null;
        TextBlock Head(string text, int col, string? tip = null)
        {
            var h = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
            if (tip != null) ToolTip.SetTip(h, tip);
            Grid.SetColumn(h, col);
            return h;
        }
        void Refresh()
        {
            list.Children.Clear();
            valueCells.Clear();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
            header.Children.Add(Head("Name", 0));
            header.Children.Add(Head("Value / Equation", 1));
            header.Children.Add(Head("Evaluates to", 2));
            header.Children.Add(Head("User", 3, "User-facing: shown in Configure, set per instance in assemblies"));
            header.Children.Add(Head("Description", 4, "What Configure shows for it"));
            header.Children.Add(Head("Min", 5, "Smallest value Configure accepts (blank: none); Min 0 and Max 1 make a checkbox"));
            header.Children.Add(Head("Max", 6, "Largest value Configure accepts (blank: none)"));
            list.Children.Add(header);
            var (vals, errs) = Evaluate();
            foreach (var r in rows)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
                var m = new Thickness(0, 0, 4, 0);
                var n = new TextBox { Text = r.Name, Margin = m };
                var e = new TextBox { Text = r.Expr, Margin = m };
                var v = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = m };
                var user = new CheckBox { IsChecked = r.Exposed, HorizontalAlignment = HorizontalAlignment.Center };
                var desc = new TextBox { Text = r.Description ?? "", Margin = m, IsEnabled = r.Exposed };
                var lo = new TextBox { Text = r.Min is { } a ? Expr.Format(a) : "", Margin = m, IsEnabled = r.Exposed };
                var hi = new TextBox { Text = r.Max is { } b ? Expr.Format(b) : "", Margin = m, IsEnabled = r.Exposed };
                var del = DeleteButton("Delete this variable");
                ToolTip.SetTip(user, "User-facing: shown in Configure");
                var rr = r;
                valueCells.Add((rr, v));
                Show(v, rr, vals, errs);
                n.TextChanged += (_, _) => { rr.Name = n.Text?.Trim() ?? ""; UpdateValues(); };
                e.TextChanged += (_, _) => { rr.Expr = e.Text?.Trim() ?? "0"; UpdateValues(); };
                user.IsCheckedChanged += (_, _) => { rr.Exposed = user.IsChecked == true; desc.IsEnabled = lo.IsEnabled = hi.IsEnabled = rr.Exposed; };
                desc.TextChanged += (_, _) => rr.Description = string.IsNullOrWhiteSpace(desc.Text) ? null : desc.Text.Trim();
                lo.TextChanged += (_, _) => rr.Min = Number(lo.Text);
                hi.TextChanged += (_, _) => rr.Max = Number(hi.Text);
                del.Click += (_, _) => { rows.Remove(rr); Refresh(); };
                Control[] cells = { n, e, v, user, desc, lo, hi, del };
                for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
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
        sp.Children.Add(tableInfo);
        sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420 });
        sp.Children.Add(add);
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow("Equations, Global Variables", sp, 900);
        ok.Click += (_, _) =>
        {
            w.Close();
            foreach (var r in rows.Where(r => !r.Exposed)) { r.Description = null; r.Min = r.Max = null; }
            session.Change(() => session.Doc.Equations = rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList());
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

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
            if (text.Length == 0) { now.Text = "No condition: the feature is built unless suppressed by hand."; now.Foreground = Palette.TextMuted; return; }
            if (!Expr.TryEval(text, lookup, out var v, out var err)) { now.Text = err; now.Foreground = Palette.Danger; return; }
            now.Text = Expr.Truthy(v) ? "Holds now: the feature is suppressed (and the features using it)." : "Does not hold now: the feature is built.";
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
            Text = "Compare with < <= > >= == != and combine with and, or, not. "
                + (names.Count > 0 ? "Variables: " + string.Join(", ", names) + "." : "Define variables in Equations first."),
        });
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow("Suppress When", sp, 420);
        ok.Click += (_, _) =>
        {
            var text = tb.Text?.Trim() ?? "";
            if (text.Length > 0 && !Expr.TryEval(text, lookup, out _, out _)) return; // (the error shows under the box)
            w.Close();
            var value = text.Length == 0 ? null : text;
            if (value != f.SuppressWhen) session.Change(() => f.SuppressWhen = value);
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
    }

    /// <summary>
    /// The part's design tables: for each, its name (the choice, and the variable holding the row number), whether
    /// Configure shows it, its columns (the variables it sets) and rows (a key and a value per column), and the row in
    /// use. Rows can be pasted from a spreadsheet (a header row of column names, the keys in the first column).
    /// </summary>
    public static async Task DesignTables(MainWindow owner)
    {
        var session = owner.Session;
        var tables = (session.Doc.Tables ?? new()).Select(Serialization.Clone).ToList();
        int current = 0;
        var picker = new ComboBox { MinWidth = 160 };
        var addTable = new Button { Content = "+ Table" };
        var delTable = new Button { Content = "Delete Table" };
        var body = new StackPanel { Spacing = 6 };
        var error = new TextBlock { Foreground = Palette.Danger, FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap };
        bool picking = false;

        static DesignTable NewTable(int n) => new()
        {
            Name = n == 1 ? "Size" : "Table" + n, Columns = { "D" },
            Rows = { new DesignRow { Key = "A", Values = { "10" } }, new DesignRow { Key = "B", Values = { "20" } } },
        };

        void Pick()
        {
            picking = true;
            picker.ItemsSource = tables.Select(t => string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name).ToList();
            picker.SelectedIndex = tables.Count == 0 ? -1 : Math.Clamp(current, 0, tables.Count - 1);
            picking = false;
            picker.IsEnabled = delTable.IsEnabled = tables.Count > 0;
            Refresh();
        }
        picker.SelectionChanged += (_, _) => { if (!picking && picker.SelectedIndex >= 0) { current = picker.SelectedIndex; Refresh(); } };
        addTable.Click += (_, _) => { tables.Add(NewTable(tables.Count + 1)); current = tables.Count - 1; Pick(); };
        delTable.Click += (_, _) => { if (tables.Count > 0) { tables.RemoveAt(current); current = Math.Max(0, current - 1); Pick(); } };

        void Refresh()
        {
            body.Children.Clear();
            if (tables.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted,
                    Text = "No design tables. A table gives rows of values for a set of variables, e.g. a screw's sizes M3, M4, M5 with D, Pitch and HeadD for each: "
                        + "choosing a row sets them all. Add one with + Table, or paste a spreadsheet.",
                });
                return;
            }
            var t = tables[current];
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,140,16,Auto,16,Auto,8,*") };
            var name = new TextBox { Text = t.Name };
            var user = new CheckBox { Content = "User-facing", IsChecked = t.Exposed };
            var desc = new TextBox { Text = t.Description ?? "", Watermark = "Description for Configure" };
            ToolTip.SetTip(name, "The choice, and a variable holding the number of the row in use (1 = the first), for conditions");
            ToolTip.SetTip(user, "Shown in Configure as a list of the rows (an assembly chooses one per instance)");
            name.TextChanged += (_, _) => { t.Name = name.Text?.Trim() ?? ""; };
            name.LostFocus += (_, _) => Pick();
            user.IsCheckedChanged += (_, _) => t.Exposed = user.IsChecked == true;
            desc.TextChanged += (_, _) => t.Description = string.IsNullOrWhiteSpace(desc.Text) ? null : desc.Text.Trim();
            var nl = new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center };
            var dl = new TextBlock { Text = "Description", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 2); Grid.SetColumn(user, 4); Grid.SetColumn(dl, 6); Grid.SetColumn(desc, 8);
            top.Children.Add(nl); top.Children.Add(name); top.Children.Add(user); top.Children.Add(dl); top.Children.Add(desc);
            body.Children.Add(top);

            // the grid: a key column, a column per variable, the row in use, delete
            var defs = "110," + string.Join(",", t.Columns.Select(_ => "90")) + ",60,30";
            var grid = new StackPanel { Spacing = 3 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions(defs) };
            head.Children.Add(new TextBlock { Text = "Row", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            for (int c = 0; c < t.Columns.Count; c++)
            {
                int cc = c;
                var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 4, 0) };
                var cn = new TextBox { Text = t.Columns[c], FontWeight = FontWeight.SemiBold };
                ToolTip.SetTip(cn, "The variable this column sets");
                cn.TextChanged += (_, _) => t.Columns[cc] = cn.Text?.Trim() ?? "";
                var dc = DeleteButton("Delete this column");
                dc.Click += (_, _) => { t.Columns.RemoveAt(cc); foreach (var r in t.Rows) if (cc < r.Values.Count) r.Values.RemoveAt(cc); Refresh(); };
                Grid.SetColumn(dc, 1);
                cell.Children.Add(cn); cell.Children.Add(dc);
                Grid.SetColumn(cell, c + 1); head.Children.Add(cell);
            }
            var inUse = new TextBlock { Text = "In use", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(inUse, "The row the part uses when nothing else is chosen");
            Grid.SetColumn(inUse, t.Columns.Count + 1); head.Children.Add(inUse);
            grid.Children.Add(head);
            var selected = t.Current;
            var group = "dt" + Guid.NewGuid().ToString("N");
            foreach (var r in t.Rows)
            {
                var rr = r;
                while (rr.Values.Count < t.Columns.Count) rr.Values.Add("0");
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions(defs) };
                var key = new TextBox { Text = rr.Key, Margin = new Thickness(0, 0, 4, 0) };
                key.TextChanged += (_, _) => { var was = rr.Key; rr.Key = key.Text?.Trim() ?? ""; if (t.Selected == was) t.Selected = rr.Key; };
                g.Children.Add(key);
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    int cc = c;
                    var v = new TextBox { Text = rr.Values[c], Margin = new Thickness(0, 0, 4, 0) };
                    v.TextChanged += (_, _) => rr.Values[cc] = v.Text?.Trim() ?? "0";
                    Grid.SetColumn(v, c + 1); g.Children.Add(v);
                }
                var use = new RadioButton { GroupName = group, IsChecked = ReferenceEquals(rr, selected), HorizontalAlignment = HorizontalAlignment.Center };
                use.IsCheckedChanged += (_, _) => { if (use.IsChecked == true) t.Selected = rr.Key; };
                var del = DeleteButton("Delete this row");
                del.Click += (_, _) => { t.Rows.Remove(rr); Refresh(); };
                Grid.SetColumn(use, t.Columns.Count + 1); Grid.SetColumn(del, t.Columns.Count + 2);
                g.Children.Add(use); g.Children.Add(del);
                grid.Children.Add(g);
            }
            body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 380, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

            var addRow = new Button { Content = "+ Row" };
            addRow.Click += (_, _) =>
            {
                var last = t.Rows.LastOrDefault();
                t.Rows.Add(new DesignRow { Key = "Row" + (t.Rows.Count + 1), Values = last?.Values.ToList() ?? t.Columns.Select(_ => "0").ToList() });
                Refresh();
            };
            var addCol = new Button { Content = "+ Column" };
            addCol.Click += (_, _) =>
            {
                int n = t.Columns.Count + 1;
                while (t.Columns.Contains("Var" + n)) n++;
                t.Columns.Add("Var" + n);
                foreach (var r in t.Rows) r.Values.Add("0");
                Refresh();
            };
            var paste = new Button { Content = "Paste from Spreadsheet" };
            ToolTip.SetTip(paste, "Replaces the table with the copied cells: a header row (the first cell anything, then the variables), then a row per configuration (its key, then the values)");
            paste.Click += async (_, _) =>
            {
                var text = owner.Clipboard is { } clip ? await clip.GetTextAsync() : null;
                if (DesignTable.Parse(text) is not { } pasted) { error.Text = "The clipboard has no table: copy a header row and rows of cells from a spreadsheet."; return; }
                t.Columns = pasted.Columns; t.Rows = pasted.Rows;
                if (t.Row(t.Selected ?? "") == null) t.Selected = null;
                error.Text = "";
                Refresh();
            };
            var copy = new Button { Content = "Copy" };
            ToolTip.SetTip(copy, "Copies the table as tab-separated cells, to edit in a spreadsheet");
            copy.Click += (_, _) =>
            {
                var lines = new List<string> { string.Join("\t", new[] { t.Name }.Concat(t.Columns)) };
                lines.AddRange(t.Rows.Select(r => string.Join("\t", new[] { r.Key }.Concat(t.Columns.Select((_, c) => r.ValueAt(c))))));
                _ = owner.Clipboard?.SetTextAsync(string.Join("\n", lines));
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            bar.Children.Add(addRow); bar.Children.Add(addCol); bar.Children.Add(paste); bar.Children.Add(copy);
            body.Children.Add(bar);
        }

        string? Problem()
        {
            var names = new HashSet<string>(session.Doc.Equations.Select(e => e.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                if (!IsName(t.Name)) return $"'{t.Name}' is not a valid table name (letters, digits and _, starting with a letter).";
                if (!names.Add(t.Name)) return $"{t.Name} is already a variable.";
                foreach (var c in t.Columns)
                {
                    if (!IsName(c)) return $"{t.Name}: '{c}' is not a valid variable name.";
                    if (!names.Add(c)) return $"{t.Name}: {c} is already a variable (in Equations or another table).";
                }
                if (t.Rows.Count == 0) return $"{t.Name} has no rows.";
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in t.Rows)
                    if (r.Key.Length == 0 || !keys.Add(r.Key)) return $"{t.Name}: each row needs a key of its own ('{r.Key}').";
            }
            return null;
        }

        Pick();
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock { Text = "Table", VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(picker); head.Children.Add(addTable); head.Children.Add(delTable);
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap,
            Text = "Rows of values for a set of variables; the row in use sets them all. Values may be expressions of the part's other variables. "
                + "The table's name holds the number of the row in use (1 = the first), for conditions such as Size >= 3.",
        });
        sp.Children.Add(head);
        sp.Children.Add(body);
        sp.Children.Add(error);
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow("Design Tables", sp, 760);
        ok.Click += (_, _) =>
        {
            foreach (var t in tables) { t.Name = t.Name.Trim(); t.Description = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description; }
            if (Problem() is { } p) { error.Text = p; return; }
            w.Close();
            var json = System.Text.Json.JsonSerializer.Serialize(tables, Serialization.Options);
            if (json != System.Text.Json.JsonSerializer.Serialize(session.Doc.Tables ?? new(), Serialization.Options))
                session.Change(() => session.Doc.Tables = tables.Count == 0 ? null : tables);
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    static bool IsName(string? s) => !string.IsNullOrEmpty(s) && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_') && !Expr.IsKeyword(s);

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
            var shown = vals.Where(kv => !ps.Any(p => p.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))).Take(12).Select(kv => $"{kv.Key} {Expr.Format(kv.Value)}");
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
        sp.Children.Add(Buttons(ok, cancel));
        var w = MakeWindow(title, sp, 400);
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
        var session = owner.Session;
        if (session.Doc.UserParameters().Count == 0)
        {
            await Message(owner, "Configure", "This part has no user-facing parameters yet. Mark variables as User in Equations, or add a design table (Tools > Design Table).");
            return;
        }
        var chosen = await Configure(owner, session.Doc, null, "Configure",
            "The part's own values: what it is built with here, and what each new instance in an assembly starts from.");
        if (chosen is not { } c) return;
        var values = c.values;
        if (session.Doc.Normalize(values) != null) session.Change(() => session.Doc.Apply(values));
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
        sp.Children.Add(new TextBlock { Text = "In IPS, values are inches; STEP/STL exports are converted to millimetres.", FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Part color (R, G, B 0-255)", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(color);
        sp.Children.Add(new TextBlock { Text = "Sheet metal bend allowance", FontWeight = FontWeight.SemiBold });
        var bendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto,4,70") };
        bendRow.Children.Add(bendMethod);
        var kLabel = new TextBlock { Text = "K-factor", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(kLabel, 2); bendRow.Children.Add(kLabel);
        Grid.SetColumn(kFactor, 4); bendRow.Children.Add(kFactor);
        sp.Children.Add(bendRow);
        sp.Children.Add(new TextBlock { Text = "For this part's flat patterns (a Flat Pattern can override it). K-factor 0 to 1, or an expression.", FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "View", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(wheel); sp.Children.Add(persp);
        sp.Children.Add(pivot);
        sp.Children.Add(new TextBlock { Text = "Middle-click a vertex, edge or face to rotate about that spot instead, until you middle-click empty space.", FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Files", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(container);
        sp.Children.Add(new TextBlock { Text = "Otherwise parts are saved as plain JSON, unless they embed other files (e.g. an imported model). Either kind opens normally.", FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
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
        sp.Children.Add(new TextBlock { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = TypeScale.Body });
        sp.Children.Add(Buttons(ok));
        var w = MakeWindow("Keyboard & Mouse", sp, 520);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task About(Window owner)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = "SpryCAD", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Palette.Brand });
        // from git at build time (SpryCadGitVersion in the .csproj): "0.0.1-dev.3+aeb382f[.dirty]"
        var info = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var (ver, build) = plus < 0 ? (info, "") : (info[..plus], info[(plus + 1)..]);
        var verText = new SelectableTextBlock { Text = $"Version {ver}" + (build.Length > 0 ? $"   (commit {build.Replace(".dirty", ", uncommitted changes")})" : ""), Foreground = Palette.TextMuted };
        ToolTip.SetTip(verText, info);
        sp.Children.Add(verText);
        sp.Children.Add(new TextBlock { Text = "Fast, lightweight, open source parametric 3D CAD.", TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Own plane-based B-rep kernel in C#, Avalonia UI, OpenGL viewport.\nNative formats: .sprt parts (JSON), .sasm assemblies. Export: STEP AP214, STL.", TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted });
        sp.Children.Add(new TextBlock { Text = "License: MIT", Foreground = Palette.TextMuted });
        sp.Children.Add(Buttons(ok));
        var w = MakeWindow("About SpryCAD", sp, 420);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }
}
