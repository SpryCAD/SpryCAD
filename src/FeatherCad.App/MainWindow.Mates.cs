using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Assembly;

namespace FeatherCad.App;

/// <summary>
/// Mates (SolidWorks' Mate PropertyManager): two faces picked on the components (or an origin plane of the
/// assembly), a mate type among those that fit them (the likely one chosen), Flip Alignment, a distance or an angle;
/// the components move to it as it is set up (the first face's component, then the rest), OK adds it and the page
/// stays open for the next one; Cancel puts everything back. Dragging a component moves it only as its mates let it.
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
        /// <summary>The mate being shown (added for the preview; removed when the setup changes or is cancelled).</summary>
        public Mate? Preview;
        /// <summary>Where the components were before the preview.</summary>
        public Dictionary<string, Placement> Before = new();
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
        st.Type = null; st.Flip = false;
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
        if (_mating != null)
            foreach (var p in _mating.Picks)
                if (p.Component == componentId) (l ??= new()).Add((p.Face, ViewportControl.SelectColor));
        if (_hoverFace is { } h && h.comp == componentId) (l ??= new()).Add((h.face, ViewportControl.HoverColor));
        return l;
    }

    static readonly MateType[] MateTypes = { MateType.Coincident, MateType.Parallel, MateType.Perpendicular, MateType.Concentric, MateType.Distance, MateType.Angle };

    /// <summary>The mate type likely meant for two references (SolidWorks' choice where it has one).</summary>
    static MateType DefaultType(MateGeomKind a, MateGeomKind b) => (a, b) switch
    {
        (MateGeomKind.Axis, MateGeomKind.Axis) => MateType.Concentric,
        (MateGeomKind.Axis, MateGeomKind.Plane) or (MateGeomKind.Plane, MateGeomKind.Axis) => MateType.Perpendicular,
        (MateGeomKind.Axis, MateGeomKind.Point) or (MateGeomKind.Point, MateGeomKind.Axis) => MateType.Concentric,
        _ => MateType.Coincident,
    };

    void MateCommand()
    {
        if (Asm is not { } a) return;
        if (a.Model.Doc.Components.Count < 1) { Status("Insert components first."); return; }
        a.BeginChange();
        var st = new Mating();
        _mating = st;
        Snapshot();
        void Snapshot() { st.Before = a.Model.Doc.Components.ToDictionary(c => c.Id, c => c.Placement); }
        void Restore() { foreach (var c in a.Model.Doc.Components) if (st.Before.TryGetValue(c.Id, out var p)) c.Placement = p; }
        void DropPreview()
        {
            if (st.Preview == null) return;
            // the preview was the last mate added: its number goes back (the next is named as if it never was)
            var d = a.Model.Doc;
            d.Mates.Remove(st.Preview);
            d.NextMateId--;
            if (d.MateCounters.TryGetValue(st.Preview.Type.ToString(), out var n)) d.MateCounters[st.Preview.Type.ToString()] = n - 1;
            st.Preview = null;
            Restore();
        }

        var page = new PmPage("Mate", "relations");
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
        clear.Click += (_, _) => { st.Picks.Clear(); st.Type = null; st.Flip = false; st.Update?.Invoke(); };
        sel.Children.Add(clear);

        var std = page.Section("Standard Mates");
        var typeButtons = new Dictionary<MateType, ToggleButton>();
        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var t in MateTypes)
        {
            var b = new ToggleButton { Content = t.ToString(), Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3), MinHeight = 0 };
            b.Click += (_, _) => { st.Type = t; st.Flip = false; st.Update?.Invoke(); };
            typeButtons[t] = b;
            grid.Children.Add(b);
        }
        std.Children.Add(grid);
        var valueBox = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var valueLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { valueLabel, valueBox }, Margin = new Thickness(0, 4) };
        std.Children.Add(valueRow);
        valueBox.LostFocus += (_, _) => ReadValue();
        valueBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ReadValue(); e.Handled = true; } };
        void ReadValue()
        {
            if (double.TryParse(valueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v != st.Value) { st.Value = v; st.Update?.Invoke(); }
        }
        var flip = new Button { Content = "Flip Alignment", Padding = new Thickness(8, 2), MinHeight = 0 };
        ToolTip.SetTip(flip, "Turn the second face the other way (faces against each other, or facing the same way)");
        flip.Click += (_, _) => { st.Flip = !st.Flip; st.Update?.Invoke(); };
        std.Children.Add(flip);
        var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
        std.Children.Add(result);

        string Describe(MateRef r) => r.Component == null ? $"{r.Face} Plane" : $"{FindComponent(r.Component)?.Name}: {r.Kind switch { MateGeomKind.Plane => "plane", MateGeomKind.Axis => "axis", _ => "point" }} ({r.Face})";

        st.Update = () =>
        {
            DropPreview();
            picks.Children.Clear();
            foreach (var p in st.Picks) picks.Children.Add(new TextBlock { Text = Describe(p), FontSize = 12 });
            if (st.Picks.Count == 0) picks.Children.Add(new TextBlock { Text = "(nothing picked)", FontSize = 12, Opacity = 0.6 });
            bool two = st.Picks.Count == 2;
            MateGeomKind ka = two ? st.Picks[0].Kind : MateGeomKind.Plane, kb = two ? st.Picks[1].Kind : MateGeomKind.Plane;
            foreach (var (t, b) in typeButtons) b.IsEnabled = two && Mate.Supports(t, ka, kb);
            if (two && (st.Type == null || !Mate.Supports(st.Type.Value, ka, kb))) st.Type = DefaultType(ka, kb);
            foreach (var (t, b) in typeButtons) b.IsChecked = two && st.Type == t;
            valueRow.IsVisible = st.Type is MateType.Distance or MateType.Angle;
            valueLabel.Text = st.Type == MateType.Angle ? "Angle (°)" : "Distance (mm)";
            if (st.Type == MateType.Angle && st.Value > 180) st.Value = 90;
            valueBox.Text = st.Value.ToString("0.###", CultureInfo.InvariantCulture);
            flip.IsEnabled = two;
            result.Text = "";
            if (two && st.Type is { } type && (st.Picks[0].Component != null || st.Picks[1].Component != null) && st.Picks[0].Component != st.Picks[1].Component)
            {
                st.Preview = a.Model.AddMate(type, st.Picks[0].Clone(), st.Picks[1].Clone(), st.Value);
                if (st.Flip) { st.Preview.Aligned = !st.Preview.Aligned; a.Model.SolveMates(); }
                var err = a.Model.LastSolve?.Errors.GetValueOrDefault(st.Preview.Id) ?? 0;
                result.Text = err > 1e-6 ? $"Cannot be satisfied with the other mates (off by {err:0.###}): it conflicts, or the components it would move are fixed."
                    : "Press OK (Enter) to add it.";
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
        ShowConfirmCorner(Ok, Cancel, "OK: add the mate (Enter); with nothing set up, close", "Cancel (Esc)");
        st.Update();
        Status("Mate: click two faces (or a face and an origin plane).");
    }

    /// <summary>A command from a mate's context menu in the tree.</summary>
    void OnMateCommand(string id, string cmd)
    {
        if (Asm is not { } a || a.Model.Doc.Mates.FirstOrDefault(m => m.Id == id) is not { } mate) return;
        a.BeginChange();
        switch (cmd)
        {
            case "flip": mate.Aligned = !mate.Aligned; break;
            case "suppress": mate.Suppressed = !mate.Suppressed; break;
            case "delete": a.Model.Doc.Mates.Remove(mate); break;
        }
        a.Model.SolveMates();
        a.EndChange();
        Status(cmd switch { "flip" => $"{mate.Name}: flipped.", "suppress" => $"{mate.Name}: {(mate.Suppressed ? "suppressed" : "unsuppressed")}.", _ => $"Deleted {mate.Name}." });
    }
}
