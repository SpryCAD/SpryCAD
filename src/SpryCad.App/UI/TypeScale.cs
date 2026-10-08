namespace SpryCad.App.UI;

/// <summary>
/// The app's four text sizes, for the window's chrome, panels and dialogs (UI/Styles.axaml takes them too). Not
/// for the graphics area's labels and glyphs, or the menus (the theme's).
/// </summary>
public static class TypeScale
{
    /// <summary>Secondary text: hints under a field, units, timings, paths, command labels.</summary>
    public const double Small = 11;
    /// <summary>Body text: tree rows, fields, list entries, dialogs.</summary>
    public const double Body = 12;
    /// <summary>A part's name in a list, a section's header.</summary>
    public const double Header = 13;
    /// <summary>A page's or a card's title.</summary>
    public const double Title = 15;
}
