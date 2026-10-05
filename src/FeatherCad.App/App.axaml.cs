using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FeatherCad.App;

public partial class App : Application
{
    public static void ShowError(string msg) => MainWindow.ShowErrorStatic(msg);

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // never lose work to an unexpected UI exception: report it and keep running
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log(e.Exception);
            ShowError(e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;
            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0])) w.OpenFile(args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
