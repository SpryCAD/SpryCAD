using Avalonia;
using Avalonia.Media;

namespace SpryCad.App.UI;

/// <summary>
/// The app's named colors (UI/Palette.axaml) for controls built in code. Looked up each time, for the theme
/// variant in use; a name missing from the palette shows magenta, so it is seen.
/// </summary>
public static class Palette
{
    static IBrush Get(string key)
        => Application.Current is { } app && app.TryGetResource("Spry." + key, app.ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Magenta;

    public static IBrush Surface => Get(nameof(Surface));
    public static IBrush SurfaceAlt => Get(nameof(SurfaceAlt));
    public static IBrush Panel => Get(nameof(Panel));
    public static IBrush Window => Get(nameof(Window));
    public static IBrush Strip => Get(nameof(Strip));
    public static IBrush StripItemHover => Get(nameof(StripItemHover));
    public static IBrush StripItem => Get(nameof(StripItem));
    public static IBrush Hud => Get(nameof(Hud));
    public static IBrush Scrim => Get(nameof(Scrim));

    public static IBrush Border => Get(nameof(Border));
    public static IBrush BorderStrong => Get(nameof(BorderStrong));
    public static IBrush FieldBorder => Get(nameof(FieldBorder));

    public static IBrush Accent => Get(nameof(Accent));
    public static IBrush AccentLight => Get(nameof(AccentLight));
    public static IBrush AccentText => Get(nameof(AccentText));
    public static IBrush HoverBg => Get(nameof(HoverBg));
    public static IBrush HoverBorder => Get(nameof(HoverBorder));
    public static IBrush SelectedBg => Get(nameof(SelectedBg));
    public static IBrush SelectedBorder => Get(nameof(SelectedBorder));
    public static IBrush SelectedText => Get(nameof(SelectedText));

    public static IBrush Text => Get(nameof(Text));
    public static IBrush Heading => Get(nameof(Heading));
    public static IBrush TextSecondary => Get(nameof(TextSecondary));
    public static IBrush TextMuted => Get(nameof(TextMuted));
    public static IBrush TextDisabled => Get(nameof(TextDisabled));
    public static IBrush Brand => Get(nameof(Brand));

    public static IBrush Danger => Get(nameof(Danger));
    public static IBrush Success => Get(nameof(Success));
    public static IBrush Warning => Get(nameof(Warning));
    public static IBrush NoteBg => Get(nameof(NoteBg));
    public static IBrush NoteBorder => Get(nameof(NoteBorder));
    public static IBrush NoteText => Get(nameof(NoteText));
}
