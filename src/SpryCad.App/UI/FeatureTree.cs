using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.App.UI;

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
    readonly Border _dropLine = new() { Height = 2, IsVisible = false, Background = Palette.Accent };
    string? _pressId;
    Point _pressAt;
    bool _moving;

    public event Action<string?>? SelectedChanged;        // feature id, or an origin row: "Origin", "Front"/"Top"/"Right", "AxisX"/"AxisY"/"AxisZ", "OriginPoint"
    public event Action<string>? EditRequested;
    public event Action<string, Point, Control>? ContextRequested;
    public event Action<string>? HoverChanged;

    /// <summary>Color by Feature: a swatch of each feature's color on its row (the legend), by id; null: none.</summary>
    public IReadOnlyDictionary<string, Color>? Swatches { get; set; }
    public event Action<int?>? RollbackRequested;
    /// <summary>Dragged feature id, target feature id (or "__rollback" for the bar), drop after the target.</summary>
    public event Action<string, string, bool>? MoveRequested;
    /// <summary>A sketch's eye was clicked: show it if hidden, hide it if shown.</summary>
    public event Action<string>? SketchVisibilityToggled;

    /// <summary>
    /// The flyout copy over the graphics area (MainWindow.FlyoutTree.cs), for picking from the tree while the panel shows
    /// something else: closed to the part's row until its arrow opens it, and only selects and hovers (no editing,
    /// reordering, rollback, menus or sketch eyes, which would cut across the command open in the panel).
    /// </summary>
    public bool Flyout { get; init; }

    /// <summary>The flyout opened (its part row's arrow).</summary>
    public bool FlyoutOpen => _expanded.Contains("__part");

    /// <summary>Closes the flyout back to the part's row (shown again next time closed).</summary>
    public void CloseFlyout() => _expanded.Remove("__part");

    (CadDocument doc, string title, RegenResult? result)? _last;

    public FeatureTree()
    {
        _overlay.Children.Add(_dropLine);
        // the room around the rows on the rows (a scroll viewer's own padding cut the last row short)
        var layers = new Grid { Children = { _rows, _overlay }, Margin = new Thickness(2, 4) };
        Content = new ScrollViewer { Content = layers, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
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
        _last = (doc, title, result);
        SetHover(null); // the hovered row is going away
        _rows.Children.Clear();
        _rowById.Clear();
        _childRows.Clear();
        _pressId = null; _moving = false; _dropLine.IsVisible = false;
        AddRow("__part", title, "part", 0, null, isHeader: true, hasChildren: Flyout);
        if (Flyout && !FlyoutOpen) { Select(_selectedId); return; }
        // the origin: a folder of the three planes, the three axes and the origin point
        AddRow("Origin", "Origin", "folder", 1, null, hasChildren: true);
        if (_expanded.Contains("Origin"))
        {
            AddRow("Front", "Front Plane", "plane", 2, null);
            AddRow("Top", "Top Plane", "plane", 2, null);
            AddRow("Right", "Right Plane", "plane", 2, null);
            AddRow("AxisX", "X Axis", "axis", 2, null);
            AddRow("AxisY", "Y Axis", "axis", 2, null);
            AddRow("AxisZ", "Z Axis", "axis", 2, null);
            AddRow("OriginPoint", "Origin Point", "origin", 2, null);
        }

        // sketches consumed by a later feature are shown as its children
        var consumedBy = new Dictionary<string, string>();
        foreach (var f in doc.Features)
            foreach (var c in f.Consumes)
                if (!string.IsNullOrEmpty(c)) consumedBy[c] = f.Id;

        // sketches, reference planes and axes: an eye shows whether each is shown in the graphics area (as set, else while unused)
        var consumedActive = doc.UsedByActive();
        bool? ShownOf(Feature f) => f is SketchFeature or PlaneFeature or AxisFeature ? doc.SketchShown(f.Id, consumedActive.Contains(f.Id)) : null;

        // suppressed by the user, by a Suppress When condition, or with a suppressed feature used
        bool Off(Feature f) => f.Suppressed || result?.Status.GetValueOrDefault(f.Id)?.Suppressed == true;
        string? ConditionTip(Feature f)
        {
            var st = result?.Status.GetValueOrDefault(f.Id);
            var cond = string.IsNullOrWhiteSpace(f.SuppressWhen) ? null : $"Suppressed when {f.SuppressWhen}";
            if (f.Suppressed) return cond == null ? null : cond + " (suppressed by hand now)";
            if (st is { Suppressed: true, SuppressedBy: "condition" }) return cond + ": it holds, so suppressed.";
            if (st is { Suppressed: true, SuppressedBy: { } by }) return $"Suppressed with {by}, which it uses." + (cond != null ? $"\n{cond}." : "");
            return cond == null ? null : cond + ": it does not hold now.";
        }

        var active = doc.ActiveCount;
        for (int i = 0; i < doc.Features.Count; i++)
        {
            if (i == active && active < doc.Features.Count && !Flyout) AddRollbackBar();
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
            AddRow(f.Id, Label(f, result), IconFor(f), 1, shown, suppressed: Off(f) || rolledBack, hasChildren: children.Count > 0,
                sketchShown: rolledBack ? null : ShownOf(f), condition: ConditionTip(f));
            if (children.Count > 0 && _expanded.Contains(f.Id))
                foreach (var ch in children)
                    _childRows.Add(AddRow(ch.Id, Label(ch, result), IconFor(ch), 2, result?.Status.GetValueOrDefault(ch.Id), suppressed: Off(ch) || rolledBack,
                        sketchShown: rolledBack ? null : ShownOf(ch), condition: ConditionTip(ch)));
        }
        if (active >= doc.Features.Count && !Flyout) AddRollbackBar();
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

    internal static string IconFor(Feature f) => f switch
    {
        SketchFeature => "sketch",
        ExtrudeFeature e => e.IsCut ? "cut" : "extrude",
        RevolveFeature r => r.IsCut ? "cutrevolve" : "revolve",
        SweepFeature s => s.IsCut ? "cutsweep" : "sweep",
        LoftFeature l => l.IsCut ? "cutloft" : "loft",
        HelicalSweepFeature h => h.IsCut ? "cuthelical" : "helical",
        ThreadFeature => "thread",
        FilletFeature => "fillet",
        ChamferFeature => "chamfer",
        HoleFeature => "hole",
        ShellFeature => "shell",
        DraftFeature => "draft",
        LinearPatternFeature => "lpattern",
        CircularPatternFeature => "cpattern",
        MirrorFeature => "mirror",
        PlaneFeature => "plane",
        AxisFeature => "axis",
        CombineFeature => "combine",
        FlatPatternFeature => "flatpattern",
        _ => "part",
    };

    static Cursor? _rollbackCursor;
    static Cursor RollbackCursor => _rollbackCursor ??= new Cursor(StandardCursorType.SizeNorthSouth);

    void AddRollbackBar()
    {
        // a thin line, in a taller clear strip that takes the mouse: as easy to grab as a thick bar, lighter to look at
        var bar = new Border
        {
            Height = 10, Margin = new Thickness(14, 1, 8, 1), Background = Brushes.Transparent,
            Child = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Palette.Accent, VerticalAlignment = VerticalAlignment.Center },
            Cursor = RollbackCursor, Tag = "__rollback",
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
            if (kids[i] is not Border b || b.Tag is not string id || id.StartsWith("__") || OriginRows.Contains(id)) continue;
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

    static bool IsFeatureRow(string id) => !id.StartsWith("__") && !OriginRows.Contains(id);

    /// <summary>The origin folder and its rows (no features: not moved, not rolled back to).</summary>
    public static readonly HashSet<string> OriginRows = ["Origin", "Front", "Top", "Right", "AxisX", "AxisY", "AxisZ", "OriginPoint"];

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

    // the eye of a sketch row: open (shown in the graphics area), or closed with a slash (hidden)
    static readonly Geometry EyeOpen = Geometry.Parse("M2 12C5 7 8.5 5 12 5s7 2 10 7c-3 5-6.5 7-10 7s-7-2-10-7z M12 9a3 3 0 1 0 0.01 0z");
    static readonly Geometry EyeShut = Geometry.Parse("M2 12C5 7 8.5 5 12 5s7 2 10 7c-3 5-6.5 7-10 7s-7-2-10-7z M4 4l16 16");

    /// <summary>A sketch row's show/hide button (clicking it toggles, without selecting the row).</summary>
    Control EyeButton(string id, bool shown)
    {
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = shown ? EyeOpen : EyeShut, Stroke = shown ? Palette.Text : Palette.TextDisabled,
            StrokeThickness = 1.6, Stretch = Stretch.Uniform, Width = 14, Height = 14,
        };
        var b = new Border
        {
            Child = glyph, Padding = new Thickness(2), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(2, 0, 0, 0),
            Opacity = shown ? 1 : 0.7,
        };
        ToolTip.SetTip(b, shown ? "Hide the sketch" : "Show the sketch (e.g. to use it again in another feature)");
        b.PointerEntered += (_, _) => b.Background = Palette.SelectedBg;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            e.Handled = true; // not a row press (no selection, no drag)
            SketchVisibilityToggled?.Invoke(id);
        };
        return b;
    }

    Border AddRow(string id, string text, string icon, int indent, FeatureStatus? status, bool isHeader = false, bool suppressed = false, bool hasChildren = false,
        bool? sketchShown = null, string? condition = null)
    {
        // as the assembly's tree (UI/TreeRow.cs): as wide as the tree, a long name trimmed, the markers after it kept
        var sp = new TreeRowPanel { Margin = new Thickness(indent * 14, 0, 0, 0) };
        sp.Children.Add(hasChildren
            ? TreeRow.Expander(_expanded.Contains(id), () => ToggleExpanded(id))
            : TreeRow.Spacer());
        sp.Children.Add(Icons.Make(icon, 16));
        if (Swatches != null && Swatches.TryGetValue(id, out var swatch))
            sp.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(swatch),
                BorderBrush = Palette.TextMuted, BorderThickness = new Thickness(0.5), VerticalAlignment = VerticalAlignment.Center,
                Opacity = suppressed ? 0.4 : 1,
            });
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body, TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Shrink.Add(tb);
        if (isHeader) tb.FontWeight = FontWeight.SemiBold;
        if (suppressed) tb.Foreground = Palette.TextDisabled;
        if (status is { Ok: false }) { tb.Foreground = Palette.Danger; ToolTip.SetTip(tb, status.Error); }
        else if (status is { Warnings.Count: > 0 }) { ToolTip.SetTip(tb, string.Join("\n", status.Warnings)); }
        sp.Children.Add(tb);
        // a feature with a Suppress When condition, or suppressed with another: the reason as a tip on a small marker
        if (condition != null)
        {
            var mark = new TextBlock { Text = "ƒ", FontStyle = FontStyle.Italic, FontSize = TypeScale.Body, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(mark, condition);
            sp.Children.Add(mark);
        }
        if (status is { Ok: false }) sp.Children.Add(new TextBlock { Text = "✖", Foreground = Palette.Danger, VerticalAlignment = VerticalAlignment.Center });
        else if (status is { Warnings.Count: > 0 }) sp.Children.Add(new TextBlock { Text = "⚠", Foreground = Palette.Warning, VerticalAlignment = VerticalAlignment.Center });
        if (status != null && status.Milliseconds > 200) sp.Children.Add(new TextBlock { Text = $"{status.Milliseconds:0} ms", Foreground = Palette.TextMuted, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center });
        if (sketchShown is { } shownNow && !Flyout) sp.Children.Add(EyeButton(id, shownNow));

        var row = new Border { Classes = { "treerow" }, Child = sp, Tag = id };
        if (status is { Ok: false }) ToolTip.SetTip(row, status.Error);
        else if (status is { Warnings.Count: > 0 }) ToolTip.SetTip(row, string.Join("\n", status.Warnings));
        row.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(row);
            if (Flyout)
            {
                // the flyout only picks: a left click selects (and goes to the open command's box, as the tree's does);
                // its part row opens and closes it, the whole row (an easier target than the arrow)
                if (id == "__part") { if (pt.Properties.IsLeftButtonPressed) ToggleExpanded(id); e.Handled = true; return; }
                if (pt.Properties.IsLeftButtonPressed) { Select(id); SelectedChanged?.Invoke(id); }
                e.Handled = true;
                return;
            }
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

    /// <summary>A sketch nest (or the flyout) opened or closed: the main tree asks to be rebuilt, the flyout redraws itself.</summary>
    void ToggleExpanded(string id)
    {
        if (!_expanded.Add(id)) _expanded.Remove(id);
        if (!Flyout) { ExpandToggled?.Invoke(); return; }
        if (_last is { } l) Rebuild(l.doc, l.title, l.result);
    }
}
