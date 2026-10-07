using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace FeatherCad.App.UI;

/// <summary>
/// A tool window or dialog with FeatherCAD's own slim title bar (the title, a close button; drag it to move the
/// window) instead of the system's. The window keeps the system's border, shadow, rounded corners and resizing:
/// the content is extended over the title bar rather than the decorations removed. Where the platform cannot do
/// that (some Linux window managers), the system title bar shows and ours hides.
/// </summary>
public class ToolWindow : Window
{
    // the look of the context menus (Fluent): a near-white panel, a faint neutral border, 8 px corners
    static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#FFF9F9F9"));
    static readonly IBrush BarBrush = PanelBrush;
    static readonly IBrush BarLineBrush = new SolidColorBrush(Color.Parse("#FFE5E5E5"));
    static readonly IBrush CloseHoverBrush = new SolidColorBrush(Color.Parse("#FFE81123"));
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#FF2D3748"));

    readonly Border _bar;
    readonly TextBlock _title;
    readonly ContentControl _body = new();

    public ToolWindow()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        ShowInTaskbar = false;
        Background = PanelBrush;

        _title = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = 40, Height = 30, Padding = new Thickness(0), MinHeight = 0, CornerRadius = new CornerRadius(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => Close();
        // the system's red close button look
        close.PointerEntered += (_, _) => { close.Background = CloseHoverBrush; ((TextBlock)close.Content!).Foreground = Brushes.White; };
        close.PointerExited += (_, _) => { close.Background = Brushes.Transparent; ((TextBlock)close.Content!).Foreground = TextBrush; };
        var bar = new DockPanel { Height = 30 };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(_title);
        _bar = new Border { Child = bar, Background = BarBrush, BorderBrush = BarLineBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
        _bar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_bar).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e);
        };
        var root = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Top);
        root.Children.Add(_bar);
        root.Children.Add(_body);
        // a thin border of our own, where the system does not draw one (see OnOpened)
        _frame = new Border { Child = root, BorderBrush = FrameBrush, BorderThickness = new Thickness(1) };
        base.Content = _frame;
        _title.Text = Title;
    }

    /// <summary>The context menus' border (black at about 20% over white).</summary>
    static readonly IBrush FrameBrush = new SolidColorBrush(Color.Parse("#FFCCCCCC"));
    readonly Border _frame;

    /// <summary>The window's content, below the title bar.</summary>
    public Control? Body { get => _body.Content as Control; set => _body.Content = value; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) _title.Text = Title;
        // our title bar only where the system's is gone
        else if (change.Property == IsExtendedIntoWindowDecorationsProperty) _bar.IsVisible = IsExtendedIntoWindowDecorations;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _bar.IsVisible = IsExtendedIntoWindowDecorations;
        // Windows 11 rounds the window and draws its border itself (with the shadow, anti-aliased): ours then goes
        if (RoundedFrame(this)) _frame.BorderThickness = new Thickness(0);
    }

    // ---- Windows 11: rounded corners and a border color, by the desktop window manager ----

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;

    /// <summary>Asks Windows 11 for rounded corners (8 px, as the context menus) and a thin gray border; false where that is not available.</summary>
    static bool RoundedFrame(Window w)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        if (w.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return false;
        try
        {
            int corner = DWMWCP_ROUND;
            if (DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int)) != 0) return false;
            var c = ((ISolidColorBrush)FrameBrush).Color;
            int colorRef = c.R | (c.G << 8) | (c.B << 16); // COLORREF: 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            return true;
        }
        catch (Exception) { return false; } // no dwmapi: our own border stays
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Esc closes a tool window whose content did not use the key (a dialog's Cancel button handles it itself)
        if (!e.Handled && e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
