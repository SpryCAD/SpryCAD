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
    static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#FFE8EDF3"));
    static readonly IBrush BarLineBrush = new SolidColorBrush(Color.Parse("#FFD5DCE6"));
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
        Background = new SolidColorBrush(Color.Parse("#FFF9FAFC"));

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
        base.Content = root;
        _title.Text = Title;
    }

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
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Esc closes a tool window whose content did not use the key (a dialog's Cancel button handles it itself)
        if (!e.Handled && e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
