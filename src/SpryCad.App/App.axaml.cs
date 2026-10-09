using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SpryCad.App;

public partial class App : Application
{
    public static void ShowError(string msg) => MainWindow.ShowErrorStatic(msg);

    /// <summary>
    /// Appends an exception (with its stack) to %APPDATA%\SpryCAD\error.log, after what was being done when given.
    /// The first entry of a run is preceded by the version, OS and GL driver, so a log sent with a bug report says
    /// what it came from; past 1 MB the log moves to error.log.1 (replacing the one before) and starts again.
    /// </summary>
    public static void Log(Exception? ex, string? context = null)
    {
        if (ex == null) return;
        Write((context != null ? context + ": " : "") + ex);
    }

    /// <summary>The GL driver (vendor, renderer, version), set when the first viewport starts; logged with the run's header.</summary>
    public static void SetGlInfo(string info)
    {
        lock (_logLock)
        {
            if (_glInfo != null) return;
            _glInfo = info;
            if (_headerWritten) Append($"[{DateTime.Now:u}] GL: {info}");
        }
    }

    static readonly object _logLock = new();
    static bool _headerWritten;
    static string? _glInfo;
    const long MaxLogBytes = 1 << 20;

    static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpryCAD", "error.log");

    static void Write(string entry)
    {
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogBytes) File.Move(LogPath, LogPath + ".1", overwrite: true);
                if (!_headerWritten)
                {
                    _headerWritten = true;
                    Append($"===== [{DateTime.Now:u}] SpryCAD {Version()} | {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                           $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}) | {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}" +
                           $" | GL: {_glInfo ?? "not started"} =====");
                }
                Append($"[{DateTime.Now:u}] {entry}{Environment.NewLine}");
            }
            catch { }
        }
    }

    static void Append(string line)
    {
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    static string Version() => System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

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
        SpryCad.Core.Regen.Warmup.Start();
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
            _ = Updater.CheckAtStart(w);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens files: several at once each in a tab, read when shown (MainWindow.OpenFilesAsync).</summary>
    static Task OpenFiles(MainWindow w, IEnumerable<string> files) => w.OpenFilesAsync(files.ToList());
}
