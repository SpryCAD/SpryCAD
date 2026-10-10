using Avalonia.Threading;
using SpryCad.Core.Assembly;

namespace SpryCad.App.Session;

/// <summary>
/// Watches the files an open assembly shows (its parts and sub-assemblies, at every depth) and reports those written
/// since, a moment after the last write (a save is several file events). One watcher per folder, told only of the
/// files of interest; nothing is read here (the assembly reads only the files reported, and reloads only those with a
/// new revision). <see cref="CheckStamps"/> catches what the watchers missed (network folders, a full event buffer):
/// the files' times compared, called when the window is activated.
/// </summary>
public sealed class SourceWatcher : IDisposable
{
    static readonly StringComparer Paths = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    readonly Action<IReadOnlyCollection<string>> _changed;
    readonly Dictionary<string, FileSystemWatcher> _dirs = new(Paths);
    /// <summary>The files watched, with their last write times as last seen (touched only on the UI thread).</summary>
    readonly Dictionary<string, DateTime> _stamps = new(Paths);
    /// <summary>The files watched, for the watchers' threads.</summary>
    volatile HashSet<string> _files = new(Paths);
    readonly HashSet<string> _pending = new(Paths);
    readonly DispatcherTimer _settle;
    bool _disposed;

    /// <param name="changed">Called on the UI thread with the files written since they were last reported.</param>
    public SourceWatcher(Action<IReadOnlyCollection<string>> changed)
    {
        _changed = changed;
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Fire());
    }

    /// <summary>Watches these files (and stops watching others): folders watched as needed. Call on the UI thread.</summary>
    public void Watch(IEnumerable<string> files)
    {
        if (_disposed) return;
        var want = new HashSet<string>(files.Select(Path.GetFullPath), Paths);
        if (want.SetEquals(_stamps.Keys)) return;
        foreach (var f in _stamps.Keys.Where(f => !want.Contains(f)).ToList()) _stamps.Remove(f);
        foreach (var f in want.Where(f => !_stamps.ContainsKey(f))) _stamps[f] = Stamp(f);
        _files = new HashSet<string>(want, Paths);
        var dirs = new HashSet<string>(want.Select(f => Path.GetDirectoryName(f)!), Paths);
        foreach (var d in _dirs.Keys.Where(d => !dirs.Contains(d)).ToList())
        {
            _dirs[d].Dispose();
            _dirs.Remove(d);
        }
        foreach (var d in dirs.Where(d => !_dirs.ContainsKey(d) && Directory.Exists(d)))
        {
            try
            {
                var w = new FileSystemWatcher(d) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, IncludeSubdirectories = false };
                w.Changed += (_, e) => Seen(e.FullPath);
                w.Created += (_, e) => Seen(e.FullPath);
                // (a save writes beside the file and renames it into place)
                w.Renamed += (_, e) => Seen(e.FullPath);
                // events lost: everything checked by its time
                w.Error += (_, _) => Dispatcher.UIThread.Post(CheckStamps);
                w.EnableRaisingEvents = true;
                _dirs[d] = w;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { } // (checked by time instead)
        }
    }

    /// <summary>A file event (a watcher's thread): one of ours is reported once its writes settle.</summary>
    void Seen(string path)
    {
        if (!_files.Contains(path)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _pending.Add(path);
            _settle.Stop();
            _settle.Start();
        });
    }

    /// <summary>The files whose write times changed since last seen, reported (cheap: no file is read). Call on the UI thread.</summary>
    public void CheckStamps()
    {
        if (_disposed) return;
        foreach (var (f, t) in _stamps)
            if (Stamp(f) != t) _pending.Add(f);
        if (_pending.Count > 0) Fire();
    }

    void Fire()
    {
        _settle.Stop();
        if (_disposed || _pending.Count == 0) return;
        var files = _pending.ToList();
        _pending.Clear();
        foreach (var f in files) if (_stamps.ContainsKey(f)) _stamps[f] = Stamp(f);
        _changed(files);
    }

    static DateTime Stamp(string f)
    {
        try { return File.GetLastWriteTimeUtc(f); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    public void Dispose()
    {
        _disposed = true;
        _settle.Stop();
        foreach (var w in _dirs.Values) w.Dispose();
        _dirs.Clear();
    }
}
