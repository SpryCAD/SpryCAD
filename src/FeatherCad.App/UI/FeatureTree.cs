using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;

namespace FeatherCad.App.UI;

/// <summary>FeatureManager design tree: part, origin, planes, features (sketches nested under their consumers), rollback bar.</summary>
public sealed class FeatureTree : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 0 };
    readonly HashSet<string> _expanded = new();
    readonly Dictionary<string, Border> _rowById = new();
    string? _selectedId;

    public event Action<string?>? SelectedChanged;        // feature id, or "Front"/"Top"/"Right"/"Origin"
    public event Action<string>? EditRequested;
    public event Action<string, Point, Control>? ContextRequested;
    public event Action<string>? HoverChanged;
    public event Action<int?>? RollbackRequested;

    public FeatureTree()
    {
        Content = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(2, 4) };
    }

    public string? SelectedId => _selectedId;

    public void Select(string? id)
    {
        if (_selectedId != null && _rowById.TryGetValue(_selectedId, out var old)) old.Classes.Remove("selected");
        _selectedId = id;
        if (id != null && _rowById.TryGetValue(id, out var b)) b.Classes.Add("selected");
    }

    public void Rebuild(CadDocument doc, string title, RegenResult? result)
    {
        _rows.Children.Clear();
        _rowById.Clear();
        AddRow("__part", title, "part", 0, null, isHeader: true);
        AddRow("Origin", "Origin", "origin", 1, null);
        AddRow("Front", "Front Plane", "plane", 1, null);
        AddRow("Top", "Top Plane", "plane", 1, null);
        AddRow("Right", "Right Plane", "plane", 1, null);

        // sketches consumed by a later feature are shown as its children
        var consumedBy = new Dictionary<string, string>();
        foreach (var f in doc.Features)
            foreach (var c in f.Consumes)
                if (!string.IsNullOrEmpty(c)) consumedBy[c] = f.Id;

        var active = doc.ActiveCount;
        for (int i = 0; i < doc.Features.Count; i++)
        {
            if (i == active && active < doc.Features.Count) AddRollbackBar();
            var f = doc.Features[i];
            if (consumedBy.ContainsKey(f.Id) && doc.IndexOf(consumedBy[f.Id]) > i) continue;
            var status = result?.Status.GetValueOrDefault(f.Id);
            var rolledBack = i >= active;
            var children = f.Consumes.Where(c => !string.IsNullOrEmpty(c)).Select(doc.Find).OfType<Feature>().ToList();
            AddRow(f.Id, f.Name, IconFor(f), 1, status, suppressed: f.Suppressed || rolledBack, hasChildren: children.Count > 0);
            if (children.Count > 0 && _expanded.Contains(f.Id))
                foreach (var ch in children)
                    AddRow(ch.Id, ch.Name, IconFor(ch), 2, result?.Status.GetValueOrDefault(ch.Id), suppressed: ch.Suppressed || rolledBack);
        }
        if (active >= doc.Features.Count) AddRollbackBar();
        Select(_selectedId);
    }

    static string IconFor(Feature f) => f switch
    {
        SketchFeature => "sketch",
        ExtrudeFeature e => e.IsCut ? "cut" : "extrude",
        RevolveFeature r => r.IsCut ? "cutrevolve" : "revolve",
        FilletFeature => "fillet",
        ChamferFeature => "chamfer",
        HoleFeature => "hole",
        ShellFeature => "shell",
        LinearPatternFeature => "lpattern",
        CircularPatternFeature => "cpattern",
        MirrorFeature => "mirror",
        PlaneFeature => "plane",
        CombineFeature => "combine",
        _ => "part",
    };

    void AddRollbackBar()
    {
        var bar = new Border
        {
            Height = 6, Margin = new Thickness(14, 3, 8, 3), CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(40, 110, 210)),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        ToolTip.SetTip(bar, "Rollback bar: drag up or down to roll the model back.");
        bool dragging = false;
        bar.PointerPressed += (_, e) => { dragging = true; e.Pointer.Capture(bar); e.Handled = true; };
        bar.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; e.Pointer.Capture(null);
            // find the feature row under the pointer
            var p = e.GetPosition(_rows);
            string? target = null; bool after = false;
            foreach (var child in _rows.Children)
            {
                if (child is not Border b || b.Tag is not string id || id.StartsWith("__") || id is "Origin" or "Front" or "Top" or "Right") continue;
                if (p.Y < b.Bounds.Bottom) { target = id; after = p.Y > b.Bounds.Center.Y; break; }
            }
            RollbackRequested?.Invoke(target == null ? null : -1);
            if (target != null) RollbackTo?.Invoke(target, after);
        };
        _rows.Children.Add(bar);
    }

    public event Action<string, bool>? RollbackTo;

    void AddRow(string id, string text, string icon, int indent, FeatureStatus? status, bool isHeader = false, bool suppressed = false, bool hasChildren = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(indent * 14, 0, 0, 0) };
        if (hasChildren)
        {
            var exp = new TextBlock { Text = _expanded.Contains(id) ? "▾" : "▸", Width = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Cursor = new Cursor(StandardCursorType.Hand) };
            exp.PointerPressed += (_, e) =>
            {
                if (!_expanded.Add(id)) _expanded.Remove(id);
                e.Handled = true;
                ExpandToggled?.Invoke();
            };
            sp.Children.Add(exp);
        }
        else sp.Children.Add(new Control { Width = 10 });
        sp.Children.Add(Icons.Make(icon, 16));
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        if (isHeader) tb.FontWeight = FontWeight.SemiBold;
        if (suppressed) tb.Foreground = Brushes.Gray;
        if (status is { Ok: false }) { tb.Foreground = Brushes.Red; ToolTip.SetTip(tb, status.Error); }
        else if (status is { Warnings.Count: > 0 }) { ToolTip.SetTip(tb, string.Join("\n", status.Warnings)); }
        sp.Children.Add(tb);
        if (status is { Ok: false }) sp.Children.Add(new TextBlock { Text = "✖", Foreground = Brushes.Red, VerticalAlignment = VerticalAlignment.Center });
        else if (status is { Warnings.Count: > 0 }) sp.Children.Add(new TextBlock { Text = "⚠", Foreground = Brushes.DarkOrange, VerticalAlignment = VerticalAlignment.Center });
        if (status != null && status.Milliseconds > 200) sp.Children.Add(new TextBlock { Text = $"{status.Milliseconds:0} ms", Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });

        var row = new Border { Classes = { "treerow" }, Child = sp, Tag = id };
        row.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(row);
            if (pt.Properties.IsRightButtonPressed)
            {
                Select(id); SelectedChanged?.Invoke(id);
                ContextRequested?.Invoke(id, e.GetPosition(this), row);
                e.Handled = true;
                return;
            }
            if (e.ClickCount >= 2) { EditRequested?.Invoke(id); e.Handled = true; return; }
            Select(id);
            SelectedChanged?.Invoke(id);
            e.Handled = true;
        };
        row.PointerEntered += (_, _) => HoverChanged?.Invoke(id);
        row.PointerExited += (_, _) => HoverChanged?.Invoke("");
        _rowById[id] = row;
        _rows.Children.Add(row);
    }

    public event Action? ExpandToggled;
}
