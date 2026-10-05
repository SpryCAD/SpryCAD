using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.App.Session;
using FeatherCad.Core;

namespace FeatherCad.App.UI;

/// <summary>A selection box in a PropertyManager page (pink when active, like SolidWorks).</summary>
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
    public string Title { get; }
    public string Icon { get; }
    public Control View => _root;
    public event Action? Changed;
    public Action? OnOk { get; set; }
    public Action? OnCancel { get; set; }
    public List<PmSelectionBox> Boxes { get; } = new();
    public PmSelectionBox? ActiveBox { get; private set; }
    public event Action? ActiveBoxChanged;
    public Func<string, double?> Lookup { get; set; } = _ => null;
    public string? Message { get => _msg.Text; set { _msg.Text = value; _msgBorder.IsVisible = !string.IsNullOrEmpty(value); } }

    readonly DockPanel _root = new();
    readonly StackPanel _body = new() { Spacing = 0 };
    readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 70, 0)) };
    readonly Border _msgBorder;

    public PmPage(string title, string icon, bool showOkCancel = true)
    {
        Title = title; Icon = icon;
        var header = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(Icons.Make(icon, 22));
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(29, 53, 87)) });
        DockPanel.SetDock(titleRow, Dock.Top);
        header.Children.Add(titleRow);
        if (showOkCancel)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = Icons.Make("ok", 18, new SolidColorBrush(Color.FromRgb(20, 150, 40))), Classes = { "tb" } };
            ToolTip.SetTip(ok, "OK (Enter)");
            ok.Click += (_, _) => OnOk?.Invoke();
            var cancel = new Button { Content = Icons.Make("cancel", 18, new SolidColorBrush(Color.FromRgb(200, 40, 40))), Classes = { "tb" } };
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
            Background = new SolidColorBrush(Color.FromRgb(255, 248, 220)), BorderBrush = new SolidColorBrush(Color.FromRgb(230, 200, 120)),
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
        s.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray });

    static Grid Row(string label, Control field, string? icon = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 1) };
        var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MinWidth = 82, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null) l.Children.Add(Icons.Make(icon, 16));
        l.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        g.Children.Add(l);
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }

    /// <summary>Numeric field that accepts expressions; shows the evaluated value as a tooltip.</summary>
    /// <summary>Length unit shown next to numeric fields (the document's units).</summary>
    public static string LengthUnit { get; set; } = "mm";

    public TextBox Number(StackPanel s, string label, Func<string> get, Action<string> set, string? icon = null, string? unit = null, bool optional = false)
    {
        unit ??= LengthUnit;
        var tb = new TextBox { Text = get(), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Stretch };
        var unitLbl = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Foreground = Brushes.Gray, FontSize = 11 };
        var dp = new DockPanel();
        DockPanel.SetDock(unitLbl, Dock.Right);
        dp.Children.Add(unitLbl); dp.Children.Add(tb);
        void Validate()
        {
            var t = tb.Text ?? "";
            if (optional && string.IsNullOrWhiteSpace(t))
            {
                tb.BorderBrush = null; ToolTip.SetTip(tb, null);
                set("");
                Changed?.Invoke();
                return;
            }
            if (Expr.TryEval(t, Lookup, out var v, out var err))
            {
                tb.BorderBrush = null;
                ToolTip.SetTip(tb, double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? null : $"= {Expr.Format(v)}");
                set(t.Trim());
                Changed?.Invoke();
            }
            else
            {
                tb.BorderBrush = Brushes.Red;
                ToolTip.SetTip(tb, err);
            }
        }
        tb.TextChanged += (_, _) => Validate();
        tb.KeyDown += (_, e) =>
        {
            if ((e.Key is Key.Up or Key.Down) && Expr.TryEval(tb.Text, Lookup, out var v, out string? _))
            {
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
                tb.Text = Expr.Format(v + (e.Key == Key.Up ? step : -step));
                e.Handled = true;
            }
        };
        s.Children.Add(Row(label, dp, icon));
        return tb;
    }

    public CheckBox Check(StackPanel s, string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get(), FontSize = 12 };
        cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); Changed?.Invoke(); };
        s.Children.Add(cb);
        return cb;
    }

    public ComboBox Combo<T>(StackPanel s, string label, IList<(T value, string text)> options, Func<T> get, Action<T> set, string? icon = null)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
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
        s.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
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
        _ => false,
    };

    public void Content(Control c) => _body.Children.Add(c);
}
