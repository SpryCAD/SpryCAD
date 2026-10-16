using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpryCad.App.UI;

/// <summary>Text built the same way wherever it is: names that can be long, which must never run past their room.</summary>
public static class Labels
{
    /// <summary>
    /// A name that may be long (a part's, a feature's, a page's title): one line, cut with an ellipsis where its room
    /// ends (or at <paramref name="maxWidth"/>), the whole name in its tooltip when <paramref name="tip"/>.
    /// </summary>
    public static TextBlock Trimmed(string text, double fontSize = TypeScale.Body, double maxWidth = double.PositiveInfinity, bool tip = true)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = fontSize, MaxWidth = maxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip) ToolTip.SetTip(t, text);
        return t;
    }
}
