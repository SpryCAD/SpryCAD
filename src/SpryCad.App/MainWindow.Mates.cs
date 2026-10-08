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
        public double Value = 10;
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
        /// <summary>The mate being edited (null: adding new ones); it stands in the list while no preview does.</summary>
        public Mate? Editing;
        public Action? Update;
    }

    Mating? _mating;
    (string comp, string face)? _hoverFace;

    public bool PickingFaces => _mating != null && Asm != null;

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
            foreach (var p in refs)
                if (p.Component == componentId) (l ??= new()).Add((p.Face, ViewportControl.SelectColor));
        if (_hoverFace is { } h && h.comp == componentId) (l ??= new()).Add((h.face, ViewportControl.HoverColor));
        return l;
    }

    static readonly MateType[] StandardMates = { MateType.Coincident, MateType.Parallel, MateType.Perpendicular, MateType.Tangent, MateType.Concentric, MateType.Lock, MateType.Distance, MateType.Angle };
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
            if (e.Type == MateType.Screw) st.Lead = e.Value; else if (e.Type is MateType.Distance or MateType.Angle) st.Value = e.Value;
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

        var page = new PmPage(editing != null ? "Edit Mate: " + editing.Name : "Mate", "relations");
        var sel = page.Section("Mate Selections");
        page.Info(sel, "Click two faces (planar or round) on the components, or an origin plane of the assembly. Click a face again to drop it.");
        var picks = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2) };
        sel.Children.Add(picks);
        var planes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var pl in AssemblyModel.OriginPlanes)
        {
            var b = new Button { Content = pl, Padding = new Thickness(8, 2), MinHeight = 0 };
            ToolTip.SetTip(b, $"The assembly's {pl} plane");
            b.Click += (_, _) => PickRef(a.Model.RefTo(null, pl), "");
            planes.Children.Add(b);
        }
        sel.Children.Add(planes);
        var clear = new Button { Content = "Clear Selections", Padding = new Thickness(8, 2), MinHeight = 0, Margin = new Thickness(0, 4, 0, 0) };
        clear.Click += (_, _) => { st.Picks.Clear(); st.Type = null; st.Flip = false; st.RatioA = st.RatioB = null; st.Update?.Invoke(); };
        sel.Children.Add(clear);

        var std = page.Section("Standard Mates");
        var typeButtons = new Dictionary<MateType, ToggleButton>();
        WrapPanel TypeGrid(IEnumerable<MateType> types)
        {
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var t in types)
            {
                var b = new ToggleButton { Content = t.ToString(), Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3), MinHeight = 0 };
                b.Click += (_, _) => { st.Type = t; st.Flip = false; st.Update?.Invoke(); };
                typeButtons[t] = b;
                grid.Children.Add(b);
            }
            return grid;
        }
        std.Children.Add(TypeGrid(StandardMates));
        var mech = page.Section("Mechanical Mates");
        mech.Children.Add(TypeGrid(MechanicalMates));
        page.Info(mech, "Gear: two round faces turning together at a ratio (their radii unless set). Screw: one turning moves the other along the axis, the lead per turn.");

        // a number field: a label and a box (Enter or leaving it applies); its Tag shows the value again
        StackPanel Field(string label, Func<double?> get, Action<double?> set, bool optional = false)
        {
            var box = new TextBox { Width = 90 };
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 96 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { lbl, box }, Margin = new Thickness(0, 2) };
            row.Tag = (Action)(() => box.Text = get() is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "");
            void Read()
            {
                if (string.IsNullOrWhiteSpace(box.Text) && optional) { if (get() != null) { set(null); st.Update?.Invoke(); } return; }
                if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v != get()) { set(v); st.Update?.Invoke(); }
            }
            box.LostFocus += (_, _) => Read();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Read(); e.Handled = true; } };
            return row;
        }
        CheckBox Check(string label, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Content = label, Margin = new Thickness(0, 2) };
            c.IsCheckedChanged += (_, _) => { if ((c.IsChecked == true) != get()) { set(c.IsChecked == true); st.Update?.Invoke(); } };
            return c;
        }
        var opts = page.Section("Options");
        var valueRow = Field("Distance (mm)", () => st.Value, v => st.Value = v ?? st.Value);
        var limits = Check("Limits (it may move between)", () => st.Limits, v => st.Limits = v);
        var minRow = Field("Minimum", () => st.Min, v => st.Min = v ?? st.Min);
        var maxRow = Field("Maximum", () => st.Max, v => st.Max = v ?? st.Max);
        var ratioA = Field("Ratio: first", () => st.RatioA, v => st.RatioA = v, optional: true);
        var ratioB = Field("Ratio: second", () => st.RatioB, v => st.RatioB = v, optional: true);
        var lead = Field("Lead (mm/turn)", () => st.Lead, v => st.Lead = v ?? st.Lead);
        var reverse = Check("Reverse", () => st.Reverse, v => st.Reverse = v);
        var flip = new Button { Content = "Flip Alignment", Padding = new Thickness(8, 2), MinHeight = 0, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(flip, "Turn the second face the other way (faces against each other, or facing the same way)");
        flip.Click += (_, _) => { st.Flip = !st.Flip; st.Update?.Invoke(); };
        foreach (var c in new Control[] { valueRow, limits, minRow, maxRow, ratioA, ratioB, lead, reverse, flip }) opts.Children.Add(c);
        var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
        opts.Children.Add(result);

        string Describe(MateRef r)
        {
            if (r.Component == null) return $"{r.Face} Plane";
            var what = r.Kind switch
            {
                MateGeomKind.Plane => "plane",
                MateGeomKind.Axis => r.R > 0 ? $"round, R {r.R:0.###}" : "axis",
                _ => r.R > 0 ? $"sphere, R {r.R:0.###}" : "point",
            };
            return $"{FindComponent(r.Component)?.Name}: {what} ({r.Face})";
        }

        st.Update = () =>
        {
            DropPreview();
            picks.Children.Clear();
            foreach (var p in st.Picks) picks.Children.Add(new TextBlock { Text = Describe(p), FontSize = 12 });
            if (st.Picks.Count == 0) picks.Children.Add(new TextBlock { Text = "(nothing picked)", FontSize = 12, Opacity = 0.6 });
            bool two = st.Picks.Count == 2;
            MateGeom? ga = two ? st.Picks[0].Geom : null, gb = two ? st.Picks[1].Geom : null;
            foreach (var (t, b) in typeButtons) b.IsEnabled = two && Mate.Supports(t, ga!, gb!);
            if (two && (st.Type == null || !Mate.Supports(st.Type.Value, ga!, gb!))) st.Type = DefaultType(ga!.Kind, gb!.Kind);
            foreach (var (t, b) in typeButtons) b.IsChecked = two && st.Type == t;
            var type = st.Type;
            bool dist = type is MateType.Distance or MateType.Angle;
            valueRow.IsVisible = dist && !st.Limits;
            ((TextBlock)valueRow.Children[0]).Text = type == MateType.Angle ? "Angle (deg)" : "Distance (mm)";
            if (type == MateType.Angle && st.Value > 180) st.Value = 90;
            limits.IsVisible = dist;
            minRow.IsVisible = maxRow.IsVisible = dist && st.Limits;
            ratioA.IsVisible = ratioB.IsVisible = type == MateType.Gear;
            lead.IsVisible = type == MateType.Screw;
            reverse.IsVisible = type is MateType.Gear or MateType.Screw;
            reverse.Content = type == MateType.Screw ? "Left hand" : "Reverse (turn the same way)";
            limits.IsChecked = st.Limits; reverse.IsChecked = st.Reverse;
            // the gear ratio: the radii unless given
            if (type == MateType.Gear && two) { st.RatioA ??= ga!.R > 0 ? ga.R : 1; st.RatioB ??= gb!.R > 0 ? gb.R : 1; }
            foreach (var row in new[] { valueRow, minRow, maxRow, ratioA, ratioB, lead }) ((Action)row.Tag!)();
            flip.IsVisible = type is not (MateType.Lock or MateType.Gear or MateType.Screw);
            flip.IsEnabled = two;
            result.Text = "";
            if (two && type is { } tp && (st.Picks[0].Component != null || st.Picks[1].Component != null) && st.Picks[0].Component != st.Picks[1].Component)
            {
                void Setup(Mate m)
                {
                    if (st.Limits && tp is MateType.Distance or MateType.Angle) { m.Min = Math.Min(st.Min, st.Max); m.Max = Math.Max(st.Min, st.Max); }
                    if (tp == MateType.Gear) { m.RatioA = st.RatioA ?? m.RatioA; m.RatioB = st.RatioB ?? m.RatioB; }
                    if (tp is MateType.Gear or MateType.Screw) m.Reverse = st.Reverse;
                    if (st.Flip) m.Aligned = !m.Aligned;
                }
                var value = tp == MateType.Screw ? st.Lead : st.Value;
                st.Preview = st.Editing is { } orig
                    ? a.Model.ReplaceMate(orig, tp, st.Picks[0].Clone(), st.Picks[1].Clone(), value, Setup, EditName(tp))
                    : a.Model.AddMate(tp, st.Picks[0].Clone(), st.Picks[1].Clone(), value, Setup);
                var err = a.Model.LastSolve?.Errors.GetValueOrDefault(st.Preview.Id) ?? 0;
                result.Text = err > 1e-6 ? $"Cannot be satisfied with the other mates (off by {err:0.###}): it conflicts, or the components it would move are fixed."
                    : st.Editing != null ? "Press OK (Enter) to keep the changes." : "Press OK (Enter) to add it.";
                result.Foreground = err > 1e-6 ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFC62828")) : null;
                if (result.Foreground == null) result.ClearValue(TextBlock.ForegroundProperty);
            }
            else if (two && st.Picks[0].Component == st.Picks[1].Component)
                result.Text = "Both faces are on the same component: pick faces on two components (or a component and a plane).";
            RefreshAssemblyScene();
            RebuildTree();
        };

        void Close()
        {
            _mating = null; _hoverFace = null;
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
                _mating = null; _hoverFace = null;
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
