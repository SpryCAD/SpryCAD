using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// The part's parameters in one window, as Inventor's: a list on the left, Equations always first, then the design
/// tables (added and removed under the list), and beside it the one picked. Equations: global variables (name, value or
/// equation, what it works out to, and for user variables shown in Configure a description and limits; those the design
/// tables set listed too, greyed, "from table Size"). A design table: its name, its columns the variables it sets, a row
/// per configuration, the row in use. Every grid copies to and pastes from a spreadsheet (Ctrl+C, Ctrl+V: tab-separated,
/// a heading row). Each change goes into the part as it is made, one undo step each.
/// </summary>
static class ParametersDialog
{
    /// <summary>
    /// Opens on the equations, or (<paramref name="tables"/>) the first design table; or on what <paramref name="select"/>
    /// names: a table (or one of its columns' variables) shown, an equation selected.
    /// </summary>
    public static async Task Show(MainWindow owner, bool tables = false, string? select = null)
    {
        if (owner.PartOnly("Parameters")) return;
        var equations = Equations(owner);
        var doc = owner.Session.Doc;
        var table = select == null ? null : (doc.Tables ?? new()).FirstOrDefault(t => t.Name.Equals(select, StringComparison.OrdinalIgnoreCase)
            || t.Columns.Any(c => c.Equals(select, StringComparison.OrdinalIgnoreCase)));
        var startTable = table?.Name ?? (tables && select == null ? doc.Tables?.FirstOrDefault()?.Name : null);
        var body = Body(owner, (equations.view, equations.refresh), startTable);
        if (select != null && table == null) Avalonia.Threading.Dispatcher.UIThread.Post(() => equations.select(select), Avalonia.Threading.DispatcherPriority.Loaded);
        var close = new Button { Content = "Close", IsCancel = true };
        var w = Dialogs.MakeWindow("Parameters", Dialogs.WithButtons(body, null, close), 1000);
        close.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    // ---- shared ----------------------------------------------------------------------------------------------------

    static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, CanUserSortColumns = true, CanUserReorderColumns = false, CanUserResizeColumns = true,
        SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.All, HeadersVisibility = DataGridHeadersVisibility.All,
        BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Height = 340,
        Styles =
        {
            // (a cell's text centred up and down while it is edited, where it was shown)
            new Style(x => x.OfType<DataGridCell>().Descendant().OfType<TextBox>())
            {
                Setters = { new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center), new Setter(Layoutable.MinHeightProperty, 0.0) },
            },
        },
    };

    static Control Header(string text, string tip) { var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold }; ToolTip.SetTip(t, tip); return t; }

    static TextBlock Note(string text) => new() { FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap, Text = text };

    static MenuItem Item(string text, string? gesture, Action a)
    {
        var m = new MenuItem { Header = text };
        if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
        m.Click += (_, _) => a();
        return m;
    }

    /// <summary>Ctrl+C, Ctrl+V, Delete and Alt+Up/Down (a row moved) on a grid (not while a cell is edited), and its menu with them.</summary>
    static void Keys(DataGrid dg, Func<Task> copy, Func<Task> paste, Action delete, Action<int> move)
    {
        var editing = false;
        dg.BeginningEdit += (_, e) => { if (!e.Cancel) editing = true; };
        dg.CellEditEnded += (_, _) => editing = false;
        dg.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (editing) return;
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; delete(); }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await copy(); }
            else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await paste(); }
            else if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.Alt) { e.Handled = true; move(e.Key == Key.Up ? -1 : 1); }
        }, RoutingStrategies.Tunnel);
        dg.ContextMenu = new ContextMenu
        {
            Items =
            {
                Item("Copy", "Ctrl+C", () => _ = copy()), Item("Paste", "Ctrl+V", () => _ = paste()), new Separator(),
                Item("Move Up", "Alt+Up", () => move(-1)), Item("Move Down", "Alt+Down", () => move(1)), new Separator(),
                Item("Delete Rows", "Delete", delete),
            },
        };
    }

    static string Cell(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>The library's materials (item, name), and none first.</summary>
    static List<(string item, string name)> Materials()
    {
        var list = new List<(string item, string name)> { ("", "(the part's own)") };
        foreach (var t in Core.Library.LibraryLink.Current.Offered(Core.Library.TableKinds.Material))
            foreach (var r in t.Rows)
            {
                var item = Core.Library.LibrarySnapshot.ItemKey(t.Key, r.Key);
                if (list.All(m => m.item != item)) list.Add((item, r.Key));
            }
        return list;
    }

    /// <summary>A design table's "1"/"0" as a checkbox, and back.</summary>
    sealed class OnOff : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly OnOff Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            !(value is string s && (s.Trim() == "0" || s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)));
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? "1" : "0";
    }

    static bool IsName(string? s) => !string.IsNullOrEmpty(s) && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_') && EquationEntry.NameProblem(s) == null;

    // ---- the equations -------------------------------------------------------------------------------------------

    /// <summary>A row of the equations: an equation being edited (or, greyed, a variable a design table sets), and what it works out to.</summary>
    sealed class EqRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        readonly Action _changed;
        /// <summary>The design table setting it (read only here), or null: an equation of the part's.</summary>
        public readonly string? FromTable;
        public EqRow(Equation e, Action changed, string? fromTable = null)
        {
            _changed = changed; FromTable = fromTable;
            _name = e.Name; _expr = fromTable != null ? $"from table {fromTable}" : e.Expr; _exposed = e.Exposed; _description = e.Description ?? "";
            _min = e.Min is { } a ? Expr.Format(a) : ""; _max = e.Max is { } b ? Expr.Format(b) : "";
        }
        string _name, _expr, _description, _min, _max;
        bool _exposed;
        /// <summary>A cell being edited: its value goes in when the edit ends (not at each key typed), the grid then says so.</summary>
        public Func<bool>? Editing;
        void Set<T>(ref T field, T value, string name)
        {
            if (FromTable != null || Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (Editing?.Invoke() != true) _changed();
        }
        /// <summary>The name the part knows it by (a rename done when the cell's edit ends: everywhere it is used).</summary>
        public string Committed = "";
        public string Name { get => _name; set => Set(ref _name, value?.Trim() ?? "", nameof(Name)); }
        public void Unrename() { _name = Committed; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
        public string Equation { get => _expr; set => Set(ref _expr, string.IsNullOrWhiteSpace(value) ? "0" : value.Trim(), nameof(Equation)); }
        public bool User { get => _exposed; set => Set(ref _exposed, value, nameof(User)); }
        public string Description { get => _description; set => Set(ref _description, value ?? "", nameof(Description)); }
        public string Min { get => _min; set => Set(ref _min, value?.Trim() ?? "", nameof(Min)); }
        public string Max { get => _max; set => Set(ref _max, value?.Trim() ?? "", nameof(Max)); }

        // what it works out to, or why it does not (red); a brush left empty would draw nothing
        public string Value { get; private set; } = "";
        public IBrush ValueBrush { get; private set; } = Palette.Text;
        public string? Problem { get; private set; }
        // what kind of value it is: a length, an angle, a plain number (a count, a ratio, a yes/no)
        public string Kind { get; private set; } = "";
        public void Show(string value, string? problem, string kind = "")
        {
            (Value, Problem, ValueBrush, Kind) = (problem ?? value, problem, problem != null ? Palette.Danger : FromTable != null ? Palette.TextMuted : Palette.Text, problem != null ? "" : kind);
            foreach (var p in new[] { nameof(Value), nameof(ValueBrush), nameof(Problem), nameof(Kind) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

        // where it is used: the features and values (clicked: the first feature selected in the tree), or unused
        public string UsedBy { get; private set; } = "";
        public string? UsedTip { get; private set; }
        public IBrush UsedBrush { get; private set; } = Palette.Text;
        public string? FirstFeature { get; private set; }
        public void Uses(List<VariableUses.Use>? uses)
        {
            uses ??= new();
            UsedBy = uses.Count == 0 ? "unused" : string.Join(", ", uses.Select(u => u.FeatureId != null && u.What != u.Owner ? $"{u.Owner} ({u.What})" : u.Owner).Distinct());
            UsedTip = uses.Count == 0 ? "Nothing uses it." : "Used by:\n" + string.Join("\n", uses.Select(u => $"{u.Owner}: {u.What}").Distinct()) + (uses.Any(u => u.FeatureId != null) ? "\nClick to select the first feature in the tree." : "");
            UsedBrush = uses.Count == 0 ? Palette.TextMuted : Palette.Text;
            FirstFeature = uses.FirstOrDefault(u => u.FeatureId != null)?.FeatureId;
            foreach (var p in new[] { nameof(UsedBy), nameof(UsedTip), nameof(UsedBrush) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
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

    static string KindOf(Quantity q) => q.IsLength ? "length" : q.IsAngle ? "angle" : q.IsNumber ? "number"
        : q.Angle == 0 && q.Length == 2 ? "area" : q.Angle == 0 && q.Length == 3 ? "volume" : "";

    static (Control view, Action refresh, Action<string> select) Equations(MainWindow owner)
    {
        var session = owner.Session;
        var units = session.Doc.Units;
        var items = new ObservableCollection<EqRow>();
        // a count of the rows with a problem, clicked: the first selected
        var problems = new TextBlock { Foreground = Palette.Danger, FontWeight = FontWeight.SemiBold, Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false };
        bool loading = true;
        IEnumerable<EqRow> Own() => items.Where(r => r.FromTable == null);

        static string KeyOf(IEnumerable<Equation> eqs) => string.Join("\n", eqs.Select(e => $"{e.Name}\t{e.Expr}\t{e.Exposed}\t{e.Description}\t{e.Min}\t{e.Max}"));
        // into the part as made, when it differs from what the part has (one undo step)
        void Apply()
        {
            if (loading) return;
            var result = Own().Where(r => r.Name.Trim().Length > 0).Select(r => r.ToEquation()).ToList();
            if (KeyOf(result) != KeyOf(session.Doc.Equations)) session.Change(() => session.Doc.Equations = result);
        }
        void Recompute()
        {
            var tables = session.Doc.Tables;
            var eqs = Own().Select(r => new Equation { Name = r.Name, Expr = r.Equation }).ToList();
            var (vals, errs) = Regenerator.EvaluateEquations(new CadDocument { Equations = eqs, Tables = tables, Units = units });
            var fromTables = items.Where(r => r.FromTable != null).Select(r => r.Name.Trim()).ToList();
            foreach (var r in items)
            {
                var name = r.Name.Trim();
                string? problem = null;
                if (r.FromTable == null)
                    // (one set twice has a value, from where it was set first: the second is the error)
                    problem = name.Length == 0 ? "Give it a name"
                        : EquationEntry.NameProblem(name)
                        ?? (Own().Count(x => x.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) > 1 || fromTables.Contains(name, StringComparer.OrdinalIgnoreCase)
                            ? $"{name} is set twice (an equation and a design table, or two equations)" : errs.GetValueOrDefault(r.Name));
                r.Show(vals.TryGetValue(name, out var v) ? units.Show(v) : "", problem, vals.TryGetValue(name, out var q) ? KindOf(q) : "");
            }
            var bad = items.Count(r => r.Problem != null);
            problems.Text = bad == 0 ? "" : $"\u26A0 {bad} problem{(bad == 1 ? "" : "s")}: click to show {(bad == 1 ? "it" : "the first")}";
            problems.IsVisible = bad > 0;
            Apply();
            // (from the part as it now is)
            var uses = VariableUses.Map(session.Doc);
            foreach (var r in items) r.Uses(uses.GetValueOrDefault(r.Name.Trim()));
        }
        // a variable renamed (its cell's edit ended): everywhere it is used too, the table then shown again from the part;
        // a name it cannot have: the old one back, why said
        void Rename(EqRow r)
        {
            var (from, to) = (r.Committed, r.Name.Trim());
            string? problem = !IsName(to) ? $"\"{to}\" cannot name a variable (letters, digits and _, starting with a letter; not a function, constant or unit)."
                : items.Any(x => x != r && x.Name.Trim().Equals(to, StringComparison.OrdinalIgnoreCase)) ? $"{to} is already a variable." : null;
            if (problem != null) { r.Unrename(); owner.Status(problem); return; }
            int n = 0;
            session.Change(() => n = VariableUses.Rename(session.Doc, from, to));
            owner.Status(n == 0 ? $"Renamed {from} to {to}." : $"Renamed {from} to {to}, and in the {n} value{(n == 1 ? "" : "s")} using it.");
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        }
        bool editingCell = false;
        EqRow Make(Equation e) => new(e, Recompute) { Editing = () => editingCell, Committed = e.Name.Trim() };

        var dg = NewGrid();
        dg.ItemsSource = items;
        // a count of the rows with a problem, clicked: the first selected
        problems.PointerPressed += (_, _) =>
        {
            if (items.FirstOrDefault(r => r.Problem != null) is not { } first) return;
            dg.SelectedItem = first;
            dg.ScrollIntoView(first, dg.Columns[0]);
        };
        void Text(string header, string tip, string property, DataGridLength width) =>
            dg.Columns.Add(new DataGridTextColumn { Header = Header(header, tip), Binding = new Binding(property) { Mode = BindingMode.TwoWay }, Width = width });
        Text("Name", "The variable's name, used in any value (W, Teeth).", nameof(EqRow.Name), new DataGridLength(120));
        Text("Value / Equation", "A value (10mm, 2in, 30deg, 6) or an equation of other variables (W/2, sqrt(A^2 + B^2), if(N < 30, 5mm, 8mm)). A value without a unit is read in the part's units.",
            nameof(EqRow.Equation), new DataGridLength(1, DataGridLengthUnitType.Star));
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("Evaluates To", "What it works out to, in the part's units; why not, in red."), Width = new DataGridLength(130), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<EqRow>((_, _) => new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                [!TextBlock.TextProperty] = new Binding(nameof(EqRow.Value)),
                [!TextBlock.ForegroundProperty] = new Binding(nameof(EqRow.ValueBrush)),
                [!ToolTip.TipProperty] = new Binding(nameof(EqRow.Problem)),
            }),
        });
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("Kind", "What it is: a length, an angle, or a plain number (a count, a ratio, 1/0 for yes/no). A plain number in a length is read in the part's units."),
            Width = new DataGridLength(70), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<EqRow>((_, _) => new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Foreground = Palette.TextMuted, FontSize = TypeScale.Small,
                [!TextBlock.TextProperty] = new Binding(nameof(EqRow.Kind)),
            }),
        });
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("Used By", "The features and values using it (clicked: the first feature selected in the tree); unused when nothing does."),
            Width = new DataGridLength(170), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<EqRow>((_, _) =>
            {
                var t = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, Cursor = new Cursor(StandardCursorType.Hand),
                    [!TextBlock.TextProperty] = new Binding(nameof(EqRow.UsedBy)),
                    [!TextBlock.ForegroundProperty] = new Binding(nameof(EqRow.UsedBrush)),
                    [!ToolTip.TipProperty] = new Binding(nameof(EqRow.UsedTip)),
                };
                t.PointerPressed += (_, _) => { if (t.DataContext is EqRow { FirstFeature: { } id }) owner.ShowFeature(id); };
                return t;
            }),
        });
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("User", "Shown in Configure, where an assembly sets it for each instance."), Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader),
            CellTemplate = new FuncDataTemplate<EqRow>((r, _) => new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0, Padding = new Thickness(0), IsEnabled = r?.FromTable == null,
                [!ToggleButton.IsCheckedProperty] = new Binding(nameof(EqRow.User)) { Mode = BindingMode.TwoWay },
            }),
        });
        Text("Description", "A user variable's label in Configure.", nameof(EqRow.Description), new DataGridLength(150));
        Text("Min", "The smallest value Configure accepts. Min 0 and Max 1 make a checkbox.", nameof(EqRow.Min), new DataGridLength(60));
        Text("Max", "The largest value Configure accepts.", nameof(EqRow.Max), new DataGridLength(60));
        // a design table's variables: greyed, not edited here; a cell's value in when its edit ends (a rename then too)
        dg.BeginningEdit += (_, e) => { if (e.Row.DataContext is EqRow { FromTable: not null }) e.Cancel = true; else editingCell = true; };
        dg.CellEditEnded += (_, e) =>
        {
            editingCell = false;
            if (e.Row.DataContext is not EqRow r || r.FromTable != null) return;
            if (e.EditAction == DataGridEditAction.Commit && r.Committed.Length > 0 && r.Name.Trim() != r.Committed) { Rename(r); return; }
            if (r.Committed.Length == 0) r.Committed = r.Name.Trim();
            Recompute();
        };
        dg.LoadingRow += (_, e) =>
        {
            var table = e.Row.DataContext is EqRow r && r.FromTable != null;
            if (table) { e.Row.Foreground = Palette.TextMuted; e.Row.FontStyle = FontStyle.Italic; }
            else { e.Row.ClearValue(TemplatedControl.ForegroundProperty); e.Row.ClearValue(TemplatedControl.FontStyleProperty); }
        };

        List<EqRow> Selected() => dg.SelectedItems.OfType<EqRow>().Where(r => r.FromTable == null).OrderBy(items.IndexOf).ToList();
        void Delete() { foreach (var r in Selected()) items.Remove(r); Recompute(); }
        async Task Copy()
        {
            var sel = Selected();
            if (sel.Count == 0) sel = Own().ToList();
            if (sel.Count == 0 || TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip) return;
            var lines = new List<string> { "Name\tValue / Equation\tEvaluates To\tUser\tDescription\tMin\tMax" };
            lines.AddRange(sel.Select(r => string.Join("\t", Cell(r.Name), Cell(r.Equation), Cell(r.Value), r.User ? "TRUE" : "FALSE", Cell(r.Description), r.Min, r.Max)));
            await clip.SetTextAsync(string.Join("\r\n", lines));
            owner.Status($"Copied {sel.Count} variable{(sel.Count == 1 ? "" : "s")}.");
        }
        async Task Paste()
        {
            if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip || await clip.GetTextAsync() is not { Length: > 0 } text) return;
            int n = 0;
            loading = true; // (one undo step for the whole paste)
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                var c = line.Split('\t');
                var name = c[0].Trim();
                if (name.Length == 0 || name.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue; // (a blank line, the heading)
                var expr = c.Length > 1 && c[1].Trim().Length > 0 ? c[1].Trim() : "0";
                var row = Own().FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (row == null) items.Insert(Own().Count(), row = Make(new Equation { Name = name, Expr = expr }));
                else row.Equation = expr;
                // (as copied here: Evaluates To third, then User, Description, Min, Max)
                if (c.Length > 3) row.User = c[3].Trim() is var u && (u.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || u == "1" || u.Equals("yes", StringComparison.OrdinalIgnoreCase) || u.Equals("x", StringComparison.OrdinalIgnoreCase));
                if (c.Length > 4) row.Description = c[4].Trim();
                if (c.Length > 5) row.Min = c[5].Trim();
                if (c.Length > 6) row.Max = c[6].Trim();
                n++;
            }
            loading = false;
            Recompute();
            owner.Status($"Pasted {n} variable{(n == 1 ? "" : "s")}.");
        }
        // the row picked moved up or down among the equations (their order is the part's)
        void Move(int by)
        {
            if (Selected() is not [var r]) return;
            var own = Own().ToList();
            var i = own.IndexOf(r);
            if (i + by < 0 || i + by >= own.Count) return;
            items.Move(items.IndexOf(r), items.IndexOf(own[i + by]));
            Recompute();
            dg.SelectedItem = r;
        }
        Keys(dg, Copy, Paste, Delete, Move);
        // names suggested while an equation is typed in its cell
        var host = new Panel { Children = { dg } };
        dg.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox tb && e.Column.DisplayIndex == 1)
                NameSuggest.Attach(tb, () => items.Where(x => x.Name.Trim().Length > 0 && x.Problem == null).Select(x => (x.Name.Trim(), x.Value)), host);
        };

        // the part's equations, then the variables its design tables set
        void Refresh()
        {
            loading = true;
            items.Clear();
            foreach (var e in session.Doc.Equations) items.Add(Make(e));
            foreach (var t in session.Doc.Tables ?? new())
                foreach (var name in new[] { t.Name }.Concat(t.Columns).Where(n => !string.IsNullOrWhiteSpace(n) && !TableConfig.IsProperty(n)))
                    // (an equation's own row stands for it: a table of its standard values, or a column setting it)
                    if (!session.Doc.Equations.Any(e => e.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                        && session.Doc.Tables!.All(o => o == t || !o.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        items.Add(new EqRow(new Equation { Name = name.Trim() }, Recompute, t.Name));
            Recompute();
            loading = false;
        }
        Refresh();

        var add = new Button { Content = "+ Variable" };
        add.Click += (_, _) =>
        {
            int k = Own().Count() + 1;
            while (items.Any(r => r.Name.Equals("Var" + k, StringComparison.OrdinalIgnoreCase))) k++;
            var r = Make(new Equation { Name = "Var" + k, Expr = units == UnitSystem.IPS ? "1in" : "10mm" });
            items.Insert(Own().Count(), r);
            Recompute();
            dg.SelectedItem = r;
            dg.ScrollIntoView(r, dg.Columns[0]);
        };
        // the equations nothing uses, deleted (one undo step)
        var unused = new Button { Content = "Delete Unused" };
        ToolTip.SetTip(unused, "Deletes the equations nothing uses (Used By: unused).");
        unused.Click += (_, _) =>
        {
            var gone = Own().Where(r => r.UsedBy == "unused").ToList();
            if (gone.Count == 0) { owner.Status("Every equation is used."); return; }
            foreach (var r in gone) items.Remove(r);
            Recompute();
            owner.Status($"Deleted {gone.Count} unused equation{(gone.Count == 1 ? "" : "s")}: {string.Join(", ", gone.Select(r => r.Name))}.");
        };
        void Select(string name)
        {
            if (items.FirstOrDefault(r => r.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } r) return;
            dg.SelectedItem = r;
            dg.ScrollIntoView(r, dg.Columns[0]);
        }
        var view = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Note("Variables for any dimension or feature value; typing L = 10 in a value field adds one here. Those a design table sets are listed greyed. "
                    + "Changes apply as made (Ctrl+Z in the window undoes them). Ctrl+C copies rows for a spreadsheet, Ctrl+V pastes them back (name, then value); Alt+Up/Down moves a row."),
                problems, host,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, unused } },
            },
        };
        return (view, Refresh, Select);
    }

    // ---- the design tables ---------------------------------------------------------------------------------------

    /// <summary>A row of a design table: its key, a value per column, whether it is the row in use.</summary>
    sealed class TRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public readonly DesignRow Row;
        readonly DesignTable _t;
        readonly Action _changed;
        public TRow(DesignRow row, DesignTable t, Action changed) { Row = row; _t = t; _changed = changed; }
        /// <summary>A cell being edited: its value goes in when the edit ends (the grid then says so), not at each key.</summary>
        public Func<bool>? Editing;
        void Changed() { if (Editing?.Invoke() != true) _changed(); }
        void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public string Key
        {
            get => Row.Key;
            set
            {
                var was = Row.Key; var now = value?.Trim() ?? "";
                if (now == was) return;
                Row.Key = now;
                if (_t.Selected == was) _t.Selected = now;
                Notify(nameof(Key)); Changed();
            }
        }
        public string this[int c]
        {
            get => c < Row.Values.Count ? Row.Values[c] : "0";
            set
            {
                while (Row.Values.Count <= c) Row.Values.Add("0");
                var now = value?.Trim() ?? "";
                if (now.Length == 0 && !(c < _t.Columns.Count && TableConfig.IsProperty(_t.Columns[c]))) now = "0";
                if (Row.Values[c] == now) return;
                Row.Values[c] = now;
                Notify("Item[]"); Changed();
            }
        }
        public bool Active
        {
            get => ReferenceEquals(_t.Current, Row);
            set { if (value && !Active) { _t.Selected = Row.Key; _changed(); } }
        }
        public void ActiveChanged() => Notify(nameof(Active));
    }

    /// <summary>The list (Equations, then the design tables) and, beside it, the one picked.</summary>
    static Control Body(MainWindow owner, (Control view, Action refresh) equations, string? startTable)
    {
        var session = owner.Session;
        var tables = new List<DesignTable>();
        var start = (session.Doc.Tables ?? new()).FindIndex(t => t.Name.Equals(startTable ?? "", StringComparison.OrdinalIgnoreCase));
        int current = Math.Max(0, start);
        bool onEquations = start < 0;
        var list = new ListBox { Classes = { "selbox" } };
        var right = new StackPanel { Spacing = 6 };
        var error = new TextBlock { Foreground = Palette.Danger, FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap };
        bool listing = false;
        var addTable = new Button { Content = "+", MinWidth = 32 };
        ToolTip.SetTip(addTable, "Add a design table");
        var delTable = new Button { Content = "−", MinWidth = 32 };
        ToolTip.SetTip(delTable, "Delete the design table picked (Equations stays)");

        // what stops the tables going in the part (named as variables, unique, with rows), or null
        string? Problem()
        {
            var names = new HashSet<string>(session.Doc.Equations.Select(e => e.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                if (!IsName(t.Name)) return $"\"{t.Name}\" cannot name a table (letters, digits and _, starting with a letter; not a function, constant or unit).";
                // (named after an equation: that equation's standard values, each row's key one)
                var values = session.Doc.Equations.Any(e => e.Name.Trim().Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!values && !names.Add(t.Name)) return $"{t.Name} is already a variable.";
                if (values && tables.Count(o => o.Name.Trim().Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1) return $"{t.Name} has two tables.";
                foreach (var c in t.Columns)
                {
                    if (TableConfig.IsProperty(c))
                    {
                        if (!TableConfig.Properties.Contains(c.Trim())) return $"{t.Name}: {c} is not a property (Part Number, Description or Material).";
                        continue;
                    }
                    // (another table's name: the row of it this one picks; an equation's: the value it sets it to)
                    if (tables.Any(o => o != t && o.Name.Trim().Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))
                        || session.Doc.Equations.Any(e => e.Name.Trim().Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
                    if (!IsName(c)) return $"{t.Name}: \"{c}\" cannot name a variable.";
                    if (!names.Add(c)) return $"{t.Name}: {c} is already a variable (in Equations or another table).";
                }
                if (t.Rows.Count == 0) return $"{t.Name} has no rows.";
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in t.Rows)
                    if (r.Key.Length == 0 || !keys.Add(r.Key)) return $"{t.Name}: each row needs a key of its own (\"{r.Key}\").";
            }
            return null;
        }
        // into the part as made, when right (else why not, under the table) and different (one undo step)
        void Apply()
        {
            foreach (var t in tables) { t.Name = t.Name.Trim(); t.Description = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description.Trim(); }
            if (Problem() is { } p) { error.Text = p + " Not applied until corrected."; return; }
            error.Text = "";
            var json = System.Text.Json.JsonSerializer.Serialize(tables, Serialization.Options);
            if (json == System.Text.Json.JsonSerializer.Serialize(session.Doc.Tables ?? new(), Serialization.Options)) return;
            var copy = tables.Select(Serialization.Clone).ToList();
            session.Change(() => { session.Doc.Tables = copy.Count == 0 ? null : copy; TableConfig.ApplyMaterial(session.Doc); });
        }

        // the list: Equations, then the tables; the one picked shown beside it
        void Relist()
        {
            listing = true;
            if (tables.Count == 0) onEquations = true;
            current = Math.Clamp(current, 0, Math.Max(0, tables.Count - 1));
            list.ItemsSource = new[] { "Equations" }.Concat(tables.Select(t => string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name)).ToList();
            list.SelectedIndex = onEquations ? 0 : current + 1;
            listing = false;
            ShowPicked();
        }
        list.SelectionChanged += (_, _) =>
        {
            if (listing || list.SelectedIndex < 0) return;
            onEquations = list.SelectedIndex == 0;
            if (!onEquations) current = list.SelectedIndex - 1;
            ShowPicked();
        };

        void ShowPicked()
        {
            right.Children.Clear();
            delTable.IsEnabled = !onEquations;
            if (onEquations)
            {
                // (shown again from the part: the tables may have changed the variables they set)
                equations.refresh();
                right.Children.Add(equations.view);
                return;
            }
            var t = tables[current];
            right.Children.Add(Note((session.Doc.Equations.Any(e => e.Name.Trim().Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                ? $"Named after the equation {t.Name.Trim()}: its standard values, each row's key one (12, 16, 20). The row picked sets {t.Name.Trim()}; user-facing, Configure offers them. "
                : "") + "Each row sets values for a set of variables (values or expressions); the table's name holds the row in use (1 = first), for conditions such as "
                + "Size >= 3 (Suppress When). Changes apply as made. Ctrl+C copies the table for a spreadsheet; Ctrl+V pastes one back (a heading row of variable names, then a row per configuration)."));

            // its name, user-facing and description
            var name = new TextBox { Text = t.Name, Width = 140 };
            ToolTip.SetTip(name, "The table's name: the choice in Configure, and a variable holding the row in use (1 = first).");
            void Rename() { if (name.Text?.Trim() is { } n && n != t.Name) RenameVariable(t.Name, n, () => t.Name = n, t, column: false); }
            name.LostFocus += (_, _) => Rename();
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(); e.Handled = true; } };
            var user = new CheckBox { Content = "User-facing", IsChecked = t.Exposed };
            ToolTip.SetTip(user, "Shows the rows as a list in Configure; set per instance in assemblies.");
            user.IsCheckedChanged += (_, _) => { t.Exposed = user.IsChecked == true; Apply(); };
            var desc = new TextBox { Text = t.Description ?? "", Watermark = "Description for Configure", MinWidth = 220 };
            desc.LostFocus += (_, _) => { if ((desc.Text ?? "") != (t.Description ?? "")) { t.Description = desc.Text; Apply(); } };
            right.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center }, name, user, desc },
            });

            // its grid: the key, a column per variable, the row in use
            var rows = new ObservableCollection<TRow>();
            void Changed() { foreach (var r in rows) r.ActiveChanged(); Apply(); }
            bool editingCell = false;
            TRow NewRow(DesignRow r) => new(r, t, Changed) { Editing = () => editingCell };
            foreach (var r in t.Rows) rows.Add(NewRow(r));
            var dg = NewGrid();
            dg.ItemsSource = rows;
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = Header("Row", "The row's key: what Configure lists (M3, M4, Small)."), Binding = new Binding(nameof(TRow.Key)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(110),
            });
            for (int c = 0; c < t.Columns.Count; c++)
            {
                int cc = c;
                var head = new TextBlock { Text = t.Columns[c], FontWeight = FontWeight.SemiBold, Background = Brushes.Transparent };
                ToolTip.SetTip(head, $"The variable {t.Columns[c]}: each row's value (a value or an expression). Right-click to rename or delete the column.");
                head.ContextMenu = new ContextMenu
                {
                    Items =
                    {
                        Item("Rename Column...", null, async () =>
                        {
                            if (TopLevel.GetTopLevel(dg) is not Window w || await Dialogs.Input(w, "Rename Column", "Variable", t.Columns[cc]) is not { } n || n.Trim().Length == 0) return;
                            RenameVariable(t.Columns[cc], n.Trim(), () => t.Columns[cc] = n.Trim(), t, column: true);
                        }),
                        Item("Move Left", null, () => MoveColumn(t, cc, -1)),
                        Item("Move Right", null, () => MoveColumn(t, cc, 1)),
                        new Separator(),
                        Item("Delete Column", null, () => DeleteColumn(t, cc)),
                    },
                };
                var colName = t.Columns[c];
                // a property: text (Part Number, Description), or a material picked from the library
                if (TableConfig.IsProperty(colName))
                {
                    head.Text = TableConfig.Label(colName);
                    ToolTip.SetTip(head, colName.Trim() == TableConfig.Material
                        ? "The material of the part in each row (the Bill of Materials, its mass and look). Right-click to move or delete the column."
                        : $"The part's {TableConfig.Label(colName).ToLowerInvariant()} in each row, as the Bill of Materials and drawings show it. Right-click to move or delete the column.");
                    if (colName.Trim() == TableConfig.Material)
                    {
                        var materials = Materials();
                        dg.Columns.Add(new DataGridTemplateColumn
                        {
                            Header = head, Width = new DataGridLength(170),
                            CellTemplate = new FuncDataTemplate<TRow>((_, _) => new ComboBox
                            {
                                ItemsSource = materials.Select(m => m.item).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(6, 0),
                                ItemTemplate = new FuncDataTemplate<string>((k, _) => new TextBlock { Text = materials.FirstOrDefault(m => m.item == k).name ?? k }),
                                [!SelectingItemsControl.SelectedItemProperty] = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay },
                            }),
                        });
                    }
                    else dg.Columns.Add(new DataGridTextColumn { Header = head, Binding = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay }, Width = new DataGridLength(130) });
                    continue;
                }
                // another table's name: the row of it each row picks (a combination of configurations)
                if (tables.FirstOrDefault(o => o != t && o.Name.Trim().Equals(colName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } picks)
                {
                    ToolTip.SetTip(head, $"The row of {picks.Name} each row picks: a row of this table combines it with the rest. Right-click to rename, move or delete the column.");
                    var keys = picks.Rows.Select(r => r.Key).ToList();
                    dg.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = head, Width = new DataGridLength(110),
                        CellTemplate = new FuncDataTemplate<TRow>((_, _) => new ComboBox
                        {
                            ItemsSource = keys, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(6, 0),
                            [!SelectingItemsControl.SelectedItemProperty] = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay },
                        }),
                    });
                    continue;
                }
                // an equation's name: the value each row sets it to
                if (session.Doc.Equations.Any(e => e.Name.Trim().Equals(colName.Trim(), StringComparison.OrdinalIgnoreCase)))
                    ToolTip.SetTip(head, $"The value of the equation {colName} in each row (in place of its own). Right-click to rename, move or delete the column.");
                // a feature's on/off column (its Suppress When is "not" the column): a checkbox per row
                if (Switched(t.Columns[c]) is { } sw)
                {
                    head.Text = sw.Name;
                    ToolTip.SetTip(head, $"{sw.Name} is built in the rows ticked (its Suppress When: not {t.Columns[c]}). Right-click to rename or delete the column.");
                    dg.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = head, Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader),
                        CellTemplate = new FuncDataTemplate<TRow>((_, _) => new CheckBox
                        {
                            HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0, Padding = new Thickness(0),
                            [!ToggleButton.IsCheckedProperty] = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay, Converter = OnOff.Instance },
                        }),
                    });
                    continue;
                }
                dg.Columns.Add(new DataGridTextColumn { Header = head, Binding = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay }, Width = new DataGridLength(90) });
            }
            var group = "dt" + Guid.NewGuid().ToString("N");
            dg.Columns.Add(new DataGridTemplateColumn
            {
                Header = Header("Active", "The row the part uses unless an assembly picks another."), Width = new DataGridLength(1, DataGridLengthUnitType.SizeToHeader),
                CellTemplate = new FuncDataTemplate<TRow>((_, _) => new RadioButton
                {
                    GroupName = group, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0, Padding = new Thickness(0),
                    [!ToggleButton.IsCheckedProperty] = new Binding(nameof(TRow.Active)) { Mode = BindingMode.TwoWay },
                }),
            });

            List<TRow> Selected() => dg.SelectedItems.OfType<TRow>().OrderBy(rows.IndexOf).ToList();
            void Delete() { foreach (var r in Selected()) { t.Rows.Remove(r.Row); rows.Remove(r); } Changed(); }
            async Task Copy()
            {
                var sel = Selected();
                if (sel.Count == 0) sel = rows.ToList();
                if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip) return;
                var lines = new List<string> { string.Join("\t", new[] { Cell(t.Name) }.Concat(t.Columns.Select(Cell))) };
                lines.AddRange(sel.Select(r => string.Join("\t", new[] { Cell(r.Key) }.Concat(t.Columns.Select((_, c) => Cell(r.Row.ValueAt(c)))))));
                await clip.SetTextAsync(string.Join("\r\n", lines));
                owner.Status($"Copied {t.Name}: {sel.Count} row{(sel.Count == 1 ? "" : "s")}.");
            }
            async Task Paste()
            {
                if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip || DesignTable.Parse(await clip.GetTextAsync()) is not { } pasted)
                { error.Text = "No table on the clipboard: copy a heading row of variable names and a row per configuration (its key, then its values)."; return; }
                // the same columns: rows changed by key and added; other columns: the table becomes the cells pasted
                if (pasted.Columns.Count == t.Columns.Count && pasted.Columns.Zip(t.Columns).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)))
                    foreach (var pr in pasted.Rows)
                    {
                        if (t.Row(pr.Key) is { } have) have.Values = pr.Values;
                        else t.Rows.Add(pr);
                    }
                else
                {
                    t.Columns = pasted.Columns; t.Rows = pasted.Rows;
                    if (t.Row(t.Selected ?? "") == null) t.Selected = null;
                }
                ShowPicked(); Apply();
                owner.Status($"Pasted {pasted.Rows.Count} row{(pasted.Rows.Count == 1 ? "" : "s")} into {t.Name}.");
            }
            void Move(int by)
            {
                if (Selected() is not [var r]) return;
                var i = t.Rows.IndexOf(r.Row);
                if (i + by < 0 || i + by >= t.Rows.Count) return;
                t.Rows.RemoveAt(i); t.Rows.Insert(i + by, r.Row);
                rows.Move(rows.IndexOf(r), rows.IndexOf(r) + by);
                Changed();
                dg.SelectedItem = r;
            }
            Keys(dg, Copy, Paste, Delete, Move);
            dg.BeginningEdit += (_, e) => { if (!e.Cancel) editingCell = true; };
            dg.CellEditEnded += (_, e) => { editingCell = false; if (e.EditAction == DataGridEditAction.Commit) Changed(); };
            var host = new Panel { Children = { dg } };
            dg.PreparingCellForEdit += (_, e) =>
            {
                if (e.EditingElement is TextBox tb && e.Column.DisplayIndex > 0)
                    NameSuggest.Attach(tb, () => Regenerator.EvaluateEquations(session.Doc).values.Select(kv => (kv.Key, session.Doc.Units.Show(kv.Value))), host);
            };
            right.Children.Add(host);

            var addRow = new Button { Content = "+ Row" };
            addRow.Click += (_, _) =>
            {
                int k = t.Rows.Count + 1;
                while (t.Row("Row" + k) != null) k++;
                var r = new DesignRow { Key = "Row" + k, Values = t.Rows.LastOrDefault()?.Values.ToList() ?? t.Columns.Select(_ => "0").ToList() };
                t.Rows.Add(r);
                var tr = NewRow(r);
                rows.Add(tr);
                Changed();
                dg.SelectedItem = tr;
                dg.ScrollIntoView(tr, dg.Columns[0]);
            };
            var addCol = new Button { Content = "+ Column" };
            addCol.Click += async (_, _) =>
            {
                int n = t.Columns.Count + 1;
                while (t.Columns.Contains("Var" + n, StringComparer.OrdinalIgnoreCase)) n++;
                if (TopLevel.GetTopLevel(dg) is not Window w || await Dialogs.Input(w, "Add Column", "Variable", "Var" + n) is not { } v || v.Trim().Length == 0) return;
                t.Columns.Add(v.Trim());
                foreach (var r in t.Rows) r.Values.Add("0");
                ShowPicked(); Apply();
            };
            // a feature switched on and off by the table: a column of checkboxes, the feature's Suppress When reading it
            ToolTip.SetTip(addCol, "A column: a variable of its own, an equation's name (the value each row sets it to), or another table's name (the row of it each row picks).");
            // a property column: Part Number, Description, Material (those the table has not)
            var addProperty = new Button { Content = "+ Property" };
            ToolTip.SetTip(addProperty, "A column of the part's part number, description or material in each row: each row a named configuration, as the Bill of Materials lists it.");
            addProperty.Click += (_, _) =>
            {
                var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                foreach (var prop in TableConfig.Properties.Where(p => !t.Columns.Contains(p)))
                {
                    var pp = prop;
                    var mi = new MenuItem { Header = TableConfig.Label(prop) };
                    mi.Click += (_, _) =>
                    {
                        t.Columns.Add(pp);
                        // (the material the part has now, to start; text empty)
                        foreach (var r in t.Rows) r.Values.Add(pp == TableConfig.Material ? session.Doc.MaterialItem ?? "" : "");
                        ShowPicked(); Apply();
                    };
                    menu.Items.Add(mi);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(it has them all)", IsEnabled = false });
                menu.ShowAt(addProperty);
            };
            var addFeature = new Button { Content = "+ Feature On/Off" };
            ToolTip.SetTip(addFeature, "A column of checkboxes turning a feature on or off in each row (it sets the feature's Suppress When).");
            addFeature.Click += (_, _) =>
            {
                var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                foreach (var f in session.Doc.Features.Where(f => f is not SketchFeature && f.Name.Length > 0))
                {
                    var ff = f;
                    var mi = new MenuItem { Header = f.Name };
                    mi.Click += (_, _) => AddSwitch(t, ff);
                    menu.Items.Add(mi);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no features yet)", IsEnabled = false });
                menu.ShowAt(addFeature);
            };
            right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { addRow, addCol, addProperty, addFeature } });
            right.Children.Add(error);
        }

        // a feature whose Suppress When is "not" this variable: the table's column switches it on and off
        Feature? Switched(string column) => session.Doc.Features.FirstOrDefault(f => f.SuppressWhen is { } w
            && System.Text.RegularExpressions.Regex.IsMatch(w, $@"(^|\bor\s+)not\s+{System.Text.RegularExpressions.Regex.Escape(column)}\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        // a variable of the tables renamed: everywhere it is used too (one undo step), the tables then shown again from the part
        void RenameVariable(string from, string to, Action here, DesignTable t, bool column)
        {
            bool Is(string a) => a.Trim().Equals(to, StringComparison.OrdinalIgnoreCase);
            var equation = session.Doc.Equations.Any(e => Is(e.Name));
            var table = tables.Any(x => x != t && Is(x.Name));
            // what the new name makes it: a table named after an equation lists its standard values; a column named after an
            // equation sets it, after another table picks its row. Those are not renames of a variable: done here only
            if (!column && equation && !table || column && (equation || table) && !t.Columns.Any(c => c != from && Is(c)))
            {
                here(); Relist(); Apply();
                owner.Status(column ? (equation ? $"Each row now sets {to}." : $"Each row now picks a row of {to}.") : $"{to}'s standard values: make each row's key a value of it (12, 16, 20); its columns are not needed.");
                return;
            }
            if (!IsName(to)) { error.Text = $"\"{to}\" cannot name a variable."; return; }
            var known = session.Doc.Equations.Select(e => e.Name.Trim()).Concat(tables.SelectMany(x => new[] { x.Name }.Concat(x.Columns)));
            if (known.Any(Is)) { error.Text = $"{to} is already a variable."; return; }
            // (a table not in the part yet, or wrong: renamed here, applied when right)
            if (Problem() != null || !(session.Doc.Tables ?? new()).Any(x => x.Name.Equals(from, StringComparison.OrdinalIgnoreCase) || x.Columns.Contains(from, StringComparer.OrdinalIgnoreCase)))
            { here(); Relist(); Apply(); return; }
            int n = 0;
            session.Change(() => n = VariableUses.Rename(session.Doc, from, to));
            owner.Status(n == 0 ? $"Renamed {from} to {to}." : $"Renamed {from} to {to}, and in the {n} value{(n == 1 ? "" : "s")} using it.");
            tables = (session.Doc.Tables ?? new()).Select(Serialization.Clone).ToList();
            Relist();
        }
        // a column moved left or right (its values with it)
        void MoveColumn(DesignTable t, int c, int by)
        {
            if (c + by < 0 || c + by >= t.Columns.Count) return;
            (t.Columns[c], t.Columns[c + by]) = (t.Columns[c + by], t.Columns[c]);
            foreach (var r in t.Rows)
            {
                while (r.Values.Count < t.Columns.Count) r.Values.Add("0");
                (r.Values[c], r.Values[c + by]) = (r.Values[c + by], r.Values[c]);
            }
            ShowPicked(); Apply();
        }
        // a column deleted (a feature's on/off column: its Suppress When cleared too, in the same undo step)
        void DeleteColumn(DesignTable t, int c)
        {
            var col = t.Columns[c];
            var sw = Switched(col);
            t.Columns.RemoveAt(c);
            foreach (var r in t.Rows) if (c < r.Values.Count) r.Values.RemoveAt(c);
            if (sw == null || Problem() != null) { ShowPicked(); Apply(); return; }
            var copy = tables.Select(Serialization.Clone).ToList();
            session.Change(() =>
            {
                session.Doc.Tables = copy.Count == 0 ? null : copy;
                if (session.Doc.Find(sw.Id) is { } f) f.SuppressWhen = Unswitched(f.SuppressWhen, col);
            });
            ShowPicked();
        }
        static string? Unswitched(string? when, string col)
        {
            var w = System.Text.RegularExpressions.Regex.Replace(when ?? "", $@"(^|\s+or\s+)not\s+{System.Text.RegularExpressions.Regex.Escape(col)}\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (w.StartsWith("(") && w.EndsWith(")")) w = w[1..^1].Trim();
            return w.Length == 0 ? null : w;
        }
        // a feature switched on and off by a table: a column (on in every row to start), its Suppress When "not" it
        void AddSwitch(DesignTable t, Feature f)
        {
            var known = new HashSet<string>(session.Doc.Equations.Select(e => e.Name.Trim()).Concat(tables.SelectMany(x => new[] { x.Name }.Concat(x.Columns))), StringComparer.OrdinalIgnoreCase);
            var stem = new string(f.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            if (stem.Length == 0 || !char.IsLetter(stem[0])) stem = "F" + stem;
            var col = stem + "_On"; int k = 2;
            while (known.Contains(col)) col = stem + "_On" + k++;
            t.Columns.Add(col);
            foreach (var r in t.Rows) r.Values.Add("1");
            if (Problem() is { } p) { error.Text = p + " Not applied until corrected."; ShowPicked(); return; }
            var copy = tables.Select(Serialization.Clone).ToList();
            session.Change(() =>
            {
                session.Doc.Tables = copy.Count == 0 ? null : copy;
                if (session.Doc.Find(f.Id) is { } df) df.SuppressWhen = string.IsNullOrWhiteSpace(df.SuppressWhen) ? $"not {col}" : $"({df.SuppressWhen}) or not {col}";
            });
            owner.Status($"{f.Name} is now switched on and off by {t.Name}: untick it in the rows without it.");
            ShowPicked();
        }

        addTable.Click += (_, _) =>
        {
            int n = tables.Count + 1;
            var known = new HashSet<string>(session.Doc.Equations.Select(e => e.Name.Trim()).Concat(tables.SelectMany(t => new[] { t.Name }.Concat(t.Columns))), StringComparer.OrdinalIgnoreCase);
            var tname = known.Contains("Size") ? "Table" + n : "Size";
            while (known.Contains(tname)) tname = "Table" + ++n;
            var col = "D"; int k = 1;
            while (known.Contains(col)) col = "D" + ++k;
            var inch = session.Doc.Units == UnitSystem.IPS;
            tables.Add(new DesignTable
            {
                Name = tname, Columns = { col },
                Rows = { new DesignRow { Key = "A", Values = { inch ? "0.5in" : "10mm" } }, new DesignRow { Key = "B", Values = { inch ? "1in" : "20mm" } } },
            });
            current = tables.Count - 1;
            onEquations = false;
            Relist(); Apply();
        };
        delTable.Click += (_, _) =>
        {
            if (onEquations || tables.Count == 0) return;
            tables.RemoveAt(current);
            current = Math.Max(0, current - 1);
            Relist(); Apply();
        };

        tables = (session.Doc.Tables ?? new()).Select(Serialization.Clone).ToList();
        Relist();

        var left = new DockPanel { Width = 170 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0), Children = { addTable, delTable } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        list.MaxHeight = double.PositiveInfinity;
        left.Children.Add(list);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
        Grid.SetColumn(right, 2);
        body.Children.Add(left); body.Children.Add(right);
        return body;
    }
}
