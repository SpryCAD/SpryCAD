using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.Session;
using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// Mass Properties (as SolidWorks): a window beside the part, not blocking it, with the part's material and
/// density (saved with it), and its mass, volume, surface area, center of mass (also shown on the part),
/// principal axes and moments of inertia, and the inertia tensors at the center of mass and at the origin.
/// Computed on the refined display mesh, which follows the exact surfaces (exact for planar parts); updates
/// when the part rebuilds.
/// </summary>
public partial class MainWindow
{
    Window? _massWindow;
    SelectableTextBlock? _massReport;
    DocumentSession? _massSession;
    string _massText = "";

    /// <summary>
    /// The materials the libraries offer (built-in and company ones, a company's restrictions applied), by name with
    /// their items, in the libraries' order; a name in two tables once, the first.
    /// </summary>
    static List<(string name, string item)> OfferedMaterials() =>
        LibraryLink.Current.Offered(TableKinds.Material).SelectMany(t => t.Rows.Select(r => (name: r.Key, item: LibrarySnapshot.ItemKey(t.Key, r.Key))))
            .GroupBy(m => m.name).Select(g => g.First()).ToList();
    /// <summary>What a part with no material is listed as (steel's density assumed, <see cref="CadDocument.DefaultDensity"/>).</summary>
    const string NoMaterial = "Not specified";
    const double KgM3ToLbIn3 = 3.6127292e-5;

    static IBrush LabelBrush => Palette.TextMuted;

    void MassPropertiesCommand()
    {
        // an assembly's: every part with its own material
        if (Asm != null) { AssemblyMassCommand(); return; }
        if (_massWindow != null) { _massWindow.Activate(); return; }

        // material and density
        var materials = OfferedMaterials();
        var names = new List<string>();
        var combo = new ComboBox { Width = 220, MaxDropDownHeight = 420 };
        var dens = new TextBox { Width = 100 };
        var unit = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush };
        var filling = false;
        void Fill()
        {
            filling = true;
            // the libraries' materials as they are now, and the part's own (custom, or no longer offered)
            materials = OfferedMaterials();
            var list = materials.Select(x => x.name).Prepend(NoMaterial).ToList();
            var own = Session.Doc.MaterialName;
            if (own != null && !list.Contains(own)) list.Add(own);
            if (!list.SequenceEqual(names)) { names = list; combo.ItemsSource = names; }
            var ips = Session.Doc.Units == UnitSystem.IPS;
            var d = Session.Doc.EffectiveDensity;
            combo.SelectedItem = own ?? NoMaterial;
            dens.Text = (ips ? d * KgM3ToLbIn3 : d).ToString(ips ? "0.######" : "0.##", CultureInfo.InvariantCulture);
            unit.Text = ips ? "lb/in³" : "kg/m³";
            filling = false;
        }
        void Changed(Action change)
        {
            Session.Change(change);
            Fill();
            UpdateMassProperties();
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (filling || combo.SelectedItem is not string name || name == Session.Doc.MaterialName) return;
            if (name == NoMaterial) { if (Session.Doc.MaterialItem != null) Changed(() => PartMaterial.Set(Session.Doc, null)); return; }
            // (the part's own, custom or no longer offered, is listed as it is: nothing to take)
            if (materials.FindIndex(x => x.name == name) is var i && i >= 0) Changed(() => PartMaterial.Set(Session.Doc, materials[i].item));
        };
        void ApplyDensity()
        {
            if (filling) return;
            var ips = Session.Doc.Units == UnitSystem.IPS;
            if (!double.TryParse(dens.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) { Fill(); return; }
            var kgm3 = ips ? v / KgM3ToLbIn3 : v;
            if (Math.Abs(Session.Doc.EffectiveDensity - kgm3) < 1e-6 * kgm3) return;
            // a typed density is a material of the part's own: the material's other properties and look kept
            var doc = Session.Doc;
            var basedOn = doc.MaterialItem != null ? LibraryLink.Resolve(doc, doc.MaterialItem) : null;
            var name = doc.MaterialName is not { } n ? PartMaterial.CustomName
                : PartMaterial.IsCustom(doc.MaterialItem) ? n : n + ", custom density";
            Changed(() => PartMaterial.SetCustom(doc, name, kgm3, basedOn));
        }
        dens.LostFocus += (_, _) => ApplyDensity();
        dens.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyDensity(); e.Handled = true; } };
        Fill();

        var materialRow = new DockPanel();
        var options = new Button { Content = "Options...", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        options.Click += async (_, _) => { if (_massWindow != null && await MassOptions(_massWindow)) UpdateMassProperties(); };
        DockPanel.SetDock(options, Dock.Right);
        materialRow.Children.Add(options);
        var mat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        mat.Children.Add(new TextBlock { Text = "Material", VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush });
        mat.Children.Add(combo);
        mat.Children.Add(new TextBlock { Text = "Density", VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush, Margin = new Thickness(8, 0, 0, 0) });
        mat.Children.Add(dens);
        mat.Children.Add(unit);
        materialRow.Children.Add(mat);

        // the report: plain text on white, as a printout (SolidWorks' report box)
        _massReport = new SelectableTextBlock { FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Foreground = Palette.Text };
        var reportBox = new Border
        {
            Background = Palette.Surface, BorderBrush = Palette.FieldBorder, BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(10, 8), Child = _massReport },
                MaxHeight = 560, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };
        var copy = new Button { Content = "Copy to Clipboard" };
        copy.Click += (_, _) => { if (_massText.Length > 0) _ = Clipboard?.SetTextAsync(_massText); };
        var close = new Button { Content = "Close", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { copy, close }) { b.MinWidth = 80; b.HorizontalContentAlignment = HorizontalAlignment.Center; buttons.Children.Add(b); }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(materialRow);
        content.Children.Add(reportBox);
        content.Children.Add(buttons);
        var w = new Window
        {
            Title = "Mass Properties - " + Session.Title.TrimEnd(' ', '*'),
            Width = 640, SizeToContent = SizeToContent.Height, CanResize = false, CanMinimize = false, CanMaximize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(12), Child = content },
        };
        Dialogs.Compact(w);
        close.Click += (_, _) => w.Close();
        w.Closed += (_, _) => EndMassProperties();
        // back to the window after working on the part (or another part's tab): up to date
        w.Activated += (_, _) => { if (_massSession != Session) WatchMassSession(); Fill(); UpdateMassProperties(); };
        _massWindow = w;
        WatchMassSession();
        UpdateMassProperties();
        w.Show(this);
    }

    void WatchMassSession()
    {
        if (_massSession != null) _massSession.Rebuilt -= UpdateMassProperties;
        _massSession = Session;
        _massSession.Rebuilt += UpdateMassProperties;
    }

    /// <summary>The window closed: so does the center of mass symbol.</summary>
    void EndMassProperties()
    {
        if (_massSession != null) _massSession.Rebuilt -= UpdateMassProperties;
        Viewport.CenterOfMass = null;
        _massWindow = null; _massReport = null; _massSession = null; _massText = "";
    }

    // report formatting (the Options dialog, as SolidWorks'): decimal places, scientific notation; kept while the app runs
    static int _massDecimals = 4;
    static bool _massScientific;

    /// <summary>The report options. True when they changed.</summary>
    static async Task<bool> MassOptions(Window owner)
    {
        var decimals = new NumericUpDown { Minimum = 0, Maximum = 8, Increment = 1, Value = _massDecimals, FormatString = "0", Width = 110 };
        var sci = new CheckBox { Content = "Scientific notation", IsChecked = _massScientific };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Decimal places", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(decimals);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(new TextBlock { Text = "Report", FontWeight = FontWeight.SemiBold });
        sp.Children.Add(row); sp.Children.Add(sci); sp.Children.Add(buttons);
        var w = new Window
        {
            Title = "Mass Properties Options", Width = 300, SizeToContent = SizeToContent.Height, CanResize = false, CanMinimize = false, CanMaximize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(14), Child = sp },
        };
        Dialogs.Compact(w);
        var changed = false;
        ok.Click += (_, _) =>
        {
            var d = (int)(decimals.Value ?? 4);
            changed = d != _massDecimals || (sci.IsChecked == true) != _massScientific;
            _massDecimals = d; _massScientific = sci.IsChecked == true;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return changed;
    }

    void UpdateMassProperties()
    {
        if (_massReport == null) return;
        var bodies = Session.DisplayResult?.State.Bodies ?? new List<Body>();
        // the report as lines (bold: headings), shown on white in a fixed-width font and copied as text
        var report = new List<(string text, bool bold)>();
        void Show()
        {
            _massReport.Inlines!.Clear();
            for (int i = 0; i < report.Count; i++)
            {
                if (i > 0) _massReport.Inlines.Add(new LineBreak());
                _massReport.Inlines.Add(new Run(report[i].text) { FontWeight = report[i].bold ? FontWeight.Bold : FontWeight.Normal });
            }
            _massText = string.Join(Environment.NewLine, report.Select(x => x.text));
        }
        if (bodies.Count == 0)
        {
            report.Add(("No solid bodies.", false));
            Show();
            Viewport.CenterOfMass = null;
            return;
        }
        // exact (integrated on the B-rep) for native bodies; the display mesh for any other
        var mp = new MassProps();
        var bb = Box3.Empty;
        foreach (var b in bodies)
        {
            if (b.Solid.Native is { } nb && SpryCad.Kernel.Native.NMass.Integrate(nb) is { } ex) mp.AddIntegrals(ex.S, ex.Area);
            else mp.Add(b.Mesh.Positions, b.Mesh.Indices);
            bb.Add(b.Mesh.Bounds);
        }
        mp.Finish();

        var ips = Session.Doc.Units == UnitSystem.IPS;
        var u = Session.Doc.Units.Symbol();
        var kgm3 = Session.Doc.EffectiveDensity;
        var density = ips ? kgm3 * KgM3ToLbIn3 : kgm3 * 1e-6; // lb/in³, or g/mm³
        // the model is in millimetres: lengths shown in the part's units (k per mm), so volumes k³, inertias k⁵
        var k = 1 / Session.Doc.Units.MmPer();
        double k3 = k * k * k, k5 = k3 * k * k;
        var inertia = density * k5;
        var massUnit = ips ? "lb" : "g";
        var iu = $"{massUnit}·{u}²";
        // every value with the same decimals; anything that rounds to zero is 0 (no -0, no 1E-15 noise)
        var dec = _massDecimals;
        string F(double v)
        {
            if (_massScientific) return v.ToString("0." + new string('0', Math.Max(1, dec)) + "E+0", CultureInfo.InvariantCulture);
            var r = Math.Round(v, dec);
            if (r == 0) r = 0; // -0
            return r.ToString(dec == 0 ? "0" : "0." + new string('0', dec), CultureInfo.InvariantCulture);
        }
        // values far below the part's own scale are noise: 0 (rounding does not hide it in scientific notation)
        double Clean(double v, double scale) => Math.Abs(v) < 1e-9 * Math.Max(scale, 1e-300) ? 0 : v;

        void Line(string s = "", bool bold = false) => report.Add((s, bold));
        // rows of cells, each column padded to its widest cell so the columns line up
        void Columns(IEnumerable<string[]> rows, string indent = "    ")
        {
            var list = rows.ToList();
            var widths = Enumerable.Range(0, list.Max(r => r.Length)).Select(c => list.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
            foreach (var r in list)
                Line(indent + string.Join("    ", r.Select((cell, c) => c < r.Length - 1 ? cell.PadRight(widths[c]) : cell)));
        }

        var dShown = (ips ? kgm3 * KgM3ToLbIn3 : kgm3).ToString(ips ? "0.######" : "0.##", CultureInfo.InvariantCulture);
        Line($"Mass properties of {Session.Title.TrimEnd(' ', '*')}", true);
        Line($"    Material: {Session.Doc.MaterialName ?? "Not specified (steel's density assumed)"}");
        if (bodies.Count > 1) Line($"    Bodies: {bodies.Count}");
        Line();
        Line($"Density = {dShown} {(ips ? "lb/in³" : "kg/m³")}");
        Line($"Mass = {F(mp.Volume * k3 * density)} {massUnit}");
        Line($"Volume = {F(mp.Volume * k3)} {u}³");
        Line($"Surface area = {F(mp.SurfaceArea * k * k)} {u}²");
        Line($"Bounding box = {F(bb.Size.X * k)} × {F(bb.Size.Y * k)} × {F(bb.Size.Z * k)} {u}");

        var c0 = mp.Center;
        var size = Math.Max(bb.Diagonal, 1e-9);
        Line();
        Line($"Center of mass: ({u})", true);
        Line($"    X = {F(Clean(c0.X, size) * k)}");
        Line($"    Y = {F(Clean(c0.Y, size) * k)}");
        Line($"    Z = {F(Clean(c0.Z, size) * k)}");

        Line();
        Line($"Principal axes of inertia and principal moments of inertia: ({iu})", true);
        Line("Taken at the center of mass.");
        string[] pn = { "Ix", "Iy", "Iz" }, pm = { "Px", "Py", "Pz" };
        var biggest = Math.Max(mp.Principal.Max(Math.Abs), 1e-300) * inertia;
        string A(double v) => (v >= 0 ? " " : "") + F(Clean(v, 1)); // signs in their own place, so the axes line up
        Columns(Enumerable.Range(0, 3).Select(i =>
        {
            var a = mp.Axes[i];
            return new[] { $"{pn[i]} = ({A(a.X)}, {A(a.Y)}, {A(a.Z)})", $"{pm[i]} = {F(Clean(mp.Principal[i] * inertia, biggest))}" };
        }));

        // as SolidWorks shows them: moments on the diagonal, products of inertia (∫xy dm) off it
        void Tensor(double[,] t, string p)
        {
            var scale = Math.Max(Math.Max(Math.Abs(t[0, 0]), Math.Abs(t[1, 1])), Math.Abs(t[2, 2])) * inertia;
            string[] ax = { "x", "y", "z" };
            Columns(Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3)
                .Select(j => $"{p}{ax[i]}{ax[j]} = {F(Clean((i == j ? t[i, j] : -t[i, j]) * inertia, scale))}").ToArray()));
        }
        Line();
        Line($"Moments of inertia: ({iu})", true);
        Line("Taken at the center of mass and aligned with the world axes.");
        Tensor(mp.AtCenter, "L");
        Line();
        Line($"Moments of inertia: ({iu})", true);
        Line("Taken at the origin, world axes.");
        Tensor(mp.AtOrigin, "I");

        Line();
        if (bodies.Any(b => b.Solid.Topology.OpenHalfEdges != 0)) Line("⚠ A body is not closed (watertight): these results are not reliable.");
        Line("Products of inertia (Lxy etc.) are ∫xy dm.");
        Line("Computed on the display mesh, which follows the exact surfaces.");
        Show();
        if (_massWindow != null) _massWindow.Title = "Mass Properties - " + Session.Title.TrimEnd(' ', '*');
        // the center of mass symbol, and the principal axes a quarter of the part's size to either side
        Viewport.CenterOfMass = (c0, mp.Axes, Math.Max(bb.Diagonal * 0.25, 1e-6));
    }
}
