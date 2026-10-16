using Avalonia;

namespace SpryCad.App.UI;

/// <summary>
/// The app's spacing steps, as <see cref="TypeScale"/> is its text sizes: gaps between things and the room inside
/// them, for the window's chrome, panels and dialogs. Not for the graphics area's labels and glyphs.
/// </summary>
public static class Spacing
{
    /// <summary>Between a thing and its own part: an icon and its badge, a field's rows.</summary>
    public const double XS = 2;
    /// <summary>Between an icon and its name, a label and its field.</summary>
    public const double S = 4;
    /// <summary>Between buttons in a row, controls in a line.</summary>
    public const double M = 8;
    /// <summary>Between a dialog's body and its buttons; inside a dialog's edge.</summary>
    public const double L = 12;
    /// <summary>Between a dialog's columns or groups.</summary>
    public const double XL = 16;

    /// <summary>A dialog window's room inside its edge.</summary>
    public static Thickness Dialog => new(L);
    /// <summary>A dialog's footer bar (the buttons under a rule, as Options'): its room inside.</summary>
    public static Thickness FooterBar => new(L, M);
}
