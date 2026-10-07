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
    readonly HashSet<Border> _childRows = new();
    string? _selectedId;

    // feature reorder drag: the pressed row, then the drop line drawn over the rows while moving
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly Border _dropLine = new() { Height = 2, IsVisible = false, Background = new SolidColorBrush(Color.FromRgb(40, 110, 210)) };
    string? _pressId;
    Point _pressAt;
    bool _moving;

    public event Action<string?>? SelectedChanged;        // feature id, or "Front"/"Top"/"Right"/"Origin"
    public event Action<string>? EditRequested;
    public event Action<string, Point, Control>? ContextRequested;
    public event Action<string>? HoverChanged;
    public event Action<int?>? RollbackRequested;
    /// <summary>Dragged feature id, target feature id (or "__rollback" for the bar), drop after the target.</summary>
    public event Action<string, string, bool>? MoveRequested;

    public FeatureTree()
    {
        _overlay.Children.Add(_dropLine);
        var layers = new Grid { Children = { _rows, _overlay } };
        Content = new ScrollViewer { Content = layers, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(2, 4) };
        // a hidden tree (a feature being edited) hovers nothing
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !IsVisible) SetHover(null); };
    }

    // the row under the pointer, highlighted in the graphics area. Kept here because a row that is removed (the
    // tree rebuilt) or hidden under the pointer gets no "pointer left": the highlight would stay on the model.
    string? _hoverId;

    void SetHover(string? id)
    {
        if (_hoverId == id) return;
        _hoverId = id;
        HoverChanged?.Invoke(id ?? "");
    }

    public string? SelectedId => _selectedId;

    string? _relatedId;

    /// <summary>Lightly highlights the feature owning the current graphics selection (does not select it).</summary>
    public void Relate(string? id)
    {
        if (_relatedId != null && _rowById.TryGetValue(_relatedId, out var old)) old.Classes.Remove("related");
        _relatedId = id;
        if (id != null && _rowById.TryGetValue(id, out var b)) b.Classes.Add("related");
    }

    public void Select(string? id)
    {
        if (_selectedId != null && _rowById.TryGetValue(_selectedId, out var old)) old.Classes.Remove("selected");
        _selectedId = id;
        if (id != null && _rowById.TryGetValue(id, out var b)) b.Classes.Add("selected");
    }

    public void Rebuild(CadDocument doc, string title, RegenResult? result)
    {
        SetHover(null); // the hovered row is going away
        _rows.Children.Clear();
        _rowById.Clear();
        _childRows.Clear();
        _pressId = null; _moving = false; _dropLine.IsVisible = false;
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
            var shown = status;
            if (children.Count > 0 && !_expanded.Contains(f.Id) && status is not { Ok: false })
                foreach (var ch in children)
                    if (result?.Status.GetValueOrDefault(ch.Id) is { Ok: false } cs)
                        shown = new FeatureStatus { Error = $"{ch.Name}: {cs.Error}", Milliseconds = status?.Milliseconds ?? 0 };
            AddRow(f.Id, Label(f, result), IconFor(f), 1, shown, suppressed: f.Suppressed || rolledBack, hasChildren: children.Count > 0);
            if (children.Count > 0 && _expanded.Contains(f.Id))
                foreach (var ch in children)
                    _childRows.Add(AddRow(ch.Id, Label(ch, result), IconFor(ch), 2, result?.Status.GetValueOrDefault(ch.Id), suppressed: ch.Suppressed || rolledBack));
        }
        if (active >= doc.Features.Count) AddRollbackBar();
        Select(_selectedId);
        Relate(_relatedId);
    }

    /// <summary>Feature name; sketches get SolidWorks' "(-)" under-defined / "(+)" over-defined prefix.</summary>
    static string Label(Feature f, RegenResult? result)
    {
        if (f is SketchFeature && result?.State.Sketches.GetValueOrDefault(f.Id)?.Solve is { } sr)
            return sr.Status switch
            {
                Core.Sketching.SketchStatus.UnderDefined => "(-) " + f.Name,
                Core.Sketching.SketchStatus.OverDefined or Core.Sketching.SketchStatus.Failed => "(+) " + f.Name,
                _ => f.Name,
            };
        return f.Name;
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
        FlatPatternFeature => "flatpattern",
        _ => "part",
    };

    void AddRollbackBar()
    {
        var bar = new Border
        {
            Height = 6, Margin = new Thickness(14, 3, 8, 3), CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(40, 110, 210)),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Tag = "__rollback",
        };
        ToolTip.SetTip(bar, "Rollback bar: drag up or down to roll the model back.");
        bar.ZIndex = 1;
        var shift = new TranslateTransform();
        bar.RenderTransform = shift;
        bool dragging = false;
        void EndDrag() { dragging = false; shift.Y = 0; bar.Opacity = 1; }
        bar.PointerPressed += (_, e) => { dragging = true; bar.Opacity = 0.8; e.Pointer.Capture(bar); e.Handled = true; };
        // while dragging, the bar snaps to the gap it would drop into
        bar.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var (_, _, gapY) = RollbackSlot(e.GetPosition(_rows).Y, bar);
            shift.Y = gapY - bar.Bounds.Center.Y;
        };
        bar.PointerCaptureLost += (_, _) => EndDrag();
        bar.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            var (target, after, _) = RollbackSlot(e.GetPosition(_rows).Y, bar);
            e.Pointer.Capture(null);
            EndDrag();
            RollbackRequested?.Invoke(target == null ? null : -1);
            if (target != null) RollbackTo?.Invoke(target, after);
        };
        _rows.Children.Add(bar);
    }

    /// <summary>The feature row a rollback bar dropped at <paramref name="y"/> lands next to (null = end of tree),
    /// and the Y of that gap; the bar's own gap reports its current center so it doesn't jump.</summary>
    (string? target, bool after, double gapY) RollbackSlot(double y, Control bar)
    {
        var kids = _rows.Children;
        int barAt = kids.IndexOf(bar);
        Border? last = null;
        for (int i = 0; i < kids.Count; i++)
        {
            if (kids[i] is not Border b || b.Tag is not string id || id.StartsWith("__") || id is "Origin" or "Front" or "Top" or "Right") continue;
            last = b;
            if (y < b.Bounds.Bottom)
            {
                var after = y > b.Bounds.Center.Y;
                var unmoved = after ? i == barAt - 1 : i == barAt + 1;
                return (id, after, unmoved ? bar.Bounds.Center.Y : after ? b.Bounds.Bottom : b.Bounds.Top);
            }
        }
        var atEnd = last == null || barAt == kids.Count - 1;
        return (null, false, atEnd ? bar.Bounds.Center.Y : last!.Bounds.Bottom);
    }

    public event Action<string, bool>? RollbackTo;

    static bool IsFeatureRow(string id) => !id.StartsWith("__") && id is not ("Origin" or "Front" or "Top" or "Right");

    /// <summary>
    /// Where a feature dragged to <paramref name="y"/> drops: before/after a feature (a nested sketch row counts as
    /// part of its parent's block) or the rollback bar, and the Y to draw the drop line at.
    /// </summary>
    (string target, bool after, double lineY)? MoveSlot(double y)
    {
        var blocks = new List<(string id, double top, double bottom)>();
        foreach (var c in _rows.Children)
        {
            if (c is not Border b || b.Tag is not string id || !(IsFeatureRow(id) || id == "__rollback")) continue;
            if (_childRows.Contains(b) && blocks.Count > 0) blocks[^1] = (blocks[^1].id, blocks[^1].top, b.Bounds.Bottom);
            else blocks.Add((id, b.Bounds.Top, b.Bounds.Bottom));
        }
        if (blocks.Count == 0) return null;
        foreach (var (id, top, bottom) in blocks)
            if (y < bottom)
            {
                var after = y > (top + bottom) / 2;
                return (id, after, after ? bottom : top);
            }
        return (blocks[^1].id, true, blocks[^1].bottom);
    }

    void EndMove(Control row)
    {
        _pressId = null; _moving = false;
        row.Opacity = 1;
        _dropLine.IsVisible = false;
    }

    Border AddRow(string id, string text, string icon, int indent, FeatureStatus? status, bool isHeader = false, bool suppressed = false, bool hasChildren = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(indent * 14, 0, 0, 0) };
        if (hasChildren)
        {
            // an 18x18 hit area (transparent background so the whole square takes clicks), larger glyph, hover cue
            var exp = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = _expanded.Contains(id) ? "▾" : "▸", FontSize = 14, Foreground = Brushes.DimGray,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(exp, _expanded.Contains(id) ? "Collapse" : "Expand");
            exp.PointerEntered += (_, _) => exp.Background = new SolidColorBrush(Color.FromArgb(40, 0, 90, 200));
            exp.PointerExited += (_, _) => exp.Background = Brushes.Transparent;
            exp.PointerPressed += (_, e) =>
            {
                if (!_expanded.Add(id)) _expanded.Remove(id);
                e.Handled = true;
                ExpandToggled?.Invoke();
            };
            sp.Children.Add(exp);
        }
        else sp.Children.Add(new Control { Width = 18 });
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
        if (status is { Ok: false }) ToolTip.SetTip(row, status.Error);
        else if (status is { Warnings.Count: > 0 }) ToolTip.SetTip(row, string.Join("\n", status.Warnings));
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
            if (IsFeatureRow(id)) { _pressId = id; _pressAt = e.GetPosition(_rows); }
            Select(id);
            SelectedChanged?.Invoke(id);
            e.Handled = true;
        };
        // dragging a feature row reorders it; a drop line shows where it will go
        row.PointerMoved += (_, e) =>
        {
            if (_pressId != id) return;
            var p = e.GetPosition(_rows);
            if (!_moving)
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { _pressId = null; return; }
                if (Math.Abs(p.Y - _pressAt.Y) < 5) return;
                e.Pointer.Capture(row);   // before _moving: a capture lost by a child element must not end the drag
                _moving = true;
                row.Opacity = 0.5;
            }
            var s = MoveSlot(p.Y);
            _dropLine.IsVisible = s is { } v && v.target != id;
            if (s is { } m)
            {
                Canvas.SetLeft(_dropLine, 14);
                Canvas.SetTop(_dropLine, m.lineY - 1);
                _dropLine.Width = Math.Max(0, _rows.Bounds.Width - 22);
            }
        };
        row.PointerReleased += (_, e) =>
        {
            if (_pressId != id) return;
            var moving = _moving;
            var s = MoveSlot(e.GetPosition(_rows).Y);
            if (moving) e.Pointer.Capture(null);
            EndMove(row);
            if (moving && s is { } m && m.target != id) MoveRequested?.Invoke(id, m.target, m.after);
        };
        row.PointerCaptureLost += (_, _) => { if (_pressId == id && _moving) EndMove(row); };
        row.PointerEntered += (_, _) => SetHover(id);
        row.PointerExited += (_, _) => SetHover(null);
        _rowById[id] = row;
        _rows.Children.Add(row);
        return row;
    }

    public event Action? ExpandToggled;
}
