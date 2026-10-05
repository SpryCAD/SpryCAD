using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FeatherCad.App;

public partial class App : Application
{
    public static void ShowError(string msg) => MainWindow.ShowErrorStatic(msg);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;
            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0])) w.OpenFile(args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
