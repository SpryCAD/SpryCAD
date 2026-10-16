using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.App.UI;

/// <summary>
/// The part's equations (global variables) in a table: name, value or equation, what it works out to, and for user
/// variables (shown in Configure) a description and limits. Cells are edited in place; Ctrl+C copies the selected rows
/// as tab-separated text, as a spreadsheet pastes it, and Ctrl+V pastes rows from one (name, equation, then optionally
/// user, description, min, max; a heading row is skipped; a name already there is changed, a new one added). Each
/// change goes into the part as it is made (a cell's edit ended with Enter, a row added, pasted or deleted): one undo step each.
/// </summary>
static class EquationsDialog
{
    /// <summary>A row of the table: an equation being edited, and what it works out to.</summary>
    sealed class Row : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        readonly Action _changed;
        public Row(Equation e, Action changed)
        {
            _changed = changed;
            _name = e.Name; _expr = e.Expr; _exposed = e.Exposed; _description = e.Description ?? "";
            _min = e.Min is { } a ? Expr.Format(a) : ""; _max = e.Max is { } b ? Expr.Format(b) : "";
        }
        string _name, _expr, _description, _min, _max;
        bool _exposed;
        void Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            _changed();
        }
        public string Name { get => _name; set => Set(ref _name, value?.Trim() ?? "", nameof(Name)); }
        public string Equation { get => _expr; set => Set(ref _expr, string.IsNullOrWhiteSpace(value) ? "0" : value.Trim(), nameof(Equation)); }
        public bool User { get => _exposed; set => Set(ref _exposed, value, nameof(User)); }
        public string Description { get => _description; set => Set(ref _description, value ?? "", nameof(Description)); }
        public string Min { get => _min; set => Set(ref _min, value?.Trim() ?? "", nameof(Min)); }
        public string Max { get => _max; set => Set(ref _max, value?.Trim() ?? "", nameof(Max)); }

        // what it works out to, or why it does not (red)
        public string Value { get; private set; } = "";
        // (the text's own color unless red: a brush left empty would draw nothing)
        public IBrush ValueBrush { get; private set; } = Palette.Text;
        public string? Problem { get; private set; }
        public void Show(string value, string? problem)
        {
            (Value, Problem, ValueBrush) = (problem ?? value, problem, problem != null ? Palette.Danger : Palette.Text);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Problem)));
        }

        static double? Number(string text) => Expr.TryEval(text, null, out var v, out _) ? v : null;
        public Equation ToEquation() => new()
        {
            Name = Name, Expr = Equation, Exposed = User,
            // (a description and limits only for a user variable: Configure is all they are for)
            Description = User && Description.Trim().Length > 0 ? Description.Trim() : null,
            Min = User ? Number(Min) : null, Max = User ? Number(Max) : null,
        };
    }

    public static async Task Show(MainWindow owner)
    {
        if (owner.PartOnly("Equations")) return;
        var session = owner.Session;
        var tables = session.Doc.Tables;
        var units = session.Doc.Units;
        var items = new System.Collections.ObjectModel.ObservableCollection<Row>();
        // variables the design tables set (read only here)
        var fromTables = (tables ?? new()).SelectMany(t => new[] { t.Name }.Concat(t.Columns)).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();

        void Recompute()
        {
            var eqs = items.Select(r => new Equation { Name = r.Name, Expr = r.Equation }).ToList();
            var (vals, errs) = Regenerator.EvaluateEquations(new CadDocument { Equations = eqs, Tables = tables, Units = units });
            foreach (var r in items)
            {
                var name = r.Name.Trim();
                // (one set twice has a value, from where it was set first: the second is the error)
                var problem = name.Length == 0 ? "Give it a name"
                    : EquationEntry.NameProblem(name)
                    ?? (items.Count(x => x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) > 1 || fromTables.Contains(name, StringComparer.OrdinalIgnoreCase)
                        ? $"{name} is set twice" : errs.GetValueOrDefault(r.Name));
                r.Show(vals.TryGetValue(name, out var v) ? units.Show(v) : "", problem);
            }
            Apply();
        }
        // into the part as made, when it differs from what the part has (one undo step)
        static string KeyOf(IEnumerable<Equation> eqs) => string.Join("\n", eqs.Select(e => $"{e.Name}\t{e.Expr}\t{e.Exposed}\t{e.Description}\t{e.Min}\t{e.Max}"));
        bool loaded = false;
        void Apply()
        {
            if (!loaded) return;
            var result = items.Where(r => r.Name.Trim().Length > 0).Select(r => r.ToEquation()).ToList();
            if (KeyOf(result) == KeyOf(session.Doc.Equations)) return;
            session.Change(() => session.Doc.Equations = result);
        }
        Row Make(Equation e) => new(e, Recompute);
        foreach (var e in session.Doc.Equations) items.Add(Make(e));

        var dg = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, CanUserSortColumns = true, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.All, HeadersVisibility = DataGridHeadersVisibility.All,
            BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Height = 360,
        };
        Control Header(string text, string tip) { var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold }; ToolTip.SetTip(t, tip); return t; }
        void Text(string header, string tip, string property, DataGridLength width) =>
            dg.Columns.Add(new DataGridTextColumn { Header = Header(header, tip), Binding = new Binding(property) { Mode = BindingMode.TwoWay }, Width = width });
        Text("Name", "The variable's name, used in any value (W, Teeth).", nameof(Row.Name), new DataGridLength(120));
        Text("Value / Equation", "A value (10mm, 2in, 30deg, 6) or an equation of other variables (W/2, sqrt(A^2 + B^2), if(N < 30, 5mm, 8mm)). A value without a unit is read in the part's units.",
            nameof(Row.Equation), new DataGridLength(1, DataGridLengthUnitType.Star));
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("Evaluates To", "What it works out to, in the part's units; why not, in red."), Width = new DataGridLength(130), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((_, _) => new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                [!TextBlock.TextProperty] = new Binding(nameof(Row.Value)),
                [!TextBlock.ForegroundProperty] = new Binding(nameof(Row.ValueBrush)),
                [!ToolTip.TipProperty] = new Binding(nameof(Row.Problem)),
            }),
        });
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("User", "Shown in Configure, where an assembly sets it for each instance."), Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader),
            CellTemplate = new FuncDataTemplate<Row>((_, _) => new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0, Padding = new Thickness(0),
                [!ToggleButton.IsCheckedProperty] = new Binding(nameof(Row.User)) { Mode = BindingMode.TwoWay },
            }),
        });
        Text("Description", "A user variable's label in Configure.", nameof(Row.Description), new DataGridLength(150));
        Text("Min", "The smallest value Configure accepts. Min 0 and Max 1 make a checkbox.", nameof(Row.Min), new DataGridLength(60));
        Text("Max", "The largest value Configure accepts.", nameof(Row.Max), new DataGridLength(60));
        // (a cell's text centred up and down while it is edited, where it was shown)
        dg.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<DataGridCell>().Descendant().OfType<TextBox>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Avalonia.Styling.Setter(Layoutable.MinHeightProperty, 0.0),
            },
        });
        var editing = false;
        dg.BeginningEdit += (_, _) => editing = true;
        dg.CellEditEnded += (_, _) => editing = false;

        List<Row> Selected() => dg.SelectedItems.OfType<Row>().OrderBy(items.IndexOf).ToList();
        void DeleteRows() { foreach (var r in Selected()) items.Remove(r); Recompute(); }
        async Task Copy()
        {
            var sel = Selected();
            if (sel.Count == 0) sel = items.ToList();
            if (sel.Count == 0 || TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip) return;
            static string Cell(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
            var lines = new List<string> { "Name\tValue / Equation\tEvaluates To\tUser\tDescription\tMin\tMax" };
            lines.AddRange(sel.Select(r => string.Join("\t", Cell(r.Name), Cell(r.Equation), Cell(r.Value), r.User ? "TRUE" : "FALSE", Cell(r.Description), r.Min, r.Max)));
            await clip.SetTextAsync(string.Join("\r\n", lines));
            owner.Status($"Copied {sel.Count} variable{(sel.Count == 1 ? "" : "s")}.");
        }
        async Task Paste()
        {
            if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip || await clip.GetTextAsync() is not { Length: > 0 } text) return;
            int n = 0;
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                var c = line.Split('\t');
                var name = c[0].Trim();
                if (name.Length == 0 || name.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue; // (a blank line, the heading)
                var expr = c.Length > 1 && c[1].Trim().Length > 0 ? c[1].Trim() : "0";
                var row = items.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (row == null) items.Add(row = Make(new Equation { Name = name, Expr = expr }));
                else row.Equation = expr;
                // (as copied here: Evaluates To third, then User, Description, Min, Max)
                if (c.Length > 3) row.User = c[3].Trim() is var u && (u.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || u == "1" || u.Equals("yes", StringComparison.OrdinalIgnoreCase) || u.Equals("x", StringComparison.OrdinalIgnoreCase));
                if (c.Length > 4) row.Description = c[4].Trim();
                if (c.Length > 5) row.Min = c[5].Trim();
                if (c.Length > 6) row.Max = c[6].Trim();
                n++;
            }
            Recompute();
            owner.Status($"Pasted {n} variable{(n == 1 ? "" : "s")}.");
        }
        dg.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (editing) return;
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; DeleteRows(); }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await Copy(); }
            else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await Paste(); }
        }, RoutingStrategies.Tunnel);
        MenuItem Item(string text, string? gesture, Action a)
        {
            var m = new MenuItem { Header = text };
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            m.Click += (_, _) => a();
            return m;
        }
        dg.ContextMenu = new ContextMenu
        {
            Items =
            {
                Item("Copy", "Ctrl+C", () => _ = Copy()),
                Item("Paste", "Ctrl+V", () => _ = Paste()),
                new Separator(),
                Item("Delete Rows", "Delete", DeleteRows),
            },
        };
        Recompute();
        loaded = true;

        var add = new Button { Content = "+ Variable" };
        add.Click += (_, _) =>
        {
            int k = items.Count + 1;
            while (items.Any(r => r.Name.Equals("Var" + k, StringComparison.OrdinalIgnoreCase))) k++;
            var r = Make(new Equation { Name = "Var" + k, Expr = units == UnitSystem.IPS ? "1in" : "10mm" });
            items.Add(r);
            Recompute();
            dg.SelectedItem = r;
            dg.ScrollIntoView(r, dg.Columns[0]);
        };
        var info = new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap,
            Text = "Variables for any dimension or feature value. Typing L = 10 in a value field adds one here. Changes apply as made (Ctrl+Z in the window undoes them). Ctrl+C copies the rows for a spreadsheet; Ctrl+V pastes them back (name, then value).",
        };
        var tableInfo = new TextBlock
        {
            FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap, IsVisible = fromTables.Count > 0,
            Text = "Set by design tables (Tools > Design Table): " + string.Join(", ", fromTables) + ".",
        };
        var close = new Button { Content = "Close", IsCancel = true };
        var sp = new StackPanel { Spacing = 8, Children = { info, tableInfo, dg, add } };
        var w = Dialogs.MakeWindow("Equations", Dialogs.WithButtons(sp, null, close), 900);
        close.Click += (_, _) => { dg.CommitEdit(); w.Close(); };
        // (a cell being edited when the window is closed otherwise: its edit kept)
        w.Closing += (_, _) => dg.CommitEdit();
        await w.ShowDialog(owner);
    }
}
