using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.Core;
using SpryCad.Core.Model;

namespace SpryCad.App.UI;

/// <summary>A selection box in a PropertyManager page (blue when active: what is clicked goes in).</summary>
public sealed class PmSelectionBox
{
    public required string Label { get; init; }
    public required SelFilter Filter { get; init; }
    public List<SelItem> Items { get; } = new();
    public int Max { get; init; } = int.MaxValue;
    public Func<SelItem, bool>? Accept { get; init; }
    public ListBox List { get; } = new() { Classes = { "selbox" } };
    public event Action? Changed;
    public Func<SelItem, string>? LabelOf { get; set; }

    public bool Add(SelItem s)
    {
        if (Accept != null && !Accept(s)) return false;
        var i = Items.FindIndex(x => x.Key == s.Key);
        if (i >= 0) Items.RemoveAt(i);
        else
        {
            if (Max == 1) Items.Clear();
            if (Items.Count >= Max) return false;
            Items.Add(s);
        }
        Refresh();
        Changed?.Invoke();
        return true;
    }

    public void Remove(int idx)
    {
        if (idx < 0 || idx >= Items.Count) return;
        Items.RemoveAt(idx); Refresh(); Changed?.Invoke();
    }

    public void Clear() { Items.Clear(); Refresh(); Changed?.Invoke(); }

    public void Refresh()
    {
        List.ItemsSource = Items.Select(i => LabelOf?.Invoke(i) ?? i.Key).ToList();
    }
}

/// <summary>
/// PropertyManager page: title, OK/Cancel, and stacked sections of fields. Pages are built in code
/// by the feature commands; field edits raise <see cref="Changed"/> for live preview.
/// </summary>
public sealed class PmPage
{
    /// <summary>The OK / Cancel icon colors, shared with the confirmation corner so the pair looks the same in both.</summary>
    public static IBrush OkBrush => Palette.Success;
    public static IBrush CancelBrush => Palette.Danger;

    public string Title { get; }
    public string Icon { get; }
    public Control View => _root;
    public event Action? Changed;
    public Action? OnOk { get; set; }
    public Action? OnCancel { get; set; }

    readonly Dictionary<TextBox, string> _invalid = new();
    /// <summary>Label of the first field holding an invalid value (OK must not commit the last valid one silently).</summary>
    public string? FirstInvalidField => _invalid.Count == 0 ? null : _invalid.Values.First();
    public List<PmSelectionBox> Boxes { get; } = new();
    public PmSelectionBox? ActiveBox { get; private set; }
    public event Action? ActiveBoxChanged;
    public Func<string, Quantity?> Lookup { get; set; } = _ => null;
    public string? Message { get => _msg.Text; set { _msg.Text = value; _msgBorder.IsVisible = !string.IsNullOrEmpty(value); } }

    readonly DockPanel _root = new();
    readonly StackPanel _body = new() { Spacing = 0 };
    readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Palette.NoteText };
    readonly Border _msgBorder;

    public PmPage(string title, string icon, bool showOkCancel = true)
    {
        Title = title; Icon = icon;
        var header = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(Icons.Make(icon, 22));
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = TypeScale.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Heading });
        DockPanel.SetDock(titleRow, Dock.Top);
        header.Children.Add(titleRow);
        if (showOkCancel)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = Icons.Make("ok", 18, OkBrush), Classes = { "tb" } };
            ToolTip.SetTip(ok, "OK (Enter)");
            ok.Click += (_, _) => OnOk?.Invoke();
            var cancel = new Button { Content = Icons.Make("cancel", 18, CancelBrush), Classes = { "tb" } };
            ToolTip.SetTip(cancel, "Cancel (Esc)");
            cancel.Click += (_, _) => OnCancel?.Invoke();
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Top);
            header.Children.Add(buttons);
        }
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);
        _msgBorder = new Border
        {
            Background = Palette.NoteBg, BorderBrush = Palette.NoteBorder,
            BorderThickness = new Thickness(1), Margin = new Thickness(4, 2), Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(3),
            Child = _msg, IsVisible = false,
        };
        DockPanel.SetDock(_msgBorder, Dock.Top);
        _root.Children.Add(_msgBorder);
        _root.Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }

    public void RaiseChanged() => Changed?.Invoke();

    public StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = title, Classes = { "pmheader" } });
        _body.Children.Add(new Border { Classes = { "pmsection" }, Child = panel });
        return panel;
    }

    public void Info(StackPanel s, string text) =>
        s.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Palette.TextMuted });

    static Grid Row(string label, Control field, string? icon = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 1) };
        var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MinWidth = 82, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null) l.Children.Add(Icons.Make(icon, 16));
        l.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body });
        g.Children.Add(l);
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }

    /// <summary>The document's units: what a length typed without one is in, and what worked-out values show in.</summary>
    public static Func<UnitSystem> CurrentUnits { get; set; } = () => UnitSystem.MMGS;
    static UnitSystem Units => CurrentUnits();

    /// <summary>
    /// Numeric field that accepts expressions, the evaluated value as a tooltip. A length (<paramref name="unit"/> null)
    /// or an angle ("°") is shown and kept with its units: one typed without gets the document's ("10" → "10in", see
    /// <see cref="UnitFormat.TryEnter"/>), and <paramref name="set"/> is given the text so kept. Any other
    /// <paramref name="unit"/> is a plain number, the unit shown beside it.
    /// </summary>
    public TextBox Number(StackPanel s, string label, Func<string> get, Action<string> set, string? icon = null, string? unit = null, bool optional = false)
    {
        var kind = unit == null ? ValueKind.Length : unit == "°" ? ValueKind.Angle : ValueKind.Number;
        var tb = new TextBox { Text = UnitFormat.Show(get(), kind), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Stretch };
        void MarkInvalid(bool bad) { if (bad) _invalid[tb] = label; else _invalid.Remove(tb); }
        Control field = tb;
        if (kind == ValueKind.Number && !string.IsNullOrEmpty(unit))
        {
            var unitLbl = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Foreground = Palette.TextMuted, FontSize = TypeScale.Small };
            var dp = new DockPanel();
            DockPanel.SetDock(unitLbl, Dock.Right);
            dp.Children.Add(unitLbl); dp.Children.Add(tb);
            field = dp;
        }
        void Validate()
        {
            var t = tb.Text ?? "";
            if (optional && string.IsNullOrWhiteSpace(t))
            {
                MarkInvalid(false);
                tb.BorderBrush = null; ToolTip.SetTip(tb, null);
                set("");
                Changed?.Invoke();
                return;
            }
            if (Units.TryEnter(t, kind, Lookup, out var kept, out var v, out var err))
            {
                MarkInvalid(false);
                tb.BorderBrush = null;
                // (what a formula, or a value in other units, comes to in the document's)
                var plain = Expr.IsLiteral(t, out var u) && (u == "" || kind == ValueKind.Length && u == Units.Symbol() || kind == ValueKind.Angle);
                ToolTip.SetTip(tb, plain ? null : $"= {Units.Show(v)}");
                set(kept);
                Changed?.Invoke();
            }
            else
            {
                MarkInvalid(true);
                tb.BorderBrush = Palette.Danger;
                ToolTip.SetTip(tb, err);
            }
        }
        tb.TextChanged += (_, _) => Validate();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || !Units.TryEnter(tb.Text, kind, Lookup, out string _, out var v, out string? _)) return;
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
            if (e.Key == Key.Down) step = -step;
            // in the units the value is written in (a step of an inch in "2in", of a millimetre in "10mm"), else the document's
            if (kind == ValueKind.Length)
            {
                var unit = Expr.IsLiteral(tb.Text, out var u) && u != "" ? u : Units.Symbol();
                var per = Expr.UnitFactor(unit);
                tb.Text = Expr.Format(v.Value / per + step) + unit;
            }
            else if (kind == ValueKind.Angle) tb.Text = Expr.Format(v.Value + step) + "deg";
            else tb.Text = Expr.Format(v.Value + step);
            e.Handled = true;
        };
        s.Children.Add(Row(label, field, icon));
        return tb;
    }

    /// <summary>A list of whole numbers ("2, 5, 7-9"), each at least <paramref name="min"/>; empty means none.</summary>
    public TextBox IntList(StackPanel s, string label, Func<List<int>> get, Action<List<int>> set, int min, string? tip = null)
    {
        static string Format(List<int> v) => string.Join(", ", v.OrderBy(x => x));
        var tb = new TextBox { Text = Format(get()), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Stretch, Watermark = "none" };
        if (tip != null) ToolTip.SetTip(tb, tip);
        tb.TextChanged += (_, _) =>
        {
            var res = new SortedSet<int>();
            string? err = null;
            foreach (var part in (tb.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var r = part.Split('-');
                if (r.Length == 1 && int.TryParse(r[0], out var n) && n >= min) res.Add(n);
                else if (r.Length == 2 && int.TryParse(r[0], out var a) && int.TryParse(r[1], out var b) && a >= min && b >= a && b - a < 10000)
                    for (int k = a; k <= b; k++) res.Add(k);
                else { err = $"\"{part}\" is not an instance number ({min} or more) or a range such as {min}-{min + 2}"; break; }
            }
            if (err != null) { _invalid[tb] = label; tb.BorderBrush = Palette.Danger; ToolTip.SetTip(tb, err); return; }
            _invalid.Remove(tb); tb.BorderBrush = null; ToolTip.SetTip(tb, tip);
            set(res.ToList());
            Changed?.Invoke();
        };
        s.Children.Add(Row(label, tb, null));
        return tb;
    }

    public CheckBox Check(StackPanel s, string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get(), FontSize = TypeScale.Body };
        cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); Changed?.Invoke(); };
        s.Children.Add(cb);
        return cb;
    }

    public ComboBox Combo<T>(StackPanel s, string label, IList<(T value, string text)> options, Func<T> get, Action<T> set, string? icon = null)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = TypeScale.Body };
        cb.ItemsSource = options.Select(o => o.text).ToList();
        var cur = get();
        cb.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.value, cur)));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(options[cb.SelectedIndex].value); Changed?.Invoke(); } };
        s.Children.Add(Row(label, cb, icon));
        return cb;
    }

    public Button ButtonRow(StackPanel s, string text, Action click, string? icon = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (icon != null) content.Children.Add(Icons.Make(icon, 16));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.Click += (_, _) => click();
        s.Children.Add(b);
        return b;
    }

    public PmSelectionBox Selection(StackPanel s, string label, SelFilter filter, string hint, int max = int.MaxValue, Func<SelItem, bool>? accept = null)
    {
        var box = new PmSelectionBox { Label = label, Filter = filter, Max = max, Accept = accept };
        s.Children.Add(new TextBlock { Text = hint, FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
        box.List.GotFocus += (_, _) => Activate(box);
        box.List.PointerPressed += (_, _) => Activate(box);
        box.List.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Back) { box.Remove(box.List.SelectedIndex); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var clear = new MenuItem { Header = "Clear Selections" };
        clear.Click += (_, _) => box.Clear();
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) => box.Remove(box.List.SelectedIndex);
        menu.Items.Add(del); menu.Items.Add(clear);
        box.List.ContextMenu = menu;
        box.Changed += () => Changed?.Invoke();
        s.Children.Add(box.List);
        Boxes.Add(box);
        if (ActiveBox == null) Activate(box);
        return box;
    }

    public void Activate(PmSelectionBox? box)
    {
        if (ActiveBox == box) return;
        ActiveBox?.List.Classes.Remove("activebox");
        ActiveBox = box;
        box?.List.Classes.Add("activebox");
        ActiveBoxChanged?.Invoke();
    }

    /// <summary>Routes a viewport pick to the active selection box.</summary>
    public bool HandlePick(SelItem s)
    {
        if (ActiveBox == null) return false;
        if (!Matches(ActiveBox.Filter, s))
        {
            // auto switch to another box that accepts this kind
            var other = Boxes.FirstOrDefault(b => Matches(b.Filter, s));
            if (other == null) return false;
            Activate(other);
        }
        return ActiveBox!.Add(s);
    }

    public static bool Matches(SelFilter f, SelItem s) => s.Kind switch
    {
        SelKind.Face => (f & SelFilter.Face) != 0 || ((f & SelFilter.PlanarFace) != 0 && s.Planar) || (f & SelFilter.Feature) != 0,
        SelKind.Edge => (f & SelFilter.Edge) != 0,
        SelKind.Plane => (f & SelFilter.Plane) != 0,
        SelKind.Sketch => (f & SelFilter.Sketch) != 0,
        SelKind.Feature => (f & SelFilter.Feature) != 0,
        SelKind.Body => (f & SelFilter.Body) != 0,
        SelKind.Vertex => s.IsCenter ? (f & SelFilter.Center) != 0 : (f & SelFilter.Vertex) != 0,
        SelKind.SketchSegment => (f & SelFilter.SketchSegment) != 0,
        // (a reference axis or an origin axis also where edges are asked for: a line)
        SelKind.Axis => (f & SelFilter.Axis) != 0 || (s.FeatureId != null || s.IsWorldAxis) && (f & SelFilter.Edge) != 0,
        _ => false,
    };

    public void Content(Control c) => _body.Children.Add(c);
}
