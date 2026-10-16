using Avalonia;
using Avalonia.Controls;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Assembly;
using SpryCad.Core.Library;

namespace SpryCad.App;

/// <summary>
/// Insert Fastener (section 25): the wall of a hole sized from the library picked, the screw for it found (its size
/// from the hole, its length the stack it clamps plus the nut or the thread it needs, rounded up to a standard length
/// the company libraries offer), shown with the other lengths offered; OK adds the screw, its washers and nut, mated
/// to the hole, as one undoable change. The page stays open for the next hole.
/// </summary>
public partial class MainWindow
{
    sealed class Fastening
    {
        public MateRef? Hole;
        public FastenerOptions Options = new();
        public FastenerPlan? Plan;
        public string? Screw;
        public string? Problem;
        public Action? Update;
    }

    Fastening? _fastening;

    void InsertFastenerCommand()
    {
        if (Asm is not { } a) return;
        if (_mating != null || _motor != null || _patterning != null) { Status("Finish the open page first (OK or Cancel)."); return; }
        if (_fastening != null) return;
        var st = new Fastening();
        _fastening = st;

        var page = new PmPage("Insert Fastener", "thread");
        var holeSec = page.Section("Hole");
        var holeLbl = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = TypeScale.Body };
        holeSec.Children.Add(new Border { Classes = { "pmsection" }, Child = holeLbl, Padding = new Thickness(6, 4) });
        page.Info(holeSec, "Select the wall of a Hole Wizard clearance hole. The screw enters from the unobstructed end.");

        var fast = page.Section("Fastener");
        page.Combo(fast, "Held by", new List<(FastenerEnd, string)> { (FastenerEnd.Auto, "Auto (tapped part, else nut)"), (FastenerEnd.Nut, "Nut"), (FastenerEnd.Tapped, "Tapped part") },
            () => st.Options.End, v => { st.Options.End = v; Replan(); });
        page.Check(fast, "Washer under the nut", () => st.Options.NutWasher, v => { st.Options.NutWasher = v; Replan(); });
        page.Check(fast, "Washer under the head", () => st.Options.HeadWasher, v => { st.Options.HeadWasher = v; Replan(); });
        var screwBox = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
        fast.Children.Add(screwBox);
        var noteLbl = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = TypeScale.Small, Foreground = Palette.TextMuted };
        fast.Children.Add(noteLbl);
        bool filling = false;
        screwBox.SelectionChanged += (_, _) =>
        {
            if (filling || st.Plan == null || screwBox.SelectedIndex < 0) return;
            st.Screw = st.Plan.Screws[screwBox.SelectedIndex].Key;
        };

        void Replan()
        {
            st.Plan = null; st.Problem = null; st.Screw = null;
            if (st.Hole is { Component: { } c } h)
            {
                try
                {
                    st.Plan = a.Model.PlanFastener(c, h.Face, st.Options);
                    st.Screw = st.Plan.Screw?.Key;
                }
                catch (InvalidOperationException ex) { st.Problem = ex.Message; }
            }
            st.Update?.Invoke();
        }

        st.Update = () =>
        {
            filling = true;
            screwBox.Items.Clear();
            if (st.Plan is { } p)
            {
                var hold = p.End == FastenerEnd.Nut ? "a nut" : p.Tapped != null ? a.Model.NameAt(p.Tapped) : "a tapped part";
                holeLbl.Text = $"{p.Size} hole in {a.Model.NameAt(p.Component)}: {Expr.Format(Math.Round(p.Clamped, 2))} mm clamped, held by {hold}; "
                    + $"the screw needs {Expr.Format(Math.Round(p.Needed, 2))} mm.";
                var lcol = Fasteners.ScrewColumns.ToList().IndexOf("l");
                foreach (var r in p.Screws)
                {
                    var tooShort = r.Values[lcol] < p.Needed - 1e-6;
                    screwBox.Items.Add(new ComboBoxItem { Content = $"{r.Key}{(r.Key == p.Screw?.Key ? "  (recommended)" : tooShort ? "  (too short)" : "")}" });
                }
                screwBox.SelectedIndex = st.Screw != null ? p.Screws.FindIndex(r => r.Key == st.Screw) : -1;
                noteLbl.Text = string.Join("\n", p.Notes);
            }
            else
            {
                holeLbl.Text = st.Problem ?? "(no hole picked)";
                noteLbl.Text = "";
            }
            filling = false;
            RefreshAssemblyScene();
        };

        void Close()
        {
            _fastening = null; _hoverFace = null;
            ShowPage(null); HideConfirmCorner();
            RefreshAssemblyScene();
            RebuildTree();
        }
        void Ok()
        {
            if (st.Plan is not { } p || st.Screw == null) { Status(st.Problem ?? "Select the wall of a hole first."); return; }
            a.BeginChange();
            try
            {
                var ins = a.Model.InsertFastener(p, st.Screw);
                a.EndChange();
                Status($"Added {ins.Designation}{(ins.Nut != null ? " with a nut" : "")}{(ins.Washers.Count > 0 ? $" and {ins.Washers.Count} washer{(ins.Washers.Count > 1 ? "s" : "")}" : "")}, mated to the hole. Select the next hole, or click Close.");
            }
            catch (InvalidOperationException ex)
            {
                a.AbandonChange();
                Status(ex.Message);
                return;
            }
            st.Hole = null; st.Plan = null; st.Screw = null; st.Problem = null;
            st.Update?.Invoke();
            RebuildTree();
        }
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK (Enter)", "Close (Esc)");
        st.Update();
        Status("Insert Fastener: select the wall of a Hole Wizard clearance hole.");
    }

    /// <summary>A face clicked while the Insert Fastener page is open: the hole to fasten, if it is round.</summary>
    void FastenerPick(string component, string face)
    {
        if (_fastening is not { } st || Asm is not { } a) return;
        if (a.Model.RefTo(component, face) is not { Kind: MateGeomKind.Axis } r) { Status($"{a.Model.NameAt(component)}: select the cylindrical wall of a hole."); return; }
        st.Hole = r;
        st.Plan = null; st.Problem = null; st.Screw = null;
        try
        {
            st.Plan = a.Model.PlanFastener(component, face, st.Options);
            st.Screw = st.Plan.Screw?.Key;
        }
        catch (InvalidOperationException ex) { st.Problem = ex.Message; Status(ex.Message); }
        st.Update?.Invoke();
    }
}
