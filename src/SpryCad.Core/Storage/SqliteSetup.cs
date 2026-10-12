using System.Runtime.InteropServices;

namespace SpryCad.Core.Storage;

/// <summary>
/// Which SQLite the files are read and written with, chosen once before the first database is opened. On Linux,
/// the system's (libsqlite3.so.0, on every distribution: the packages depend on it, and it gets the system's
/// security updates; a bundled build would need a newer glibc than the packages promise). Elsewhere, the build
/// shipped with the app (e_sqlite3).
/// </summary>
static class SqliteSetup
{
    static readonly Lazy<bool> Done = new(Init);

    public static void Ensure() => _ = Done.Value;

    static bool Init()
    {
        if (OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libsqlite3.so.0", out var system))
        {
            // the provider imports "sqlite3": the system library by its versioned name (the bare libsqlite3.so is
            // only there with the development package)
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                (name, _, _) => name == "sqlite3" ? system : IntPtr.Zero);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        }
        else SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        return true;
    }
}
