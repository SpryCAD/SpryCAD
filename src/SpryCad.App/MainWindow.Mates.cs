using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SpryCad.App.UI;
using SpryCad.App.Viewport;
using SpryCad.Core.Assembly;

namespace SpryCad.App;

/// <summary>
/// Mates (SolidWorks' Mate PropertyManager): two faces picked on the components (or an origin plane of the
/// assembly), a mate type among those that fit them (the likely one chosen), Flip Alignment, a distance or an angle;
/// the components move to it as it is set up (the first face's component, then the rest), OK adds it and the page
/// stays open for the next one; Cancel puts everything back. Dragging a component moves it only as its mates let it.
/// A mate is edited on the same page (double-click it in the tree): its faces, type and values shown, changed with
/// the components following, OK keeping the changes (same name, same place in the list), Cancel the mate as it was.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Mate page's state while it is open.</summary>
    sealed class Mating
    {
        public readonly List<MateRef> Picks = new();
        public MateType? Type;
        /// <summary>A Coincident's offset (mm; 0: touching; below 0: on the other side).</summary>
        public double Offset;
        /// <summary>An Angle's angle (degrees).</summary>
        public double Angle = 90;
        /// <summary>The Mechanical tab shown (else Standard).</summary>
        public bool Mechanical;
        public bool Flip;
        public bool Limits;
        public double Min = 0, Max = 20;
        public double? RatioA, RatioB;
        public double Lead = 2;
        public bool Reverse;
        /// <summary>The mate being shown (added for the preview; removed when the setup changes or is cancelled).</summary>
        public Mate? Preview;
        /// <summary>Where the components were before the preview.</summary>
        public Dictionary<string, Placement> Before = new();
        /// <summary>The pick selected in the Mate Selections box (-1: none): drawn in its own color.</summary>
        public int Focus = -1;
        /// <summary>The mate being edited (null: adding new ones); it stands in the list while no preview does.</summary>
        public Mate? Editing;
        public Action? Update;
    }

    Mating? _mating;
    (string comp, string face)? _hoverFace;
    /// <summary>A pick (its entry in Mate Selections) or an origin plane (its button) under the pointer: shown in orange.</summary>
    MateRef? _hoverPick;

    void HoverPick(MateRef? r)
    {
        if (r == _hoverPick) return;
        _hoverPick = r;
        RefreshAssemblyScene();
    }

    /// <summary>The color a mate's i-th reference is drawn in: full selection blue, paler while another is selected in the box.</summary>
    Rgba PickColor(int i) => _mating is { Focus: >= 0 } mt && mt.Focus != i ? PaleSelect : ViewportControl.SelectColor;

    public bool PickingFaces => _mating != null && Asm != null;

    /// <summary>The picks not selected in the Mate Selections box while one is: a paler selection blue.</summary>
    static readonly Rgba PaleSelect = new(0.62f, 0.78f, 1.0f);

    public void HoverFace(string? component, string? face)
    {
        var h = component != null && face != null ? (component, face) : ((string, string)?)null;
        if (h == _hoverFace) return;
        _hoverFace = h;
        RefreshAssemblyScene();
    }

    public void PickFace(string component, string face)
    {
        if (_mating is not { } st || Asm is not { } a) return;
        PickRef(a.Model.RefTo(component, face), $"{FindComponent(component)?.Name}: that face is not planar or round (it cannot be mated yet)");
    }

    void PickRef(MateRef? r, string notMateable)
    {
        if (_mating is not { } st) return;
        if (r == null) { Status(notMateable); return; }
        // picked again: dropped; a third: replaces the second
        var i = st.Picks.FindIndex(p => p.Component == r.Component && p.Face == r.Face);
        if (i >= 0) st.Picks.RemoveAt(i);
        else if (st.Picks.Count == 2) st.Picks[1] = r;
        else st.Picks.Add(r);
        st.Type = null; st.Flip = false; st.RatioA = st.RatioB = null;
        st.Update?.Invoke();
    }

    /// <summary>A drag moved a component: the mates pull it, and what it is mated to, back onto them.</summary>
    public void Dragged(string component)
    {
        if (Asm is not { } a) return;
        if (a.Model.Doc.Mates.Count > 0) a.Model.SolveMates();
        RefreshAssemblyScene();
    }

    /// <summary>The faces drawn highlighted on a component: the one under the cursor, and the Mate page's picks.</summary>
    List<(string face, Rgba color)>? FaceHighlights(string componentId)
    {
        List<(string, Rgba)>? l = null;
        var refs = _mating?.Picks ?? (SelectedMate() is { } m ? new List<MateRef> { m.A, m.B } : null);
        if (refs != null)
            for (int i = 0; i < refs.Count; i++)
                if (refs[i].Component == componentId)
                    // with an entry selected in the Mate Selections box, its face in the selection blue and the other
                    // paler, so it is plain which it is (orange is the hover's)
                    (l ??= new()).Add((refs[i].Face, PickColor(i)));
        if (_hoverPick is { Component: { } hc, Face: var hf } && hc == componentId) (l ??= new()).Add((hf, ViewportControl.HoverColor));
        if (_hoverFace is { } h && h.comp == componentId) (l ??= new()).Add((h.face, ViewportControl.HoverColor));
        return l;
    }

    static readonly MateType[] StandardMates = { MateType.Coincident, MateType.Parallel, MateType.Perpendicular, MateType.Tangent, MateType.Concentric, MateType.Lock, MateType.Angle };
    static readonly MateType[] MechanicalMates = { MateType.Gear, MateType.Screw };

    /// <summary>The mate type likely meant for two references (SolidWorks' choice where it has one).</summary>
    static MateType DefaultType(MateGeomKind a, MateGeomKind b) => (a, b) switch
    {
        (MateGeomKind.Axis, MateGeomKind.Axis) => MateType.Concentric,
        (MateGeomKind.Axis, MateGeomKind.Plane) or (MateGeomKind.Plane, MateGeomKind.Axis) => MateType.Perpendicular,
        (MateGeomKind.Axis, MateGeomKind.Point) or (MateGeomKind.Point, MateGeomKind.Axis) => MateType.Concentric,
        _ => MateType.Coincident,
    };

    void MateCommand() => MatePage(null);

    /// <summary>The icon of a mate type (in the tree and on the Mate page).</summary>
    public static string MateIcon(MateType t) => t switch
    {
        MateType.Coincident or MateType.Distance => "mate-coincident",
        MateType.Parallel => "mate-parallel",
        MateType.Perpendicular => "mate-perpendicular",
        MateType.Tangent => "mate-tangent",
        MateType.Concentric => "mate-concentric",
        MateType.Lock => "mate-lock",
        MateType.Angle => "mate-angle",
        MateType.Gear => "mate-gear",
        MateType.Screw => "mate-screw",
        _ => "mates",
    };

    /// <summary>The Mate page: adding mates, or (<paramref name="editing"/>) changing one.</summary>
    void MatePage(Mate? editing)
    {
        if (Asm is not { } a) return;
        if (a.Model.Doc.Components.Count < 1) { Status("Insert components first."); return; }
        if (_mating != null) { Status("Finish the open Mate page first (OK or Cancel)."); return; }
        a.BeginChange();
        var st = new Mating { Editing = editing };
        if (editing is { } e)
        {
            st.Picks.Add(e.A.Clone()); st.Picks.Add(e.B.Clone());
            st.Type = e.Type;
            if (e.Type == MateType.Screw) st.Lead = e.Value;
            else if (e.Type == MateType.Angle) st.Angle = e.Value;
            else if (e.Type == MateType.Coincident) st.Offset = e.Value;
            st.Mechanical = e.Type is MateType.Gear or MateType.Screw;
            if (e.Min is { } lo && e.Max is { } hi) { st.Limits = true; st.Min = lo; st.Max = hi; }
            if (e.Type == MateType.Gear) { st.RatioA = e.RatioA; st.RatioB = e.RatioB; }
            st.Reverse = e.Reverse;
        }
        _mating = st;
        Snapshot();
        void Snapshot() { st.Before = a.Model.Doc.Components.ToDictionary(c => c.Id, c => c.Placement); }
        void Restore() { foreach (var c in a.Model.Doc.Components) if (st.Before.TryGetValue(c.Id, out var p)) c.Placement = p; }
        // an edited mate's name: kept, or the next of its new type's when the type changed (counted on OK)
        string? EditName(MateType t) => st.Editing is { } e && t != e.Type ? t + (a.Model.Doc.MateCounters.GetValueOrDefault(t.ToString()) + 1).ToString() : null;
        void DropPreview()
        {
            if (st.Preview == null) return;
            if (st.Editing is { } orig)
            {
                // the mate as it was, back in its place
                var i = a.Model.Doc.Mates.IndexOf(st.Preview);
                if (i >= 0) a.Model.Doc.Mates[i] = orig;
                st.Preview = null;
                Restore();
                return;
            }
            // the preview was the last mate added: its number goes back (the next is named as if it never was)
            var d = a.Model.Doc;
            d.Mates.Remove(st.Preview);
            d.NextMateId--;
            if (d.MateCounters.TryGetValue(st.Preview.Type.ToString(), out var n)) d.MateCounters[st.Preview.Type.ToString()] = n - 1;
            st.Preview = null;
            Restore();
        }

        var page = new PmPage(editing != null ? "Edit Mate: " + editing.Name : "Mate", "mates");
        var red = Palette.Danger;

        // Standard / Mechanical: tabs, as SolidWorks has them (styled as the panel's tabs, sharing the width)
        static TabStripItem Tab(string icon, string text) => new()
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { Icons.Make(icon, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
            // the page's text size (a tab's own is much larger)
            FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var tabs = new TabStrip
        {
            Classes = { "paneltabs" }, Margin = new Thickness(0, 0, 0, 4),
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 }),
            Items = { Tab("mate-coincident", "Standard"), Tab("mate-gear", "Mechanical") },
            SelectedIndex = st.Mechanical ? 1 : 0,
        };
        tabs.SelectionChanged += (_, _) =>
        {
            var mechanical = tabs.SelectedIndex == 1;
            if (mechanical == st.Mechanical) return;
            // the strip selects its first tab by itself when it is shown: only a click or a key changes the tab
            if (!tabs.IsPointerOver && !tabs.IsKeyboardFocusWithin)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => tabs.SelectedIndex = st.Mechanical ? 1 : 0);
                return;
            }
            st.Mechanical = mechanical;
            // (after the strip has finished changing its selection)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_mating == st) st.Update?.Invoke(); });
        };
        page.Content(tabs);

        // the picks, in a selection box as the other editors have (blue: what is clicked goes in)
        var sel = page.Section("Mate Selections");
        var box = new ListBox { Classes = { "selbox", "activebox" }, MinHeight = 44 };
        void DropPick(int i)
        {
            if (i < 0 || i >= st.Picks.Count) return;
            st.Picks.RemoveAt(i);
            st.Type = st.Picks.Count == 2 ? st.Type : null; st.Flip = false; st.RatioA = st.RatioB = null;
            st.Update?.Invoke();
        }
        void ClearPicks() { st.Picks.Clear(); st.Type = null; st.Flip = false; st.RatioA = st.RatioB = null; st.Update?.Invoke(); }
        // an entry selected: its face drawn in the full selection blue (the other paler), and named in the status line
        box.SelectionChanged += (_, _) =>
        {
            st.Focus = box.SelectedIndex;
            if (st.Focus >= 0 && st.Focus < st.Picks.Count) Status($"{Label(st.Picks[st.Focus])}: {Describe(st.Picks[st.Focus])}.");
            RefreshAssemblyScene();
        };
        box.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back) { DropPick(box.SelectedIndex); e.Handled = true; } };
        var delPick = new MenuItem { Header = "Delete" };
        delPick.Click += (_, _) => DropPick(box.SelectedIndex);
        var clearPicks = new MenuItem { Header = "Clear Selections" };
        clearPicks.Click += (_, _) => ClearPicks();
        box.ContextMenu = new ContextMenu { Items = { delPick, clearPicks } };
        sel.Children.Add(box);
        var planes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var pl in AssemblyModel.OriginPlanes)
        {
            var b = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Icons.Make("plane", 14), new TextBlock { Text = pl } } }, Padding = new Thickness(6, 2), MinHeight = 0 };
            ToolTip.SetTip(b, $"The assembly's {pl} plane");
            b.Click += (_, _) => PickRef(a.Model.RefTo(null, pl), "");
            // hovered: the plane shown where it is (even with the planes hidden)
            b.PointerEntered += (_, _) => HoverPick(a.Model.RefTo(null, pl));
            b.PointerExited += (_, _) => HoverPick(null);
            planes.Children.Add(b);
        }
        sel.Children.Add(planes);
        page.Info(sel, "Click two faces (planar or round) on the components, or an origin plane. Click a face again (or Delete in the box) to drop it.");

        // the types: a row each, its icon and name (those that cannot relate the picks greyed)
        var typeButtons = new Dictionary<MateType, ToggleButton>();
        StackPanel TypeList(IEnumerable<MateType> types)
        {
            var list = new StackPanel { Spacing = 1 };
            foreach (var t in types)
            {
                var b = new ToggleButton
                {
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Make(MateIcon(t), 18), new TextBlock { Text = t.ToString(), VerticalAlignment = VerticalAlignment.Center } } },
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 3), MinHeight = 0, Classes = { "matetype" },
                };
                b.Click += (_, _) => { st.Type = t; st.Flip = false; st.Update?.Invoke(); };
                typeButtons[t] = b;
                list.Children.Add(b);
            }
            return list;
        }
        var std = page.Section("Mate Type");
        std.Children.Add(TypeList(StandardMates));
        var mech = page.Section("Mechanical Mates");
        mech.Children.Add(TypeList(MechanicalMates));
        page.Info(mech, "Gear: two round faces turning together at a ratio (their radii unless set). Screw: one turning moves the other along the axis, the lead per turn.");

        // a number field: a label and a box (Enter or leaving it applies); its Tag shows the value again
        StackPanel Field(string label, Func<double?> get, Action<double?> set, bool optional = false, bool signed = false)
        {
            var tb = new TextBox { Width = 90 };
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 96 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { lbl, tb }, Margin = new Thickness(0, 2) };
            row.Tag = (Action)(() => tb.Text = get() is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "");
            void Read()
            {
                if (string.IsNullOrWhiteSpace(tb.Text) && optional) { if (get() != null) { set(null); st.Update?.Invoke(); } return; }
                if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && (signed || v >= 0) && v != get()) { set(v); st.Update?.Invoke(); }
            }
            tb.LostFocus += (_, _) => Read();
            tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Read(); e.Handled = true; } };
            return row;
        }
        CheckBox Check(string label, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Content = label, Margin = new Thickness(0, 2) };
            c.IsCheckedChanged += (_, _) => { if ((c.IsChecked == true) != get()) { set(c.IsChecked == true); st.Update?.Invoke(); } };
            return c;
        }
        var opts = page.Section("Options");
        var offsetRow = Field("Offset (mm)", () => st.Offset, v => st.Offset = v ?? st.Offset, signed: true);
        ToolTip.SetTip(offsetRow, "How far apart the references are held: 0 touching; a negative offset puts it on the other side");
        var angleRow = Field("Angle (deg)", () => st.Angle, v => st.Angle = Math.Min(180, v ?? st.Angle));
        var limits = Check("Limits (it may move between)", () => st.Limits, v => st.Limits = v);
        var minRow = Field("Minimum", () => st.Min, v => st.Min = v ?? st.Min);
        var maxRow = Field("Maximum", () => st.Max, v => st.Max = v ?? st.Max);
        var ratioA = Field("Ratio: first", () => st.RatioA, v => st.RatioA = v, optional: true);
        var ratioB = Field("Ratio: second", () => st.RatioB, v => st.RatioB = v, optional: true);
        var lead = Field("Lead (mm/turn)", () => st.Lead, v => st.Lead = v ?? st.Lead);
        var reverse = Check("Reverse", () => st.Reverse, v => st.Reverse = v);
        // mate alignment: the second reference's normal (or axis) the same way as the first's, or against it
        var alignSame = new ToggleButton { Content = Icons.Make("align-same", 18), Padding = new Thickness(4), MinHeight = 0, Classes = { "matetype", "framed" } };
        var alignOpp = new ToggleButton { Content = Icons.Make("align-opposite", 18), Padding = new Thickness(4), MinHeight = 0, Classes = { "matetype", "framed" } };
        ToolTip.SetTip(alignSame, "Aligned: the normals (or axes) point the same way");
        ToolTip.SetTip(alignOpp, "Anti-aligned: the normals point at each other (faces against each other)");
        void SetAligned(bool want) { if (st.Preview != null && st.Preview.Aligned != want) { st.Flip = !st.Flip; st.Update?.Invoke(); } else st.Update?.Invoke(); }
        alignSame.Click += (_, _) => SetAligned(true);
        alignOpp.Click += (_, _) => SetAligned(false);
        var align = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0),
            Children = { new TextBlock { Text = "Mate alignment:", VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) }, alignSame, alignOpp },
        };
        foreach (var c in new Control[] { offsetRow, angleRow, limits, minRow, maxRow, ratioA, ratioB, lead, reverse, align }) opts.Children.Add(c);
        var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
        opts.Children.Add(result);

        // a pick as SolidWorks lists it (Face<…>@Component), what it is in its tooltip
        string Label(MateRef r) => r.Component == null ? $"{r.Face} Plane" : $"Face<{r.Face}>@{FindComponent(r.Component)?.Name}";
        string Describe(MateRef r) => r.Component == null ? $"The assembly's {r.Face} plane" : r.Kind switch
        {
            MateGeomKind.Plane => "A planar face",
            MateGeomKind.Axis => r.R > 0 ? $"A round face, R {r.R:0.###} (its axis)" : "An axis",
            _ => r.R > 0 ? $"A sphere, R {r.R:0.###} (its centre)" : "A point",
        };

        st.Update = () =>
        {
            DropPreview();
            var keepIdx = box.SelectedIndex;
            box.ItemsSource = st.Picks.Select(p =>
            {
                var tb = new TextBlock { Text = Label(p), Background = Avalonia.Media.Brushes.Transparent };
                ToolTip.SetTip(tb, Describe(p));
                // hovered: its face or plane shown in orange
                tb.PointerEntered += (_, _) => HoverPick(p);
                tb.PointerExited += (_, _) => HoverPick(null);
                return tb;
            }).ToList();
            _hoverPick = null;
            if (keepIdx >= 0 && keepIdx < st.Picks.Count) box.SelectedIndex = keepIdx;
            bool two = st.Picks.Count == 2;
            MateGeom? ga = two ? st.Picks[0].Geom : null, gb = two ? st.Picks[1].Geom : null;
            foreach (var (t, b) in typeButtons) b.IsEnabled = two && Mate.Supports(t, ga!, gb!);
            if (two && (st.Type == null || !Mate.Supports(st.Type.Value, ga!, gb!)))
                st.Type = st.Mechanical && Mate.Supports(MateType.Gear, ga!, gb!) ? MateType.Gear : DefaultType(ga!.Kind, gb!.Kind);
            foreach (var (t, b) in typeButtons) b.IsChecked = two && st.Type == t;
            var type = st.Type;
            if (tabs.SelectedIndex != (st.Mechanical ? 1 : 0)) tabs.SelectedIndex = st.Mechanical ? 1 : 0;
            ((Control)std.Parent!).IsVisible = !st.Mechanical;
            ((Control)mech.Parent!).IsVisible = st.Mechanical;
            bool coin = type == MateType.Coincident, ang = type == MateType.Angle;
            offsetRow.IsVisible = coin && !st.Limits;
            angleRow.IsVisible = ang && !st.Limits;
            limits.IsVisible = coin || ang;
            minRow.IsVisible = maxRow.IsVisible = (coin || ang) && st.Limits;
            ((TextBlock)minRow.Children[0]).Text = ang ? "Minimum (deg)" : "Minimum (mm)";
            ((TextBlock)maxRow.Children[0]).Text = ang ? "Maximum (deg)" : "Maximum (mm)";
            ratioA.IsVisible = ratioB.IsVisible = type == MateType.Gear;
            lead.IsVisible = type == MateType.Screw;
            reverse.IsVisible = type is MateType.Gear or MateType.Screw;
            reverse.Content = type == MateType.Screw ? "Left hand" : "Reverse (turn the same way)";
            limits.IsChecked = st.Limits; reverse.IsChecked = st.Reverse;
            // the gear ratio: the radii unless given
            if (type == MateType.Gear && two) { st.RatioA ??= ga!.R > 0 ? ga.R : 1; st.RatioB ??= gb!.R > 0 ? gb.R : 1; }
            foreach (var row in new[] { offsetRow, angleRow, minRow, maxRow, ratioA, ratioB, lead }) ((Action)row.Tag!)();
            align.IsVisible = type is not (MateType.Lock or MateType.Gear or MateType.Screw or MateType.Tangent);
            result.Text = "";
            if (two && type is { } tp && (st.Picks[0].Component != null || st.Picks[1].Component != null) && st.Picks[0].Component != st.Picks[1].Component)
            {
                void Setup(Mate m)
                {
                    if (st.Limits && tp is MateType.Coincident or MateType.Angle) { m.Min = Math.Min(st.Min, st.Max); m.Max = Math.Max(st.Min, st.Max); }
                    // a negative offset: the other side
                    if (tp == MateType.Coincident && !st.Limits && st.Offset < 0) m.Side = -m.Side;
                    if (tp == MateType.Gear) { m.RatioA = st.RatioA ?? m.RatioA; m.RatioB = st.RatioB ?? m.RatioB; }
                    if (tp is MateType.Gear or MateType.Screw) m.Reverse = st.Reverse;
                    if (st.Flip) m.Aligned = !m.Aligned;
                }
                var value = tp switch
                {
                    MateType.Screw => st.Lead,
                    MateType.Angle => st.Angle,
                    MateType.Coincident when !st.Limits => Math.Abs(st.Offset),
                    _ => 0,
                };
                st.Preview = st.Editing is { } orig
                    ? a.Model.ReplaceMate(orig, tp, st.Picks[0].Clone(), st.Picks[1].Clone(), value, Setup, EditName(tp))
                    : a.Model.AddMate(tp, st.Picks[0].Clone(), st.Picks[1].Clone(), value, Setup);
                var err = a.Model.LastSolve?.Errors.GetValueOrDefault(st.Preview.Id) ?? 0;
                result.Text = err > 1e-6 ? $"Cannot be satisfied with the other mates (off by {err:0.###}): it conflicts, or the components it would move are fixed."
                    : st.Editing != null ? "Press OK (Enter) to keep the changes." : "Press OK (Enter) to add it.";
                result.Foreground = err > 1e-6 ? red : null;
                if (result.Foreground == null) result.ClearValue(TextBlock.ForegroundProperty);
            }
            else if (two && st.Picks[0].Component == st.Picks[1].Component)
                result.Text = "Both faces are on the same component: pick faces on two components (or a component and a plane).";
            alignSame.IsEnabled = alignOpp.IsEnabled = st.Preview != null;
            alignSame.IsChecked = st.Preview?.Aligned == true;
            alignOpp.IsChecked = st.Preview is { Aligned: false };
            RefreshAssemblyScene();
            RebuildTree();
        };

        void Close()
        {
            _mating = null; _hoverFace = null; _hoverPick = null;
            // solved again as the mates now stand: a preview that could not be satisfied leaves no red behind
            if (a.Model.Doc.Mates.Count > 0) a.Model.SolveMates();
            ShowPage(null); HideConfirmCorner();
            // each mate added made its own undo point; the one begun for the next is not needed
            a.AbandonChange();
            RefreshAssemblyScene();
            RebuildTree();
        }
        void Ok()
        {
            if (st.Editing is { } orig)
            {
                // nothing set up (the faces dropped): the mate stays as it was
                if (st.Preview == null) { Close(); return; }
                if (st.Preview.Type != orig.Type) a.Model.Doc.MateCounters[st.Preview.Type.ToString()] = a.Model.Doc.MateCounters.GetValueOrDefault(st.Preview.Type.ToString()) + 1;
                Status($"Edited {st.Preview.Name}.");
                st.Preview = null;
                _mating = null; _hoverFace = null; _hoverPick = null;
                ShowPage(null); HideConfirmCorner();
                a.EndChange();
                RefreshAssemblyScene();
                RebuildTree();
                return;
            }
            if (st.Preview == null) { Close(); return; }
            // kept: the next mate starts from here (and OK with nothing set up closes the page)
            Status($"Added {st.Preview.Name}.");
            st.Preview = null;
            st.Picks.Clear(); st.Type = null; st.Flip = false;
            Snapshot();
            // an undo point per mate: this one ends, the next begins
            a.EndChange(); a.BeginChange();
            st.Update?.Invoke();
        }
        void Cancel()
        {
            DropPreview();
            Close();
        }
        page.OnOk = Ok;
        page.OnCancel = Cancel;
        ShowPage(page);
        ShowConfirmCorner(Ok, Cancel, editing != null ? "OK: keep the changes (Enter)" : "OK: add the mate (Enter); with nothing set up, close", "Cancel (Esc)");
        st.Update();
        Status(editing != null ? $"Editing {editing.Name}: change its faces (click one to drop it, then pick another), type or values."
            : "Mate: click two faces (or a face and an origin plane).");
    }

    /// <summary>A command from a mate's context menu in the tree.</summary>
    void OnMateCommand(string id, string cmd)
    {
        if (Asm is not { } a || a.Model.Doc.Mates.FirstOrDefault(m => m.Id == id) is not { } mate) return;
        if (cmd == "edit") { MatePage(mate); return; }
        if (cmd == "rename") { _ = RenameMate(mate); return; }
        if (_mating != null) { Status("Finish the open Mate page first (OK or Cancel)."); return; }
        a.BeginChange();
        switch (cmd)
        {
            case "flip": mate.Aligned = !mate.Aligned; break;
            case "suppress": mate.Suppressed = !mate.Suppressed; break;
            case "delete":
                a.Model.Doc.Mates.Remove(mate);
                if (a.SelectedMate == mate.Id) a.SelectedMate = null;
                break;
        }
        a.Model.SolveMates();
        a.EndChange();
        Status(cmd switch { "flip" => $"{mate.Name}: flipped.", "suppress" => $"{mate.Name}: {(mate.Suppressed ? "suppressed" : "unsuppressed")}.", _ => $"Deleted {mate.Name}." });
        RefreshAssemblyScene();
    }

    async Task RenameMate(Mate mate)
    {
        if (Asm is not { } a) return;
        var name = (await Dialogs.Input(this, "Rename", "Mate name:", mate.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == mate.Name) return;
        if (a.Model.Doc.Mates.Any(m => m != mate && m.Name == name)) { Status($"Another mate is already named {name}."); return; }
        a.BeginChange();
        mate.Name = name;
        a.EndChange();
        Status($"Renamed to {name}.");
    }
}
