using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.UI;
using SpryCad.Core.Library;
using SpryCad.Core.Model;

namespace SpryCad.App;

/// <summary>
/// Edit Material (as SolidWorks): a window beside the part, not blocking it, listing the materials the libraries offer
/// by type (a company's restrictions applied), the one picked shown with its properties in the part's units, its look
/// and where it comes from. Apply gives it to the part (one undo); Custom makes a material of the part's own from it
/// with another density; Edit in Library opens the library editor at its table.
/// </summary>
public partial class MainWindow
{
    Window? _materialWindow;
    /// <summary>The library materials given to parts lately, latest first (offered on the material row's menu).</summary>
    readonly List<string> _recentMaterials = new();

    /// <summary>The part's appearance: one of its own (color, finish, opacity), or its material's (the default grey with none).</summary>
    async Task PartAppearanceCommand()
    {
        if (IsAssemblyActive) { Status("Right-click a component for its appearance in the assembly."); return; }
        var doc = Session.Doc;
        var otherwise = doc.MaterialInfo?.Appearance ?? Appearance.Default;
        var choice = await AppearanceDialog.Show(this, "Appearance: " + Session.Title.TrimEnd(' ', '*'), "Part appearance, used wherever the part is used:",
            doc.Appearance, otherwise, doc.MaterialItem != null ? "Material's Appearance" : "Default Appearance");
        if (choice == null || IsAssemblyActive || Session.Doc != doc || choice.Look == doc.Appearance) return;
        Session.Change(() => doc.Appearance = choice.Look);
        Status(choice.Look == null ? "Appearance set to the material's." : $"Appearance set to {choice.Look.Name ?? "custom"}.");
    }

    /// <summary>Gives the active part a library material (null: none), one undo step.</summary>
    void SetPartMaterial(string? item)
    {
        if (IsAssemblyActive || item == Session.Doc.MaterialItem) return;
        Session.Change(() => PartMaterial.Set(Session.Doc, item));
        NoteRecentMaterial(item);
        Status(item == null ? "Material removed. Steel density is used for mass." : $"Material: {Session.Doc.MaterialName}.");
    }

    void NoteRecentMaterial(string? item)
    {
        if (item == null || PartMaterial.IsCustom(item)) return;
        _recentMaterials.Remove(item);
        _recentMaterials.Insert(0, item);
        if (_recentMaterials.Count > 8) _recentMaterials.RemoveAt(_recentMaterials.Count - 1);
    }

    void EditMaterialCommand()
    {
        if (IsAssemblyActive) { Status("A material is a part's: open the part to edit its material."); return; }
        if (_materialWindow != null) { _materialWindow.Activate(); return; }

        var search = new TextBox { Watermark = "Search materials", Margin = new Thickness(0, 0, 0, 6) };
        var tree = new TreeView { Width = 270, Height = 430, BorderBrush = Palette.FieldBorder, BorderThickness = new Thickness(1) };
        var details = new StackPanel { Spacing = 6, Width = 380 };
        var useMaterialLook = new CheckBox { Content = "Use material appearance" };
        var apply = new Button { Content = "Apply", IsDefault = true };
        var custom = new Button { Content = "Custom…" };
        var inLibrary = new Button { Content = "Edit in Library…" };
        var close = new Button { Content = "Close", IsCancel = true };
        ToolTip.SetTip(custom, "A material of the part's own: the selected material with another density (never offered for update)");
        ToolTip.SetTip(inLibrary, "Opens the selected material's table in the library editor (library owners).");

        // what is listed: (item, snapshot) by the libraries' order, and the part's own if they do not offer it
        string? selected = null;
        var rows = new Dictionary<string, LibrarySnapshot>(StringComparer.Ordinal);
        void List()
        {
            var doc = Session.Doc;
            var filter = (search.Text ?? "").Trim();
            rows.Clear();
            foreach (var t in LibraryLink.Current.Offered(TableKinds.Material))
                foreach (var r in t.Rows)
                {
                    var item = LibrarySnapshot.ItemKey(t.Key, r.Key);
                    if (!rows.ContainsKey(item) && LibraryLink.Current.Snapshot(item) is { } s) rows[item] = s;
                }
            if (doc.MaterialItem is { } own && !rows.ContainsKey(own) && LibraryLink.Resolve(doc, own) is { } os) rows[own] = os;
            selected ??= doc.MaterialItem;

            bool Matches(LibrarySnapshot s) => filter.Length == 0 || s.Row.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (s.Text?.Values.Any(v => v.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false);
            var groups = rows.Where(kv => Matches(kv.Value)).GroupBy(kv => Materials.Of(kv.Value)!.Type).OrderBy(g => g.Key);
            var items = new List<TreeViewItem>();
            TreeViewItem? pick = null;
            foreach (var g in groups)
            {
                var children = g.Select(kv =>
                {
                    var name = kv.Value.Row + (kv.Key == doc.MaterialItem ? "  (this part's)" : "");
                    var tvi = new TreeViewItem { Header = name, Tag = kv.Key };
                    if (kv.Key == selected) pick = tvi;
                    return tvi;
                }).ToList();
                // a group open when searching, or when it holds the part's material
                items.Add(new TreeViewItem
                {
                    Header = Materials.TypeName(g.Key), ItemsSource = children, FontWeight = FontWeight.SemiBold,
                    IsExpanded = filter.Length > 0 || children.Any(c => (string)c.Tag! == selected),
                });
                foreach (var c in children) c.FontWeight = FontWeight.Normal;
            }
            tree.ItemsSource = items;
            if (pick != null) tree.SelectedItem = pick;
            Show();
        }

        // the picked material: its properties in the part's units, its look, where it is from
        void Show()
        {
            details.Children.Clear();
            var doc = Session.Doc;
            if (selected == null || !rows.TryGetValue(selected, out var s) || Materials.Of(s) is not { } m)
            {
                details.Children.Add(new TextBlock { Text = doc.MaterialName == null ? "No material assigned. Steel density is used for mass." : "Select a material.", Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
                apply.IsEnabled = custom.IsEnabled = inLibrary.IsEnabled = false;
                useMaterialLook.IsVisible = doc.Appearance != null;
                return;
            }
            apply.IsEnabled = true; custom.IsEnabled = true;
            inLibrary.IsEnabled = !PartMaterial.IsCustom(selected);
            details.Children.Add(new TextBlock { Text = m.Name, FontSize = TypeScale.Title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading, TextWrapping = TextWrapping.Wrap });
            var table = LibraryLink.Current.Table(s.Table);
            var from = PartMaterial.IsCustom(selected) ? "Custom material (not from a library)"
                : $"{Materials.TypeName(m.Type)} · {(s.LibraryName.Length > 0 ? s.LibraryName : "library")}, {table?.Title ?? s.Table}";
            details.Children.Add(new TextBlock { Text = from, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap });
            if (table is { Verified: false }) details.Children.Add(new TextBlock { Text = $"{table.Standard}: not verified. Check the values before you rely on them.", Foreground = Palette.Warning, TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Small });

            // its look: a swatch and the finish
            if (m.Appearance is { } look)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                sp.Children.Add(Swatch(look.Color, 36, 18));
                sp.Children.Add(new TextBlock
                {
                    Text = $"{(look.Metallic >= 0.5 ? "Metal" : "Non-metal")}, {(look.Roughness < 0.25 ? "polished" : look.Roughness < 0.6 ? "satin" : "matte")}{(look.Opacity < 1 ? $", {look.Opacity * 100:0}% opaque" : "")}",
                    VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextMuted,
                });
                details.Children.Add(sp);
            }

            // its properties, in the part's units; a value not given shown as such
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
            int row = 0;
            void Line(string label, double? value, string column, string? note = null)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var (scale, unit) = Materials.Unit(column, doc.Units);
                var text = value is { } v ? (v / scale).ToString("G4", CultureInfo.InvariantCulture) : "—";
                var cells = new Control[]
                {
                    new TextBlock { Text = label, Foreground = Palette.TextMuted, Margin = new Thickness(0, 2, 16, 2) },
                    new TextBlock { Text = text + (note != null && value != null ? "  " + note : ""), Margin = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Right },
                    new TextBlock { Text = value != null ? unit : "", Foreground = Palette.TextMuted, Margin = new Thickness(6, 2, 0, 2) },
                };
                for (int c = 0; c < 3; c++) { Grid.SetRow(cells[c], row); Grid.SetColumn(cells[c], c); grid.Children.Add(cells[c]); }
                row++;
            }
            Line("Density", m.Density, "density");
            Line("Elastic modulus", m.Modulus, "modulus");
            Line("Poisson's ratio", m.Poisson, "poisson");
            Line("Shear modulus", m.Shear, "shear", m.Value("shear") == null ? "(from E and ν)" : null);
            Line("Yield strength", m.Yield, "yield");
            Line("Tensile strength", m.Tensile, "tensile");
            Line("Elongation at break", m.Elongation, "elongation");
            Line("Hardness" + (m.Text(Materials.HardnessScaleKey) is { } hs ? $" ({hs})" : ""), m.Hardness, "hardness");
            Line("Thermal expansion", m.Expansion, "expansion");
            Line("Thermal conductivity", m.Conductivity, "conductivity");
            Line("Specific heat", m.SpecificHeat, "specificHeat");
            details.Children.Add(grid);
            foreach (var (key, label) in new[] { (Materials.SpecKey, "Specification"), (Materials.CategoryKey, "Category"), (Materials.DescriptionKey, "Notes") })
                if (m.Text(key) is { } v) details.Children.Add(new TextBlock { Text = $"{label}: {v}", TextWrapping = TextWrapping.Wrap });
            useMaterialLook.IsVisible = doc.Appearance != null;
        }

        tree.SelectionChanged += (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: string item }) { selected = item; Show(); }
        };
        search.TextChanged += (_, _) => List();
        tree.DoubleTapped += (_, _) => { if (tree.SelectedItem is TreeViewItem { Tag: string }) Apply(); };

        void Apply()
        {
            if (IsAssemblyActive) { Status("A material is a part's: open the part to edit its material."); return; }
            var doc = Session.Doc;
            var clearLook = useMaterialLook.IsVisible && useMaterialLook.IsChecked == true && doc.Appearance != null;
            if (selected == null || (selected == doc.MaterialItem && !clearLook)) return;
            var s = rows[selected];
            Session.Change(() =>
            {
                // (the part's own, custom or no longer offered: kept as the part has it)
                if (selected != doc.MaterialItem)
                {
                    if (PartMaterial.IsCustom(selected) || LibraryLink.Current.Snapshot(selected) == null) PartMaterial.SetCustom(doc, s.Row, Materials.Of(s)!.Density, s);
                    else PartMaterial.Set(doc, selected);
                }
                if (clearLook) doc.Appearance = null;
            });
            useMaterialLook.IsChecked = false;
            NoteRecentMaterial(doc.MaterialItem);
            Status($"Material: {doc.MaterialName}.");
            List();
        }
        apply.Click += (_, _) => Apply();

        custom.Click += async (_, _) =>
        {
            if (selected == null || !rows.TryGetValue(selected, out var s) || Materials.Of(s) is not { } m || _materialWindow is not { } w) return;
            var name = (await Dialogs.Input(w, "Custom Material", "Material name:", PartMaterial.IsCustom(selected) ? m.Name : m.Name + ", custom density"))?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var (scale, unit) = Materials.Unit("density", Session.Doc.Units);
            var typed = await Dialogs.Input(w, "Custom Material", $"Density ({unit}):", (m.Density / scale).ToString("G6", CultureInfo.InvariantCulture));
            if (typed == null) return;
            if (!double.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !(d > 0) || !double.IsFinite(d))
            {
                await Dialogs.Message(w, "Custom Material", $"\"{typed.Trim()}\" is not a valid density. Enter a positive number in {unit}.");
                return;
            }
            if (IsAssemblyActive) return;
            var doc = Session.Doc;
            Session.Change(() => PartMaterial.SetCustom(doc, name, d * scale, s));
            selected = doc.MaterialItem;
            Status($"Material: {doc.MaterialName}.");
            List();
        };

        inLibrary.Click += async (_, _) =>
        {
            if (selected != null && rows.TryGetValue(selected, out var s)) await LibraryEditor.Open(this, s.Table, domain: LibraryDomain.Materials);
        };

        var left = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(tree);
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        body.Children.Add(left);
        body.Children.Add(new ScrollViewer { Content = details, MaxHeight = 470 });
        var bottom = Dialogs.ButtonRow(useMaterialLook, custom, inLibrary, apply, close);
        var content = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(bottom);
        content.Children.Add(body);

        var win = new Window
        {
            Title = "Material - " + Session.Title.TrimEnd(' ', '*'),
            SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, CanMinimize = false, CanMaximize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = Spacing.Dialog, Child = content },
        };
        Dialogs.Compact(win);
        close.Click += (_, _) => win.Close();
        win.Closed += (_, _) => _materialWindow = null;
        // back to the window after working on the part (or another part's tab): the part's material as it is now
        win.Activated += (_, _) =>
        {
            win.Title = "Material - " + Session.Title.TrimEnd(' ', '*');
            if (!IsAssemblyActive) List();
        };
        _materialWindow = win;
        List();
        win.Show(this);
    }

    /// <summary>A color (RGB 0-1) as a small swatch.</summary>
    static Border Swatch(double[] rgb, double width, double height)
    {
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(B(rgb[0]), B(rgb[1]), B(rgb[2]))),
            BorderBrush = Palette.TextMuted, BorderThickness = new Thickness(0.5), VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
