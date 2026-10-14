using Avalonia;

namespace SpryCad.App;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MoveSettings();
        // (before the app is built: which graphics API it draws through is chosen there)
        Session.UserSettings.Load();
        // files opened while SpryCAD already runs open there, in tabs
        var files = args.Where(File.Exists).ToArray();
        if (files.Length > 0 && SingleInstance.TrySend(files)) return;

        // The kernel uses iterative algorithms but booleans on big models can recurse in places;
        // run the UI on a thread with a generous stack.
        var t = new Thread(() => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args), 64 * 1024 * 1024);
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        // an update the user accepted: its installer runs now that the app has closed
        Updater.RunPending();
    }

    /// <summary>
    /// Settings, recent files, the index of parts and autosave from before the product was renamed (FeatherCAD):
    /// copied into the SpryCAD folder the first time it runs.
    /// </summary>
    static void MoveSettings()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var old = Path.Combine(appData, "FeatherCAD");
            var now = Path.Combine(appData, "SpryCAD");
            if (Directory.Exists(now) || !Directory.Exists(old)) return;
            Directory.CreateDirectory(now);
            foreach (var f in Directory.GetFiles(old))
            {
                var name = Path.GetFileName(f);
                // autosaved parts kept their old extension
                if (name.EndsWith(".fcad", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fcprt", StringComparison.OrdinalIgnoreCase))
                    name = Path.ChangeExtension(name, ".sprt");
                File.Copy(f, Path.Combine(now, name));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Direct3D (ANGLE) or the card's own OpenGL first (Options > Graphics), the other after it so the app always starts
            .With(new Win32PlatformOptions
            {
                RenderingMode = Session.UserSettings.PreferOpenGL
                    ? new[] { Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
                    : new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software },
            })
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software } })
            .WithInterFont()
            .LogToTrace();
}
