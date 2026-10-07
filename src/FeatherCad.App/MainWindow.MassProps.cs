using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.App.Session;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.App;

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
    StackPanel? _massReport;
    DocumentSession? _massSession;
    string _massText = "";

    /// <summary>Common materials and their densities (kg/m³).</summary>
    static readonly (string name, double density)[] Materials =
    {
        ("Steel (plain carbon)", 7850), ("Stainless steel", 8000), ("Cast iron", 7200), ("Aluminum 6061", 2700),
        ("Brass", 8500), ("Copper", 8940), ("Titanium Ti-6Al-4V", 4430), ("ABS", 1050), ("PLA", 1240),
        ("Nylon 6/6", 1140), ("Acetal (POM)", 1410), ("Polycarbonate", 1200),
    };
    const string CustomMaterial = "Custom";
    const double KgM3ToLbIn3 = 3.6127292e-5;

    static readonly IBrush SectionBrush = new SolidColorBrush(Color.FromRgb(29, 53, 87));
    static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(90, 100, 115));

    void MassPropertiesCommand()
    {
        if (_massWindow != null) { _massWindow.Activate(); return; }

        // material and density
        var names = Materials.Select(x => x.name).Append(CustomMaterial).ToList();
        var combo = new ComboBox { ItemsSource = names, Width = 220 };
        var dens = new TextBox { Width = 100 };
        var unit = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush };
        var filling = false;
        void Fill()
        {
            filling = true;
            var ips = Session.Doc.Units == UnitSystem.IPS;
            var d = Session.Doc.Density ?? CadDocument.DefaultDensity;
            combo.SelectedItem = Session.Doc.Material is { } mat && names.Contains(mat) ? mat
                : Session.Doc.Density == null ? Materials[0].name : CustomMaterial;
            dens.Text = (ips ? d * KgM3ToLbIn3 : d).ToString(ips ? "0.######" : "0.##", CultureInfo.InvariantCulture);
            unit.Text = ips ? "lb/in³" : "kg/m³";
            filling = false;
        }
        void SetMaterial(string? name, double kgm3)
        {
            if (Session.Doc.Material == name && Session.Doc.Density == kgm3) return;
            Session.Change(() => { Session.Doc.Material = name; Session.Doc.Density = kgm3; });
            Fill();
            UpdateMassProperties();
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (filling || combo.SelectedItem is not string name || name == CustomMaterial) return;
            SetMaterial(name, Materials.First(x => x.name == name).density);
        };
        void ApplyDensity()
        {
            if (filling) return;
            var ips = Session.Doc.Units == UnitSystem.IPS;
            if (!double.TryParse(dens.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) { Fill(); return; }
            var kgm3 = ips ? v / KgM3ToLbIn3 : v;
            if (Math.Abs((Session.Doc.Density ?? CadDocument.DefaultDensity) - kgm3) < 1e-9 * kgm3 && Session.Doc.Density != null) return;
            // a typed density that is no listed material's is a custom material
            var preset = Materials.FirstOrDefault(x => Math.Abs(x.density - kgm3) < 1e-6 * kgm3);
            SetMaterial(preset.name ?? CustomMaterial, kgm3);
        }
        dens.LostFocus += (_, _) => ApplyDensity();
        dens.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyDensity(); e.Handled = true; } };
        Fill();

        var materialRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        materialRow.Children.Add(new TextBlock { Text = "Material", VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush });
        materialRow.Children.Add(combo);
        materialRow.Children.Add(new TextBlock { Text = "Density", VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush, Margin = new Thickness(8, 0, 0, 0) });
        materialRow.Children.Add(dens);
        materialRow.Children.Add(unit);

        // report options (as SolidWorks' Options...): decimal places, scientific notation
        var decimals = new NumericUpDown { Minimum = 0, Maximum = 8, Increment = 1, Value = _massDecimals, FormatString = "0", Width = 110 };
        decimals.ValueChanged += (_, e) => { _massDecimals = (int)(e.NewValue ?? 4); UpdateMassProperties(); };
        var sci = new CheckBox { Content = "Scientific notation", IsChecked = _massScientific, Margin = new Thickness(12, 0, 0, 0) };
        sci.IsCheckedChanged += (_, _) => { _massScientific = sci.IsChecked == true; UpdateMassProperties(); };
        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        optionsRow.Children.Add(new TextBlock { Text = "Decimal places", VerticalAlignment = VerticalAlignment.Center, Foreground = LabelBrush });
        optionsRow.Children.Add(decimals);
        optionsRow.Children.Add(sci);

        _massReport = new StackPanel { Spacing = 2 };
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => { if (_massText.Length > 0) _ = Clipboard?.SetTextAsync(_massText); };
        var close = new Button { Content = "Close", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { copy, close }) { b.MinWidth = 80; b.HorizontalContentAlignment = HorizontalAlignment.Center; buttons.Children.Add(b); }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(materialRow);
        content.Children.Add(optionsRow);
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(214, 220, 228)) });
        content.Children.Add(_massReport);
        content.Children.Add(buttons);
        var w = new Window
        {
            Title = "Mass Properties - " + Session.Title.TrimEnd(' ', '*'),
            Width = 660, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(16), Child = content },
        };
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

    // report formatting (as SolidWorks' Options): decimal places and scientific notation; kept while the app runs
    static int _massDecimals = 4;
    static bool _massScientific;

    void UpdateMassProperties()
    {
        if (_massReport == null) return;
        _massReport.Children.Clear();
        var bodies = Session.DisplayResult?.State.Bodies ?? new List<Body>();
        if (bodies.Count == 0)
        {
            _massReport.Children.Add(new TextBlock { Text = "No solid bodies.", Foreground = LabelBrush });
            Viewport.CenterOfMass = null; _massText = "";
            return;
        }
        var mp = new MassProps();
        var bb = Box3.Empty;
        foreach (var b in bodies) { mp.Add(b.Mesh.Positions, b.Mesh.Indices); bb.Add(b.Mesh.Bounds); }
        mp.Finish();

        var ips = Session.Doc.Units == UnitSystem.IPS;
        var u = UnitLabel;
        var kgm3 = Session.Doc.Density ?? CadDocument.DefaultDensity;
        var density = ips ? kgm3 * KgM3ToLbIn3 : kgm3 * 1e-6; // model units: lb/in³, or g/mm³
        var massUnit = ips ? "lb" : "g";
        var iu = $"{massUnit}·{u}²";
        // every value with the same decimals; anything that rounds to zero is 0 (no -0, no 1E-15 noise)
        var dec = _massDecimals;
        string F(double v)
        {
            if (_massScientific) return v == 0 ? 0.ToString("0." + new string('0', Math.Max(1, dec)) + "E+0", CultureInfo.InvariantCulture)
                : v.ToString("0." + new string('0', Math.Max(1, dec)) + "E+0", CultureInfo.InvariantCulture);
            var r = Math.Round(v, dec);
            if (r == 0) r = 0; // -0
            return r.ToString(dec == 0 ? "0" : "0." + new string('0', dec), CultureInfo.InvariantCulture);
        }
        // in scientific notation, noise is not rounded away: clear values far below the part's own scale
        double Clean(double v, double scale) => Math.Abs(v) < 1e-9 * Math.Max(scale, 1e-300) ? 0 : v;

        var text = new System.Text.StringBuilder();
        text.AppendLine($"Mass properties of {Session.Title.TrimEnd(' ', '*')}");
        void Section(string title, string? sub = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = SectionBrush });
            if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = LabelBrush });
            _massReport.Children.Add(sp);
            text.AppendLine().AppendLine(title + (sub != null ? "  " + sub : ""));
        }
        // cells "Name = value" in even columns
        void Cells(IEnumerable<string[]> rows, int columns, double width = 200)
        {
            var g = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            for (int c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition(width, GridUnitType.Pixel));
            int r = 0;
            foreach (var row in rows)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < row.Length; c++)
                {
                    var tb = new SelectableTextBlock { Text = row[c], Margin = new Thickness(0, 1, 12, 1), FontFamily = new FontFamily("Consolas, Menlo, monospace") };
                    Grid.SetRow(tb, r); Grid.SetColumn(tb, c); g.Children.Add(tb);
                }
                text.AppendLine("  " + string.Join("   ", row));
                r++;
            }
            _massReport.Children.Add(g);
        }

        var mass = mp.Volume * density;
        Section("Overview");
        Cells(new[]
        {
            new[] { $"Density = {(ips ? kgm3 * KgM3ToLbIn3 : kgm3).ToString(ips ? "0.######" : "0.##", CultureInfo.InvariantCulture)} {(ips ? "lb/in³" : "kg/m³")}" },
            new[] { $"Mass = {F(mass)} {massUnit}" },
            new[] { $"Volume = {F(mp.Volume)} {u}³" },
            new[] { $"Surface area = {F(mp.SurfaceArea)} {u}²" },
            new[] { $"Bounding box = {F(bb.Size.X)} × {F(bb.Size.Y)} × {F(bb.Size.Z)} {u}" },
        }.Concat(bodies.Count > 1 ? new[] { new[] { $"Bodies = {bodies.Count}" } } : Array.Empty<string[]>()), 1, 600);

        var c0 = mp.Center;
        var size = Math.Max(bb.Diagonal, 1e-9);
        Section($"Center of mass ({u})");
        Cells(new[] { new[] { $"X = {F(Clean(c0.X, size))}", $"Y = {F(Clean(c0.Y, size))}", $"Z = {F(Clean(c0.Z, size))}" } }, 3);

        Section($"Principal axes of inertia and principal moments ({iu})", "Taken at the center of mass.");
        string[] pn = { "Ix", "Iy", "Iz" }, pm = { "Px", "Py", "Pz" };
        var biggest = Math.Max(mp.Principal.Max(Math.Abs), 1e-300) * density;
        Cells(Enumerable.Range(0, 3).Select(i =>
        {
            var a = mp.Axes[i];
            return new[] { $"{pn[i]} = ({F(Clean(a.X, 1))}, {F(Clean(a.Y, 1))}, {F(Clean(a.Z, 1))})", $"{pm[i]} = {F(Clean(mp.Principal[i] * density, biggest))}" };
        }), 2, 300);

        // as SolidWorks shows them: moments on the diagonal, products of inertia (∫xy dm) off it
        void Tensor(double[,] t, string p)
        {
            var scale = Math.Max(Math.Max(Math.Abs(t[0, 0]), Math.Abs(t[1, 1])), Math.Abs(t[2, 2])) * density;
            string[] ax = { "x", "y", "z" };
            Cells(Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3)
                .Select(j => $"{p}{ax[i]}{ax[j]} = {F(Clean((i == j ? t[i, j] : -t[i, j]) * density, scale))}").ToArray()), 3);
        }
        Section($"Moments of inertia ({iu})", "Taken at the center of mass and aligned with the world axes.");
        Tensor(mp.AtCenter, "L");
        Section($"Moments of inertia ({iu})", "Taken at the origin, world axes.");
        Tensor(mp.AtOrigin, "I");

        var note = "Products of inertia (Lxy etc.) are ∫xy dm. Computed on the display mesh, which follows the exact surfaces.";
        if (bodies.Any(b => b.Solid.Topology.OpenHalfEdges != 0)) note = "⚠ A body is not closed (watertight): these results are not reliable.  " + note;
        _massReport.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = LabelBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        _massText = text.ToString();
        if (_massWindow != null) _massWindow.Title = "Mass Properties - " + Session.Title.TrimEnd(' ', '*');
        // the center of mass symbol, and the principal axes a quarter of the part's size to either side
        Viewport.CenterOfMass = (c0, mp.Axes, Math.Max(bb.Diagonal * 0.25, 1e-6));
    }
}
