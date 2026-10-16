using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.Core.Library;
using SpryCad.Core.Model;

namespace SpryCad.App.UI;

/// <summary>
/// Appearance (a part's, or a component's in an assembly): a color, how metallic and how rough the surface is, how
/// opaque, with a lit preview; the presets the libraries offer (<see cref="TableKinds.Appearance"/>, a company's own
/// and restrictions with them); or none of its own, to show what it would otherwise (its material's, its part's).
/// </summary>
public static class AppearanceDialog
{
    /// <summary>What was chosen: an appearance, or (<c>Look</c> null) none of its own.</summary>
    public sealed record Choice(Appearance? Look);

    /// <param name="own">Its own appearance now (null: none).</param>
    /// <param name="otherwise">What it shows with none of its own, and how that is called ("the material's appearance").</param>
    /// <returns>The choice, or null when cancelled.</returns>
    public static async Task<Choice?> Show(Window owner, string title, string intro, Appearance? own, Appearance otherwise, string otherwiseName)
    {
        var look = own ?? otherwise with { Name = null };
        Choice? result = null;

        var preview = new Ellipse { Width = 96, Height = 96, Stroke = Palette.FieldBorder, StrokeThickness = 0.5 };
        var hex = new TextBox { Width = 90 };
        var colorSwatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Palette.FieldBorder, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        Slider MakeSlider() => new() { Minimum = 0, Maximum = 1, SmallChange = 0.05, LargeChange = 0.25, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        var metallic = MakeSlider(); var roughness = MakeSlider(); var opacity = MakeSlider();
        opacity.Minimum = 0.05;
        TextBlock Value() => new() { Width = 40, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextMuted };
        var metallicText = Value(); var roughnessText = Value(); var opacityText = Value();
        var presetName = new TextBlock { Foreground = Palette.TextMuted, FontSize = TypeScale.Small };
        var filling = false;

        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        static string Hex(double[] c) => $"#{B(c[0]):X2}{B(c[1]):X2}{B(c[2]):X2}";
        static double[]? ParseHex(string? s)
        {
            s = (s ?? "").Trim().TrimStart('#');
            return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? new[] { ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0 } : null;
        }

        void Fill()
        {
            filling = true;
            hex.Text = Hex(look.Color);
            metallic.Value = look.Metallic; roughness.Value = look.Roughness; opacity.Value = look.Opacity;
            filling = false;
            Draw();
        }
        void Draw()
        {
            colorSwatch.Background = new SolidColorBrush(Color.FromRgb(B(look.Color[0]), B(look.Color[1]), B(look.Color[2])));
            preview.Fill = Sphere(look);
            preview.Opacity = Math.Max(look.Opacity, 0.15);
            metallicText.Text = look.Metallic.ToString("0.00", CultureInfo.InvariantCulture);
            roughnessText.Text = look.Roughness.ToString("0.00", CultureInfo.InvariantCulture);
            opacityText.Text = $"{look.Opacity * 100:0}%";
            presetName.Text = look.Name ?? "";
        }
        hex.TextChanged += (_, _) => { if (!filling && ParseHex(hex.Text) is { } c && !c.SequenceEqual(look.Color)) { look = look with { Color = c, Name = null }; Draw(); } };
        metallic.ValueChanged += (_, _) => { if (!filling) { look = look with { Metallic = Math.Round(metallic.Value, 2), Name = null }; Draw(); } };
        roughness.ValueChanged += (_, _) => { if (!filling) { look = look with { Roughness = Math.Round(roughness.Value, 2), Name = null }; Draw(); } };
        opacity.ValueChanged += (_, _) => { if (!filling) { look = look with { Opacity = Math.Round(opacity.Value, 2), Name = null }; Draw(); } };

        // the presets: the libraries' appearances, by name
        var presets = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 420 };
        foreach (var t in LibraryLink.Current.Offered(TableKinds.Appearance))
            foreach (var r in t.Rows)
            {
                if (Materials.AppearanceOf(r.Key, t.Columns, r.Values) is not { } p) continue;
                var b = new Button
                {
                    Width = 30, Height = 30, Margin = new Thickness(2), Padding = new Thickness(2),
                    Content = new Ellipse { Width = 24, Height = 24, Fill = Sphere(p), Opacity = Math.Max(p.Opacity, 0.15) },
                };
                ToolTip.SetTip(b, p.Name);
                b.Click += (_, _) => { look = p; Fill(); };
                presets.Children.Add(b);
            }

        Control Row(string label, Control c, Control? after = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextMuted });
            sp.Children.Add(c);
            if (after != null) sp.Children.Add(after);
            return sp;
        }
        var controls = new StackPanel { Spacing = 6 };
        controls.Children.Add(Row("Color", hex, colorSwatch));
        controls.Children.Add(Row("Metallic", metallic, metallicText));
        controls.Children.Add(Row("Roughness", roughness, roughnessText));
        controls.Children.Add(Row("Opacity", opacity, opacityText));
        ToolTip.SetTip(hex, "The color as #RRGGBB");
        ToolTip.SetTip(metallic, "0: plastic or paint; 1: bare metal (its highlights in its own color)");
        ToolTip.SetTip(roughness, "0: polished (small, bright highlights); 1: matte");
        ToolTip.SetTip(opacity, "100%: solid; less: see-through (glass, clear plastic)");
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        top.Children.Add(new StackPanel { Spacing = 4, Children = { preview, presetName } });
        top.Children.Add(controls);

        var useOther = new Button { Content = "Use " + otherwiseName, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ToolTip.SetTip(useOther, $"No appearance of its own: {otherwiseName} shows");

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(top);
        content.Children.Add(new TextBlock { Text = "Presets", FontWeight = FontWeight.SemiBold });
        content.Children.Add(presets);
        var w = Dialogs.MakeWindow(title, Dialogs.WithButtons(content, useOther, ok, cancel), 470);
        ok.Click += (_, _) =>
        {
            if (ParseHex(hex.Text) == null) { hex.Focus(); return; }
            result = new Choice(look);
            w.Close();
        };
        useOther.Click += (_, _) => { result = new Choice(null); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        Fill();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// A lit ball in the appearance, roughly as the graphics area lights it: a highlight up and to the left, tighter
    /// and brighter the smoother, in the color for a metal; the color; its shadow side darker (a metal's darker still).
    /// </summary>
    public static IBrush Sphere(Appearance a)
    {
        var c = a.Color;
        double rough = Math.Clamp(a.Roughness, 0, 1), metal = Math.Clamp(a.Metallic, 0, 1);
        double spec = (1.3 - rough) * (0.45 + 0.4 * metal);
        Color Mix(double k, double light) =>
            Color.FromRgb(B(c[0] * k + light * (1 - metal + metal * c[0])), B(c[1] * k + light * (1 - metal + metal * c[1])), B(c[2] * k + light * (1 - metal + metal * c[2])));
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            Center = new RelativePoint(0.42, 0.38, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(1.0, spec), 0),
                new GradientStop(Mix(1.0 - 0.1 * metal, 0), 0.12 + 0.3 * rough),
                new GradientStop(Mix(0.75 - 0.25 * metal, 0), 0.7),
                new GradientStop(Mix(0.35 - 0.15 * metal, 0), 1),
            },
        };
    }
}
