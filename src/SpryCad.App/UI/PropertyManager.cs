using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    /// <summary>The list: several of its items can be selected (Ctrl/Shift-click, Shift+arrows, Ctrl+A) to remove at once.</summary>
    public ListBox List { get; } = new() { Classes = { "selbox" }, SelectionMode = SelectionMode.Multiple };
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

    /// <summary>
    /// Removes the items selected in the list, and selects the one that took the first's place (the one below), or the
    /// one above when the last was removed; it has the keyboard focus, so Delete again removes it.
    /// </summary>
    public void RemoveSelected()
    {
        var picked = List.Selection.SelectedIndexes.Where(i => i >= 0 && i < Items.Count).Distinct().OrderByDescending(i => i).ToList();
        if (picked.Count == 0) return;
        var hadFocus = List.IsKeyboardFocusWithin;
        foreach (var i in picked) Items.RemoveAt(i);
        Refresh();
        var next = Math.Min(picked[^1], Items.Count - 1);
        if (next >= 0) List.SelectedIndex = next;
        Changed?.Invoke();
        // (the list's rows are made anew: the focus, on a row now gone, is given to the one selected once it is made)
        if (hadFocus)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (next >= 0 && next < Items.Count) List.SelectedIndex = next;
                if (next >= 0 && List.ContainerFromIndex(next) is { } row) row.Focus(NavigationMethod.Directional);
                else List.Focus();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

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
    /// <summary>
    /// Label of the first field holding an invalid value (OK must not commit the last valid one silently). A field the
    /// page hides (an Angle row while the relation is a distance) does not count: it is not what OK commits.
    /// </summary>
    public string? FirstInvalidField => _invalid.Where(kv => Shown(kv.Key)).Select(kv => kv.Value).FirstOrDefault();
    public List<PmSelectionBox> Boxes { get; } = new();
    public PmSelectionBox? ActiveBox { get; private set; }
    public event Action? ActiveBoxChanged;
    public Func<string, Quantity?> Lookup { get; set; } = _ => null;
    public string? Message
    {
        get => _msg.Text;
        set { _msg.Text = value; _msgBorder.IsVisible = !string.IsNullOrEmpty(value); MessageChanged?.Invoke(); }
    }
    public event Action? MessageChanged;

    readonly DockPanel _root = new();
    readonly StackPanel _body = new() { Spacing = 0 };
    readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Palette.NoteText };
    readonly Border _msgBorder;

    public PmPage(string title, string icon, bool showOkCancel = true)
    {
        Title = title; Icon = icon;
        var header = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
        // (a long title cut short in a narrow panel, not clipped)
        var titleRow = new DockPanel();
        var titleIcon = Icons.Make(icon, 22);
        titleIcon.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(titleIcon, Dock.Left);
        titleRow.Children.Add(titleIcon);
        var titleText = Labels.Trimmed(title, TypeScale.Title);
        titleText.FontWeight = FontWeight.SemiBold; titleText.Foreground = Palette.Heading;
        titleRow.Children.Add(titleText);
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
        // the page's message (a feature's error, a warning, a hint): beside the page over the graphics area, as a tooltip
        // is, not above the fields (a long one moved every field down); shown while the page is on screen, and clicks
        // go through it to the model
        _msgBorder = new Border
        {
            Background = Palette.NoteBg, BorderBrush = Palette.NoteBorder,
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(3),
            MaxWidth = 300, Child = _msg, IsVisible = false, IsHitTestVisible = false,
        };
        var msgPopup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = header, Placement = PlacementMode.RightEdgeAlignedTop, HorizontalOffset = 8, VerticalOffset = 4,
            ShouldUseOverlayLayer = true, IsHitTestVisible = false, Child = _msgBorder,
        };
        header.Children.Add(msgPopup);
        void SyncMessage()
        {
            var open = _msgBorder.IsVisible && _root.GetVisualRoot() != null && _root.IsEffectivelyVisible;
            if (msgPopup.IsOpen != open) msgPopup.IsOpen = open;
        }
        _msgBorder.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) SyncMessage(); };
        _root.AttachedToVisualTree += (_, _) => SyncMessage();
        _root.DetachedFromVisualTree += (_, _) => msgPopup.IsOpen = false;
        _root.LayoutUpdated += (_, _) => SyncMessage(); // (the page hidden or shown again with the panel)
        _root.Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }

    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// Whether the page shows <paramref name="c"/>: nothing between it and the page is hidden (whether the page itself
    /// is on screen does not matter).
    /// </summary>
    bool Shown(Control c)
    {
        for (Control? x = c; x != null && x != _root; x = x.Parent as Control)
            if (!x.IsVisible) return false;
        return true;
    }

    /// <summary>
    /// A field's row in its section (the field with its label, or the field alone): what to hide to hide the field.
    /// Found by what it is in, not by counting levels up (a field wrapped with its unit is a level deeper).
    /// </summary>
    public static Control RowOf(Control field)
    {
        var c = field;
        while (c.Parent is Control p && !(p.Parent is Border b && b.Classes.Contains("pmsection"))) c = p;
        return c;
    }

    // the page's value fields, the one typed in last, and the one shown as the failure's (its tooltip before)
    readonly List<TextBox> _numbers = new();
    TextBox? _lastEdited, _failed;
    object? _failedTip;

    /// <summary>
    /// The feature fails as the page has it (<paramref name="error"/>; null: it builds): the value typed in last is
    /// shown red, the reason in its tooltip (none is when no value was typed: the message above says why).
    /// </summary>
    public void ShowFailure(string? error)
    {
        var target = error == null ? null : _lastEdited;
        if (_failed != null && _failed != target)
        {
            _failed.Classes.Set("failed", false);
            ToolTip.SetTip(_failed, _failedTip);
            _failed = null;
        }
        if (target == null) return;
        if (_failed != target) { _failedTip = ToolTip.GetTip(target); _failed = target; }
        target.Classes.Set("failed", true);
        ToolTip.SetTip(target, error);
    }

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
        // the label at most 130 wide, wrapping: a long one never pushes the field off the page
        var l = new DockPanel { MinWidth = 82, MaxWidth = 130, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
        {
            var ic = Icons.Make(icon, 16);
            ic.Margin = new Thickness(0, 0, 4, 0);
            ic.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(ic, Dock.Left);
            l.Children.Add(ic);
        }
        l.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body, TextWrapping = TextWrapping.Wrap });
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
            // a hidden field neither writes nor is wrong: two fields can share a value (a plane reference's Distance
            // and Angle), and the hidden one would write it back as its own kind ("Wall" as "(Wall)deg") when the page
            // opens, since TextChanged fires late for each field's first text
            if (!Shown(tb)) { MarkInvalid(false); tb.Classes.Set("invalid", false); return; }
            var t = tb.Text ?? "";
            if (optional && string.IsNullOrWhiteSpace(t))
            {
                MarkInvalid(false);
                tb.Classes.Set("invalid", false); ToolTip.SetTip(tb, null);
                set("");
                Changed?.Invoke();
                return;
            }
            if (Units.TryEnter(t, kind, Lookup, out var kept, out var v, out var err))
            {
                MarkInvalid(false);
                tb.Classes.Set("invalid", false);
                // (what a formula, or a value in other units, comes to in the document's)
                var plain = Expr.IsLiteral(t, out var u) && (u == "" || kind == ValueKind.Length && u == Units.Symbol() || kind == ValueKind.Angle);
                ToolTip.SetTip(tb, plain ? null : $"= {Units.Show(v)}");
                set(kept);
                Changed?.Invoke();
            }
            else
            {
                MarkInvalid(true);
                tb.Classes.Set("invalid", true);
                ToolTip.SetTip(tb, err);
            }
        }
        _numbers.Add(tb);
        tb.TextChanged += (_, _) => { _lastEdited = tb; Validate(); };
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
                else { err = $"\"{part}\" is not a valid instance. Enter a number of {min} or more, or a range such as {min}-{min + 2}."; break; }
            }
            if (err != null) { _invalid[tb] = label; tb.Classes.Set("invalid", true); ToolTip.SetTip(tb, err); return; }
            _invalid.Remove(tb); tb.Classes.Set("invalid", false); ToolTip.SetTip(tb, tip);
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
            if (e.Key is Key.Delete or Key.Back) { box.RemoveSelected(); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var clear = new MenuItem { Header = "Clear Selections" };
        clear.Click += (_, _) => box.Clear();
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) => box.RemoveSelected();
        menu.Items.Add(del); menu.Items.Add(clear);
        box.List.ContextMenu = menu;
        box.Changed += () => Changed?.Invoke();
        // a fixed height (5 rows; as many as it holds when fewer), scrolling past it: adding or removing items never
        // moves what is below. The grip under it resizes it (kept for boxes of that name while the app runs).
        var rows = Math.Min(max, 5);
        box.List.MinHeight = BoxRow + 4;
        box.List.MaxHeight = double.PositiveInfinity;
        box.List.Height = _boxHeights.TryGetValue(label, out var kept) ? kept : rows * BoxRow + 4;
        ScrollViewer.SetVerticalScrollBarVisibility(box.List, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        s.Children.Add(box.List);
        if (max > 1) s.Children.Add(Grip(box.List, label));
        Boxes.Add(box);
        if (ActiveBox == null) Activate(box);
        return box;
    }

    // a selection box's row height, and the heights boxes were dragged to (by their label)
    const double BoxRow = 21;
    static readonly Dictionary<string, double> _boxHeights = new();

    /// <summary>The grip under a selection box: dragged up or down, the box's height (a row at least).</summary>
    static Control Grip(ListBox list, string label)
    {
        var grip = new Border
        {
            Height = 8, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new Border { Width = 28, Height = 2, CornerRadius = new CornerRadius(1), Background = Palette.FieldBorder, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTip.SetTip(grip, "Drag to resize the list");
        double startY = 0, startH = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragging = true; startY = e.GetPosition(null).Y; startH = list.Bounds.Height;
            e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            list.Height = _boxHeights[label] = Math.Clamp(startH + e.GetPosition(null).Y - startY, BoxRow + 4, 600);
        };
        grip.PointerReleased += (_, e) => { dragging = false; e.Pointer.Capture(null); };
        return grip;
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
