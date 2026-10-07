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
        // compile the modeling code paths in the background so the first rebuild is fast
        FeatherCad.Core.Regen.Warmup.Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;
            if (desktop.Args is { Length: > 0 } args)
                _ = OpenFiles(w, args.Where(File.Exists));
            // files from later launches (SingleInstance): open in tabs here, the window brought to the front
            SingleInstance.Listen(files => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (w.WindowState == Avalonia.Controls.WindowState.Minimized) w.WindowState = Avalonia.Controls.WindowState.Normal;
                w.Activate();
                _ = OpenFiles(w, files);
            }));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens files: several at once each in a tab, read when shown (MainWindow.OpenFilesAsync).</summary>
    static Task OpenFiles(MainWindow w, IEnumerable<string> files) => w.OpenFilesAsync(files.ToList());
}
