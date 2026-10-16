using Avalonia.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using SpryCad.Core;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;

namespace SpryCad.App.Session;

/// <summary>
/// Owns the open document: undo/redo snapshots, dirty state and asynchronous rebuilds on a
/// background thread (requests are coalesced so the UI never waits on the kernel).
/// </summary>
public sealed class DocumentSession
{
    public CadDocument Doc { get; private set; } = NewDocument();
    public string? FilePath { get; set; }
    public bool Dirty { get; private set; }
    public RegenResult? Result { get; private set; }
    /// <summary>Result of the current preview (property manager open), if any.</summary>
    public RegenResult? PreviewResult { get; private set; }
    public bool PreviewActive { get; private set; }

    public event Action? Rebuilt;
    public event Action? DocumentChanged;
    public event Action<bool>? BusyChanged;
    /// <summary>What the running rebuild is doing (UI thread, a few times a second at most).</summary>
    public event Action<RebuildProgress>? Progress;

    readonly List<string> _undo = new();
    readonly List<string> _redo = new();
    readonly Regenerator _regen = new();
    // exports model the threads: a regenerator of their own, kept (an export after another, or after a small edit,
    // builds little), seeded from the display's (everything before the first thread is built already)
    readonly Regenerator _exportRegen = new() { ThreadOverride = ThreadDisplay.Modeled };

    /// <summary>The document (a snapshot of it) rebuilt with every thread modeled, as exports write it.</summary>
    public RegenResult RebuildForExport(CadDocument snapshot)
    {
        _exportRegen.SeedFrom(_regen);
        return _exportRegen.Rebuild(snapshot);
    }
    readonly object _lock = new();
    // separate slots: a live-preview request must never replace a pending real rebuild
    CadDocument? _pendingReal, _pendingPreview;
    // real rebuild requests are numbered: what a result answers, so a caller can wait for the one it asked for
    int _requested, _pendingRealRev;

    /// <summary>The number of the latest real rebuild request (see <see cref="ResultRevision"/>).</summary>
    public int RequestedRevision { get { lock (_lock) return _requested; } }
    /// <summary>The request the current <see cref="Result"/> was built for: an earlier result can arrive after a
    /// later request was made (e.g. an empty new part's, just before its STEP import's).</summary>
    public int ResultRevision { get; private set; }
    bool _running;
    // the running rebuild: cancelled when a newer request of the same kind (or any real rebuild) supersedes it
    CancellationTokenSource? _runningCts;
    bool _runningPreview;
    // how long each feature took when last built: the time left of a rebuild is estimated from them
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _featureMs = new();

    /// <summary>A new part, in the units chosen last.</summary>
    public static CadDocument NewDocument() => new() { Units = UserSettings.Units };

    /// <summary>The name of a part not saved yet ("Part1", "Part2", ...: one per tab).</summary>
    public string UntitledName { get; set; } = "Part1";

    public string Title => (FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : UntitledName) + (ReadOnly ? " (read-only)" : "") + (Dirty ? " *" : "");

    /// <summary>
    /// A file not read yet: parts opened in a batch get their tabs at once and are read when first shown (or when
    /// the list's previews are made). The tab shows the file's name meanwhile.
    /// </summary>
    public string? PendingPath { get; private set; }
    public bool IsPending => PendingPath != null;
    /// <summary>The file the part comes from: its own, or the STEP model (or part) still to be read.</summary>
    public string? SourcePath => PendingPath ?? FilePath;

    /// <summary>Marks the session as the given file, to be read later (<see cref="TakePending"/>).</summary>
    public void Defer(string path)
    {
        PendingPath = path;
        UntitledName = Path.GetFileNameWithoutExtension(path);
        DocumentChanged?.Invoke();
    }

    /// <summary>The file to read now (the caller loads or imports it); null when nothing is pending.</summary>
    public string? TakePending() { var p = PendingPath; PendingPath = null; return p; }

    /// <summary>
    /// Drops the built model (geometry, display meshes, the rebuild cache) to save memory; the document, its unsaved
    /// changes and undo history stay, and it is built again when shown (<see cref="EnsureBuilt"/>). False (nothing
    /// dropped) while a rebuild is running or waiting.
    /// </summary>
    public bool Unload()
    {
        lock (_lock) if (_running || _pendingReal != null || Result == null) return false;
        Result = null; PreviewResult = null;
        _regen.Invalidate();
        _exportRegen.Invalidate();
        return true;
    }

    /// <summary>Starts a rebuild if the model was dropped (<see cref="Unload"/>) and is not being built.</summary>
    public void EnsureBuilt()
    {
        if (Result == null && !IsPending && !Busy) RequestRebuild();
    }

    // ------------------------------------------------------------------------------------------
    // the local working copy the part is edited in (Core.Storage.WorkingCopy): autosave writes it, saving swaps it in

    WorkingCopy? _wc;

    /// <summary>Someone else's lock on the file when it was opened (null: none): opened read-only unless the user says otherwise.</summary>
    public LockInfo? ForeignLock => _wc?.ForeignLock;

    /// <summary>The part's file was made virtual in an assembly (null: not): opened read-only, with a banner saying so.</summary>
    public SupersededBy? Superseded { get; set; }

    /// <summary>Opened while someone else has it open: Save saves a copy elsewhere (Save As).</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set { if (_readOnly == value) return; _readOnly = value; DocumentChanged?.Invoke(); }
    }
    bool _readOnly;

    /// <summary>Autosave (off the UI thread, on a copy of the document): its working revision, in the local copy.</summary>
    public void SaveWorking(CadDocument copy) => (_wc ??= WorkingCopy.New()).SaveWorking(copy, UserSettings.AuthorName);

    /// <summary>The part's saved revisions, newest first (null: never saved, so no history yet).</summary>
    public List<HistoryEntry>? History() => _wc is { Exists: true } wc && FilePath != null ? wc.Read(PartHistory.Read) : null;

    /// <summary>How the part's file size is spent (as it will be saved).</summary>
    public FileUsage? Usage() => _wc is { Exists: true } wc ? wc.Read(db => PartHistory.Usage(db, wc.Path)) : null;

    /// <summary>Changes the part's history (tags, purging, compacting): unsaved until the part is saved, like any edit.</summary>
    public void EditHistory(Action<PartDatabase> edit)
    {
        if (_wc is not { Exists: true } wc) throw new InvalidOperationException("The part has no revision history. Save it first.");
        wc.Edit(edit);
        HistoryVersion++;
        SetDirty();
        DocumentChanged?.Invoke();
    }

    /// <summary>Counts the changes made to the history (<see cref="EditHistory"/>): what shows it knows when to read it again.</summary>
    public int HistoryVersion { get; private set; }

    /// <summary>
    /// The revision the part is as it is now: the one last saved or opened while nothing has changed since, the one
    /// reverted to (<see cref="Revert"/>) while nothing has changed after; null when the part has unsaved changes.
    /// </summary>
    public Guid? ShownRevision =>
        _revert is { revision: { } r } rv && _undo.Count == rv.depth ? r
        : !Dirty && Guid.TryParse(Doc.Revision, out var head) ? head : null;

    /// <summary>Reads the part's history (as last saved, with the changes to it made since).</summary>
    public T ReadHistory<T>(Func<PartDatabase, T> read) =>
        _wc is { Exists: true } wc ? wc.Read(read) : throw new InvalidOperationException("The part has no revision history. Save it first.");

    /// <summary>
    /// Makes <paramref name="doc"/> the part's state (a revert: <see cref="PartRevert"/>), as one change that can be undone.
    /// The next save records it as a revert revision with <paramref name="message"/> (while the change is not undone).
    /// <paramref name="revision"/>: the revision it is (a revert to one, as it was), shown as the part's (<see cref="ShownRevision"/>).
    /// </summary>
    public void Revert(CadDocument doc, string message, Guid? revision = null)
    {
        BeginChange();
        // the part's identity and what is not the model stay as they are
        doc.Id = Doc.Id; doc.Revision = Doc.Revision; doc.DerivedFrom = Doc.DerivedFrom;
        doc.SketchVisibility = Doc.SketchVisibility; doc.RollbackIndex = null;
        doc.Thumbnail = Doc.Thumbnail;
        // the embedded files it had stay too (undo snapshots do not hold files; saving drops the ones no feature uses)
        foreach (var (k, v) in Doc.Files) doc.Files.TryAdd(k, v);
        Doc = doc;
        _revert = (message, _undo.Count, revision);
        EndChange();
    }

    // the revert the next save records (its message), while the change it made (undo depth) is not undone
    (string message, int depth, Guid? revision)? _revert;

    /// <summary>The part as it was in one of its revisions.</summary>
    public CadDocument LoadRevision(Guid revisionId) => _wc?.LoadRevision(revisionId) ?? throw new InvalidOperationException("The part has no revision history.");

    /// <summary>
    /// Shows an old revision of a part (from <see cref="LoadRevision"/>), read-only: a copy of its own (its own identity),
    /// so saving it (as a new file) never gets mixed up with the part it came from.
    /// </summary>
    public void OpenRevision(CadDocument doc, string name)
    {
        _wc?.Close();
        _wc = WorkingCopy.New();
        doc.ForkIdentity();
        Doc = doc; FilePath = null; UntitledName = name; Dirty = false;
        _readOnly = true;
        _saved = null;
        if (doc.Display != null) { try { _saved = MeshCache.Read(doc.Display); } catch { } doc.Display = null; }
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    /// <summary>Renews the lock on the file while it is open (see <see cref="WorkingCopy.RefreshLock"/>).</summary>
    public void RefreshLock() => _wc?.RefreshLock();

    /// <summary>Done with the part (its tab closed, the app closing): the lock released and the local copy removed.</summary>
    public void Close()
    {
        _wc?.Close();
        _wc = null;
    }

    /// <summary>Opens a part left with unsaved work (the app closed without saving it), as last autosaved: unsaved still.</summary>
    public void Recover(Recoverable r)
    {
        _wc?.Close();
        _wc = WorkingCopy.Adopt(r);
        var d = _wc.Load(working: true);
        Doc = d; FilePath = r.Source; ReadOnly = false;
        _saved = null;
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        SetDirty();
        RequestRebuild();
    }

    void SetDirty()
    {
        Dirty = true;
        // (the first edit: the file is marked as being edited here)
        if (!ReadOnly) _wc?.TakeLock();
    }

    public void New()
    {
        _wc?.Close();
        _wc = WorkingCopy.New();
        ReadOnly = false;
        Doc = NewDocument(); FilePath = null; Dirty = false;
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        _exportRegen.Invalidate();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    /// <summary>A part's file read (<see cref="Read"/>) and not yet the session's (<see cref="Load(Opened)"/>).</summary>
    public sealed record Opened(string Path, WorkingCopy Copy, CadDocument Doc, SupersededBy? Superseded, MeshCache.Entry? Saved);

    /// <summary>Reads a part's file (its local copy, the document, a display saved with it), touching no session state: can run off the UI thread.</summary>
    public static Opened Read(string path)
    {
        var wc = WorkingCopy.Open(path);
        try
        {
            var d = wc.Load();
            // a display saved with it: shown until the rebuild is done
            MeshCache.Entry? saved = null;
            if (d.Display != null) { try { saved = MeshCache.Read(d.Display); } catch { } d.Display = null; }
            return new Opened(path, wc, d, Supersede.Read(path), saved);
        }
        catch { wc.Close(); throw; }
    }

    public void Load(string path) => Load(Read(path));

    public void Load(Opened o)
    {
        _wc?.Close();
        _wc = o.Copy;
        Superseded = o.Superseded;
        _readOnly = Superseded != null;
        Doc = o.Doc; FilePath = o.Path; Dirty = false;
        _saved = o.Saved;
        PartsIndexStore.Record(o.Doc, o.Path);
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        _exportRegen.Invalidate();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    /// <summary>A fresh, unchanged document (e.g. a STEP model just opened as a new part): nothing to undo or save.</summary>
    public void MarkClean()
    {
        _undo.Clear(); _redo.Clear();
        Dirty = false;
        DocumentChanged?.Invoke();
    }

    /// <summary>Marks the document as having unsaved changes (e.g. recovered from an autosave).</summary>
    public void MarkDirty()
    {
        SetDirty();
        DocumentChanged?.Invoke();
    }

    /// <summary>
    /// Saves the part to <paramref name="path"/> (its file, or another: Save As). A <see cref="SaveConflictException"/>
    /// when someone else saved its file since it was opened, unless <paramref name="overwrite"/>.
    /// </summary>
    public void Save(string path, bool overwrite = false)
    {
        // embedded files no feature refers to any more (e.g. an import that was undone) are not saved
        var json = string.Concat(Doc.Features.Select(CadDocument.JsonOf));
        foreach (var name in Doc.Files.Keys.ToList())
            if (!json.Contains($"\"{name}\"")) Doc.Files.Remove(name);
        Doc.Thumbnail = RenderThumbnail() ?? Doc.Thumbnail;
        // a slow part keeps its display (Options), to show at once when opened again
        Doc.Display = DisplayToKeep();
        // the new revision's preview, its changes shown (the Revisions pane), from the last rebuild when it is of this state
        Doc.PreviewMesh = Result is { } built && ResultRevision == RequestedRevision && built.State.Bodies.Count > 0
            ? AssemblyModel.Merge(built.State.Bodies.Select(b => b.Mesh)) : null;
        // saved as another file: that file is a copy, with its own identity (the original keeps its own; a failed
        // save keeps the document as it was)
        var (id, rev, from) = (Doc.Id, Doc.Revision, Doc.DerivedFrom);
        bool same = FilePath != null && PartIndex.SamePath(FilePath, path);
        if (FilePath != null && !same) Doc.ForkIdentity();
        foreach (var name in Doc.Files.Keys) DocumentContainer.CheckName(name);
        _wc ??= WorkingCopy.New();
        var revert = _revert is { } rv && _undo.Count >= rv.depth ? rv.message : null;
        try { _wc.Save(Doc, UserSettings.AuthorName, same ? null : path, overwrite, revert ?? "", revert != null ? RevisionKind.Revert : RevisionKind.Save); }
        catch { (Doc.Id, Doc.Revision, Doc.DerivedFrom) = (id, rev, from); throw; }
        finally { Doc.Display = null; Doc.PreviewMesh = null; }
        PartsIndexStore.Record(Doc, path);
        FilePath = path; Dirty = false; ReadOnly = false;
        _revert = null;
        if (!same) _wc.TakeLock();
        DocumentChanged?.Invoke();
    }

    /// <summary>
    /// The display to save with the part (<see cref="UserSettings.KeepDisplay"/>): the last rebuild's, when it is of the
    /// document as it is and the part is slow enough to build; else null.
    /// </summary>
    byte[]? DisplayToKeep()
    {
        if (UserSettings.KeepDisplay == KeepDisplay.Never || Result is not { } r || ResultRevision != RequestedRevision || r.State.Bodies.Count == 0) return null;
        if (UserSettings.KeepDisplay == KeepDisplay.SlowParts && MeshCache.BuildSeconds(r) < UserSettings.KeepDisplaySeconds) return null;
        try { return MeshCache.Of(r, Doc.EffectiveAppearance); }
        catch { return null; } // (never worth failing a save)
    }

    /// <summary>The display saved with the part, while it is being rebuilt after opening (null once it is, or none was saved).</summary>
    public MeshCache.Entry? SavedDisplay => Result == null ? _saved : null;
    MeshCache.Entry? _saved;

    /// <summary>Preview of the last rebuilt part (null before the first rebuild or when it fails).</summary>
    public byte[]? RenderThumbnail()
    {
        try { return DocumentContainer.RenderThumbnail(Doc, Result); }
        catch { return null; } // a preview is never worth failing a save
    }

    /// <summary>Replaces the document with a snapshot; embedded files are kept (snapshots hold only the JSON).</summary>
    void Restore(string json)
    {
        var files = Doc.Files; var thumb = Doc.Thumbnail;
        Doc = CadDocument.FromJson(json);
        Doc.Files = files; Doc.Thumbnail = thumb;
    }

    /// <summary>Call before mutating the document; records an undo snapshot.</summary>
    public void BeginChange()
    {
        _undo.Add(Doc.ToJson(indented: false));
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Call after mutating the document.</summary>
    public void EndChange()
    {
        // the library rows the features now use copied in, those none uses dropped (part of this undo step)
        Core.Library.LibraryLink.Sync(Doc);
        // and the shop profile rows its sheet metal can use (copied when a material or gauge is new to the part)
        SheetMetal.Features.SheetProfiles.Sync(Doc);
        SetDirty();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Change(Action mutate)
    {
        BeginChange();
        mutate();
        EndChange();
    }

    /// <summary>A rebuild or preview is running or queued.</summary>
    public bool Busy { get { lock (_lock) return _running; } }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Doc.ToJson(indented: false));
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        SetDirty();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Doc.ToJson(indented: false));
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        SetDirty();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    /// <summary>Drops the last undo snapshot (used when a change was cancelled).</summary>
    public void CancelChange(string snapshotJson)
    {
        Restore(snapshotJson);
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void RequestRebuild() { var at = Stopwatch.GetTimestamp(); Enqueue(Doc.Clone(), preview: false, at); }

    /// <summary>The time from the change to the result shown, of the result shown last (null before any).</summary>
    public ShowTiming? Timing { get; private set; }

    /// <summary>
    /// How long building <paramref name="doc"/>'s features from index <paramref name="from"/> on took last time (seconds),
    /// and the slowest of them: a warning before a slow preview.
    /// </summary>
    public (double seconds, string? slowest, double slowestSeconds) Estimate(CadDocument doc, int from)
    {
        double total = 0, worst = 0; string? name = null;
        for (int i = Math.Max(0, from); i < Math.Min(doc.ActiveCount, doc.Features.Count); i++)
        {
            var f = doc.Features[i];
            if (f.Suppressed || !_featureMs.TryGetValue(f.Id, out var ms)) continue;
            total += ms;
            if (ms > worst) { worst = ms; name = f.Name; }
        }
        return (total / 1000, name, worst / 1000);
    }

    /// <summary>Rebuilds a temporary document (feature being edited) for live preview.</summary>
    public void RequestPreview(CadDocument previewDoc) { var at = Stopwatch.GetTimestamp(); Enqueue(previewDoc.Clone(), preview: true, at); }

    public void EndPreview()
    {
        PreviewActive = false;
        PreviewResult = null;
        Rebuilt?.Invoke();
    }

    /// <summary>Threads as each is set, or all modeled / all cosmetic: a view setting (not saved with the part).</summary>
    public ThreadDisplay Threads
    {
        get => _regen.ThreadOverride;
        set { if (_regen.ThreadOverride == value) return; _regen.ThreadOverride = value; RequestRebuild(); }
    }

    public void ForceRebuild()
    {
        _regen.Invalidate();
        _exportRegen.Invalidate();
        RequestRebuild();
    }

    // when the waiting requests were made (before their copy) and queued (after it): Stopwatch timestamps
    (long requested, long queued) _pendingRealAt, _pendingPreviewAt;

    void Enqueue(CadDocument snapshot, bool preview, long requestedAt)
    {
        var queued = (requestedAt, Stopwatch.GetTimestamp());
        lock (_lock)
        {
            if (preview) { _pendingPreview = snapshot; _pendingPreviewAt = queued; }
            else { _pendingReal = snapshot; _pendingRealRev = ++_requested; _pendingRealAt = queued; }
            if (preview) PreviewActive = true;
            // a newer preview makes the running preview pointless; a real rebuild goes before any preview, and a
            // newer real rebuild replaces the running one (its result would not be shown)
            if (_running && (_runningPreview || !preview)) _runningCts?.Cancel();
            if (_running) return;
            _running = true;
        }
        BusyChanged?.Invoke(true);
        var t = new Thread(Worker, 256 * 1024 * 1024) { IsBackground = true, Name = "Rebuild" };
        t.Start();
    }

    void Worker()
    {
        while (true)
        {
            CadDocument doc; bool preview; int rev = 0; (long requested, long queued) at;
            lock (_lock)
            {
                // the real model first: the tree, status and selection depend on it
                if (_pendingReal != null) { doc = _pendingReal; preview = false; rev = _pendingRealRev; _pendingReal = null; at = _pendingRealAt; }
                else if (_pendingPreview != null) { doc = _pendingPreview; preview = true; _pendingPreview = null; at = _pendingPreviewAt; }
                else { _running = false; break; }
            }
            var timing = new ShowTiming { Preview = preview, Requested = at.requested, Queued = at.queued, Started = Stopwatch.GetTimestamp() };
            RegenResult r;
            var cts = new CancellationTokenSource();
            lock (_lock) { _runningCts = cts; _runningPreview = preview; }
            var started = System.Diagnostics.Stopwatch.StartNew();
            // estimate: the features still to build, at the time they took last time
            double Remaining(int from) => doc.Features.Skip(from).Take(Math.Max(0, doc.ActiveCount - from))
                .Sum(f => _featureMs.TryGetValue(f.Id, out var ms) ? ms : 0);
            void Report(RegenProgress p) =>
                PostProgress(new RebuildProgress(preview, p.Index, p.Count, p.FeatureName, p.Detail, started.Elapsed.TotalSeconds, Remaining(p.Index) / 1000));
            // a newer request of the same kind waiting: this result would not be shown
            bool Stale() { lock (_lock) return (preview ? _pendingPreview : _pendingReal) != null; }
            try
            {
                r = _regen.Rebuild(doc, null, cts.Token, Report);
                timing.Rebuilt = Stopwatch.GetTimestamp();
                foreach (var (id, st) in r.Status) if (st.Milliseconds > 0) _featureMs[id] = st.Milliseconds;
                // build display meshes off the UI thread (not for a result already superseded); a big model's take a while too
                PostProgress(new RebuildProgress(preview, doc.ActiveCount, doc.ActiveCount, "", null, started.Elapsed.TotalSeconds, 0) { Meshing = true });
                foreach (var b in r.State.Bodies) { if (Stale()) break; _ = b.Mesh; }
                foreach (var s in r.States) foreach (var b in s.Bodies) if (!b.HasMesh && s == r.States[^1] && !Stale()) _ = b.Mesh;
                timing.Meshed = Stopwatch.GetTimestamp();
            }
            catch (OperationCanceledException) { continue; } // superseded by a newer request
            catch (Exception ex)
            {
                App.Log(ex, "Rebuild of " + (SourcePath ?? UntitledName));
                Dispatcher.UIThread.Post(() => App.ShowError("Rebuild failed: " + ex.Message));
                continue;
            }
            finally { lock (_lock) _runningCts = null; }
            if (Stale()) continue; // a newer request is waiting; skip publishing this one
            Dispatcher.UIThread.Post(() =>
            {
                timing.Applied = Stopwatch.GetTimestamp();
                if (preview) { if (PreviewActive) PreviewResult = r; }
                else { Result = r; ResultRevision = rev; _saved = null; }
                Timing = timing;
                Rebuilt?.Invoke();
                timing.Updated = Stopwatch.GetTimestamp();
            });
        }
        Dispatcher.UIThread.Post(() => BusyChanged?.Invoke(false));
    }

    // the latest progress, and whether a post to the UI thread is waiting to show it: the UI is not flooded, and the
    // step reported last is the one shown (one that takes long, such as reading a STEP file, is never dropped)
    RebuildProgress? _latestProgress;
    int _progressPosted;

    void PostProgress(RebuildProgress p)
    {
        Volatile.Write(ref _latestProgress, p);
        if (Interlocked.Exchange(ref _progressPosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _progressPosted, 0);
            if (Volatile.Read(ref _latestProgress) is { } latest) Progress?.Invoke(latest);
        }, DispatcherPriority.Background);
    }

    /// <summary>The result that should be displayed (preview when active).</summary>
    public RegenResult? DisplayResult => PreviewActive && PreviewResult != null ? PreviewResult : Result;

    /// <summary>Equation values (with their units) for expression evaluation in the UI.</summary>
    public Func<string, Quantity?> Lookup
    {
        get
        {
            var vals = Regenerator.EvaluateEquations(Doc).values;
            return n => vals.TryGetValue(n, out var v) ? v : null;
        }
    }
}

/// <summary>A running rebuild's progress, for the status bar.</summary>
public sealed record RebuildProgress(bool Preview, int Index, int Count, string Feature, string? Detail, double ElapsedSeconds, double EstimatedLeftSeconds)
{
    /// <summary>The features are built: their display meshes are being made.</summary>
    public bool Meshing { get; init; }

    public string Text => $"{(Preview ? "Preview" : "Rebuilding")}: {Step}";

    /// <summary>The step alone: the feature (n of m) and what it is doing, or the display meshes; the time taken and left.</summary>
    public string Step
    {
        get
        {
            var t = Meshing ? "display meshes" : $"{Feature} ({Index + 1} of {Count})";
            if (!Meshing && Detail != null) t += $": {Detail}";
            t += $"  {ElapsedSeconds:0.0} s";
            if (!Meshing && EstimatedLeftSeconds >= 1) t += $", about {EstimatedLeftSeconds:0} s left";
            return t;
        }
    }

    /// <summary>How far through the features (0 to 1).</summary>
    public double Fraction => Meshing || Count <= 0 ? 1 : (double)Index / Count;
}

/// <summary>
/// Where the time went from a change to its result on screen (Stopwatch timestamps): the part copied for the rebuild
/// thread (Requested to Queued), waiting for it (to Started), the rebuild (to Rebuilt), the display meshes (to Meshed),
/// the hand-over to the UI thread (to Applied), the tree and scene updated (to Updated), and the first frame drawn
/// with it, the meshes sent to the graphics card (to Shown, set by the window once that frame is drawn).
/// </summary>
public sealed class ShowTiming
{
    public bool Preview { get; init; }
    public long Requested, Queued, Started, Rebuilt, Meshed, Applied, Updated, Shown;

    static double Ms(long a, long b) => a != 0 && b > a ? Stopwatch.GetElapsedTime(a, b).TotalMilliseconds : 0;

    public double TotalMs => Ms(Requested, Shown);

    public string Breakdown =>
        $"Displayed {TotalMs:0} ms after the change. Copy {Ms(Requested, Queued):0} ms, queue {Ms(Queued, Started):0}, " +
        $"rebuild {Ms(Started, Rebuilt):0}, display meshes {Ms(Rebuilt, Meshed):0}, transfer {Ms(Meshed, Applied):0}, " +
        $"tree and scene {Ms(Applied, Updated):0}, first frame {Ms(Updated, Shown):0}";
}
