using System.Security.Cryptography;
using System.Text.Json;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;

namespace SpryCad.Core.Storage;

/// <summary>Someone else's lock on a part file: who, on which computer, since when.</summary>
public sealed record LockInfo(string User, string Machine, DateTime SinceUtc);

/// <summary>The part's file was saved by someone else since it was opened: saving over it would lose their work.</summary>
public sealed class SaveConflictException(string path) : IOException($"{System.IO.Path.GetFileName(path)} was changed by someone else since it was opened.")
{
    public string FilePath { get; } = path;
}

/// <summary>A part left with unsaved work (the app closed without saving it): what it was, where it came from.</summary>
public sealed record Recoverable(string WorkingPath, string? Source, string Name, DateTime SavedUtc);

/// <summary>
/// The local copy a part is edited in (the file format spec, section 12). SQLite's locking cannot be trusted on
/// network shares, so the user's file is never written in place: it is copied into a local folder when opened, all
/// reading and writing goes to the copy, and saving swaps the copy in for the file (written beside it, then
/// renamed over it), unless someone else saved the file since (<see cref="SaveConflictException"/>).
/// <list type="bullet">
/// <item>Autosave writes the copy's working revision (<see cref="SaveWorking"/>): local and quick, never in the history.</item>
/// <item>A lock file beside the user's file (<c>&lt;name&gt;.lock</c>: user, computer, time) says it is being edited, from
/// the first edit (<see cref="TakeLock"/>), refreshed while open; one left by a crash is stale after an hour.</item>
/// <item>A copy in use holds its owner file open; after a crash nothing does, and the copy's working revision is
/// offered back (<see cref="FindRecoverable"/>). A copy closed with nothing unsaved is removed.</item>
/// </list>
/// </summary>
public sealed class WorkingCopy : IDisposable
{
    /// <summary>Where working copies are kept (local application data).</summary>
    public static string Folder { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpryCAD", "Working");

    /// <summary>How long a lock file is taken as held without being refreshed (a crash leaves one behind).</summary>
    public static TimeSpan StaleLock { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often <see cref="RefreshLock"/> renews a lock (well inside <see cref="StaleLock"/>).</summary>
    public static TimeSpan LockRefresh => StaleLock / 6;

    /// <summary>The local copy.</summary>
    public string Path { get; }
    /// <summary>The user's file it is a copy of; null for a part never saved.</summary>
    public string? Source { get; private set; }
    /// <summary>Someone else's lock on <see cref="Source"/> when it was opened (null: none).</summary>
    public LockInfo? ForeignLock { get; }

    string? _fingerprint;
    bool _locked, _closed;
    FileStream? _owner;
    // autosave runs off the UI thread: one operation on the copy at a time, and none after it is closed
    readonly object _gate = new();

    WorkingCopy(string path, string? source, string? fingerprint, LockInfo? foreignLock)
    {
        Path = path; Source = source; _fingerprint = fingerprint; ForeignLock = foreignLock;
        Directory.CreateDirectory(Folder);
        _owner = new FileStream(OwnerOf(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WriteInfo();
    }

    /// <summary>A copy of the part file at <paramref name="source"/> (a part saved before databases is converted).</summary>
    public static WorkingCopy Open(string source)
    {
        source = System.IO.Path.GetFullPath(source);
        var path = NewPath();
        Directory.CreateDirectory(Folder);
        if (PartDatabase.IsFile(source)) File.Copy(source, path);
        else PartFile.Save(CadDocument.Load(source), path);
        return new WorkingCopy(path, source, Fingerprint(source), ReadLock(source));
    }

    /// <summary>A copy for a new part, not saved anywhere yet (made when first written).</summary>
    public static WorkingCopy New() => new(NewPath(), null, null, null);

    /// <summary>Takes over a copy left with unsaved work (<see cref="FindRecoverable"/>).</summary>
    public static WorkingCopy Adopt(Recoverable r)
    {
        var info = ReadInfo(r.WorkingPath);
        return new WorkingCopy(r.WorkingPath, info?.Source, info?.Fingerprint, info?.Source != null ? ReadLock(info.Source) : null);
    }

    /// <summary>The part as last saved, or (<paramref name="working"/>) as last autosaved.</summary>
    public CadDocument Load(bool working = false) { lock (_gate) return PartFile.Load(Path, working); }

    /// <summary>The part as it was in one of its revisions (the history).</summary>
    public CadDocument LoadRevision(Guid revisionId) { lock (_gate) return PartFile.Load(Path, revision: revisionId); }

    /// <summary>Whether the copy has been written (a new part has none until it is autosaved or saved).</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>Reads the copy's database (its history: <see cref="PartHistory"/>), one operation at a time with autosave.</summary>
    public T Read<T>(Func<PartDatabase, T> read)
    {
        lock (_gate) { using var db = PartDatabase.Open(Path); return read(db); }
    }

    /// <summary>Changes the copy's database (tags, maintenance: <see cref="PartHistory"/>); saved to the part's file by the next save.</summary>
    public void Edit(Action<PartDatabase> edit)
    {
        lock (_gate) { if (_closed) return; using var db = PartDatabase.Open(Path, readOnly: false); edit(db); }
    }

    /// <summary>Autosave: the part as the copy's working revision.</summary>
    public void SaveWorking(CadDocument doc, string? author = null)
    {
        lock (_gate) if (!_closed) PartFile.SaveWorking(doc, Path, author);
    }

    /// <summary>
    /// Saves the part (a revision in the copy) to its file, or to <paramref name="target"/> (Save As: the copy is then of
    /// that file). Refused when the file was saved by someone else since it was opened, unless <paramref name="overwrite"/>.
    /// </summary>
    public void Save(CadDocument doc, string? author = null, string? target = null, bool overwrite = false)
    {
        lock (_gate) SaveLocked(doc, author, target, overwrite);
    }

    void SaveLocked(CadDocument doc, string? author, string? target, bool overwrite)
    {
        target = target != null ? System.IO.Path.GetFullPath(target) : Source ?? throw new InvalidOperationException("The part has no file yet: save it as one.");
        bool same = Source != null && PartIndex.SamePath(Source, target);
        if (same && !overwrite && File.Exists(target) && Fingerprint(target) != _fingerprint) throw new SaveConflictException(target);
        PartFile.Save(doc, Path, author);
        var tmp = target + ".saving";
        try
        {
            File.Copy(Path, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        if (!same)
        {
            ReleaseLock();
            Source = target;
        }
        _fingerprint = Fingerprint(target);
        WriteInfo();
    }

    // ------------------------------------------------------------------------------------------
    // the lock file beside the user's file

    static string LockOf(string source) => source + ".lock";

    /// <summary>Marks the file as being edited here (from the first edit): its lock file, unless someone else's holds it.</summary>
    public void TakeLock()
    {
        // (someone else's live lock stays theirs: opened anyway, this copy edits without one)
        if (_locked || Source == null || ReadLock(Source) != null) return;
        try
        {
            var me = new LockInfo(Environment.UserName, Environment.MachineName, DateTime.UtcNow);
            // a lock left by a crash, or gone stale, is hidden: Windows refuses to write over a hidden file
            if (File.Exists(LockOf(Source))) File.SetAttributes(LockOf(Source), FileAttributes.Normal);
            File.WriteAllText(LockOf(Source), JsonSerializer.Serialize(me));
            if (OperatingSystem.IsWindows()) File.SetAttributes(LockOf(Source), File.GetAttributes(LockOf(Source)) | FileAttributes.Hidden);
            _locked = true;
        }
        catch (IOException) { } // (a read-only folder: no lock, the file still opens)
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Renews the lock (every <see cref="LockRefresh"/> while open), so it never looks stale while held.</summary>
    public void RefreshLock()
    {
        if (!_locked || Source == null) return;
        try { File.SetLastWriteTimeUtc(LockOf(Source), DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void ReleaseLock()
    {
        if (!_locked || Source == null) return;
        _locked = false;
        try { if (LockFile(Source) is { } l && IsMine(l)) File.Delete(LockOf(Source)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A lock on the file held by someone else (another user or computer), not stale; null if none.</summary>
    public static LockInfo? ReadLock(string source)
    {
        try
        {
            if (LockFile(source) is not { } l || IsMine(l)) return null;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(LockOf(source)) > StaleLock ? null : l;
        }
        catch (IOException) { return null; }
    }

    static LockInfo? LockFile(string source)
    {
        try { return File.Exists(LockOf(source)) ? JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockOf(source))) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static bool IsMine(LockInfo l) => l.User == Environment.UserName && l.Machine == Environment.MachineName;

    // ------------------------------------------------------------------------------------------
    // closing and recovery

    /// <summary>
    /// Done with the part: the lock released, and the copy removed unless <paramref name="keep"/> (unsaved work, offered
    /// back after a crash or by <see cref="FindRecoverable"/>).
    /// </summary>
    public void Close(bool keep = false)
    {
        lock (_gate) _closed = true;
        ReleaseLock();
        _owner?.Dispose(); _owner = null;
        if (keep) return;
        foreach (var f in new[] { Path, OwnerOf(Path), InfoOf(Path) })
            try { File.Delete(f); } catch { /* best effort */ }
    }

    public void Dispose() => Close(keep: true);

    /// <summary>
    /// Copies left with unsaved work: no app holds them (their owner file is free) and they have a working revision.
    /// Copies with nothing to recover are removed.
    /// </summary>
    public static List<Recoverable> FindRecoverable()
    {
        var found = new List<Recoverable>();
        if (!Directory.Exists(Folder)) return found;
        foreach (var path in Directory.GetFiles(Folder, "*" + CadDocument.FileExtension))
        {
            if (Held(path)) continue;
            var info = ReadInfo(path);
            try
            {
                using var db = PartDatabase.Open(path);
                if (db.Manifest("RootPartId") is { } p && Guid.TryParse(p, out var partId) && db.GetWorking(partId) is { } w)
                {
                    var name = info?.Source != null ? System.IO.Path.GetFileNameWithoutExtension(info.Source) : "Unsaved part";
                    found.Add(new Recoverable(path, info?.Source, name, w.CreatedUtc));
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { }
            // nothing to recover (or not readable): not kept
            foreach (var f in new[] { path, OwnerOf(path), InfoOf(path) })
                try { File.Delete(f); } catch { /* best effort */ }
        }
        // what is left of copies never written (a new part not yet autosaved when the app closed)
        foreach (var owner in Directory.GetFiles(Folder, "*.owner"))
        {
            var path = System.IO.Path.ChangeExtension(owner, CadDocument.FileExtension);
            if (File.Exists(path) || Held(path)) continue;
            foreach (var f in new[] { owner, InfoOf(path) })
                try { File.Delete(f); } catch { /* best effort */ }
        }
        return found.OrderByDescending(r => r.SavedUtc).ToList();
    }

    /// <summary>Removes a copy left with unsaved work, which the user chose not to recover.</summary>
    public static void Discard(Recoverable r)
    {
        foreach (var f in new[] { r.WorkingPath, OwnerOf(r.WorkingPath), InfoOf(r.WorkingPath) })
            try { File.Delete(f); } catch { /* best effort */ }
    }

    static bool Held(string path)
    {
        try { using var _ = new FileStream(OwnerOf(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    // ------------------------------------------------------------------------------------------

    static string NewPath() => System.IO.Path.Combine(Folder, Guid.NewGuid().ToString("N") + CadDocument.FileExtension);
    static string OwnerOf(string path) => System.IO.Path.ChangeExtension(path, ".owner");
    static string InfoOf(string path) => System.IO.Path.ChangeExtension(path, ".json");

    sealed record Info(string? Source, string? Fingerprint);

    void WriteInfo()
    {
        try { File.WriteAllText(InfoOf(Path), JsonSerializer.Serialize(new Info(Source, _fingerprint))); }
        catch (IOException) { } // (recovery then names the part "Unsaved part")
    }

    static Info? ReadInfo(string path)
    {
        try { return File.Exists(InfoOf(path)) ? JsonSerializer.Deserialize<Info>(File.ReadAllText(InfoOf(path))) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>
    /// What tells a file saved again from the one opened, cheaply even over a network: its size, last write time and the
    /// hash of its first 64 KB (a database's header holds a counter SQLite changes on every write).
    /// </summary>
    static string? Fingerprint(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            var head = new byte[Math.Min(64 * 1024, fi.Length)];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
            return $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(head))}";
        }
        catch (IOException) { return null; }
    }
}
