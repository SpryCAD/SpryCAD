using Avalonia.Threading;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;

namespace FeatherCad.App.Session;

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

    readonly List<string> _undo = new();
    readonly List<string> _redo = new();
    readonly Regenerator _regen = new();
    readonly object _lock = new();
    // separate slots: a live-preview request must never replace a pending real rebuild
    CadDocument? _pendingReal, _pendingPreview;
    bool _running;

    public static CadDocument NewDocument() => new();

    public string Title => (FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : "Part1") + (Dirty ? " *" : "");

    public void New()
    {
        Doc = NewDocument(); FilePath = null; Dirty = false;
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Load(string path)
    {
        var d = CadDocument.Load(path);
        Doc = d; FilePath = path; Dirty = false;
        _undo.Clear(); _redo.Clear();
        _regen.Invalidate();
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Save(string path)
    {
        Doc.Save(path);
        FilePath = path; Dirty = false;
        DocumentChanged?.Invoke();
    }

    /// <summary>Call before mutating the document; records an undo snapshot.</summary>
    public void BeginChange()
    {
        _undo.Add(Doc.ToJson());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Call after mutating the document.</summary>
    public void EndChange()
    {
        Dirty = true;
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Change(Action mutate)
    {
        BeginChange();
        mutate();
        EndChange();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Doc.ToJson());
        Doc = CadDocument.FromJson(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Dirty = true;
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Doc.ToJson());
        Doc = CadDocument.FromJson(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Dirty = true;
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    /// <summary>Drops the last undo snapshot (used when a change was cancelled).</summary>
    public void CancelChange(string snapshotJson)
    {
        Doc = CadDocument.FromJson(snapshotJson);
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
        DocumentChanged?.Invoke();
        RequestRebuild();
    }

    public void RequestRebuild() => Enqueue(Doc.Clone(), preview: false);

    /// <summary>Rebuilds a temporary document (feature being edited) for live preview.</summary>
    public void RequestPreview(CadDocument previewDoc) => Enqueue(previewDoc.Clone(), preview: true);

    public void EndPreview()
    {
        PreviewActive = false;
        PreviewResult = null;
        Rebuilt?.Invoke();
    }

    public void ForceRebuild()
    {
        _regen.Invalidate();
        RequestRebuild();
    }

    void Enqueue(CadDocument snapshot, bool preview)
    {
        lock (_lock)
        {
            if (preview) _pendingPreview = snapshot; else _pendingReal = snapshot;
            if (preview) PreviewActive = true;
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
            CadDocument doc; bool preview;
            lock (_lock)
            {
                // the real model first: the tree, status and selection depend on it
                if (_pendingReal != null) { doc = _pendingReal; preview = false; _pendingReal = null; }
                else if (_pendingPreview != null) { doc = _pendingPreview; preview = true; _pendingPreview = null; }
                else { _running = false; break; }
            }
            RegenResult r;
            try
            {
                r = _regen.Rebuild(doc);
                // build display meshes off the UI thread
                foreach (var b in r.State.Bodies) _ = b.Mesh;
                foreach (var s in r.States) foreach (var b in s.Bodies) if (!b.HasMesh && s == r.States[^1]) _ = b.Mesh;
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => App.ShowError("Rebuild failed: " + ex.Message));
                continue;
            }
            bool stale;
            lock (_lock) stale = (preview ? _pendingPreview : _pendingReal) != null;
            if (stale) continue; // a newer request is waiting; skip publishing this one
            Dispatcher.UIThread.Post(() =>
            {
                if (preview) { if (PreviewActive) PreviewResult = r; }
                else Result = r;
                Rebuilt?.Invoke();
            });
        }
        Dispatcher.UIThread.Post(() => BusyChanged?.Invoke(false));
    }

    /// <summary>The result that should be displayed (preview when active).</summary>
    public RegenResult? DisplayResult => PreviewActive && PreviewResult != null ? PreviewResult : Result;

    /// <summary>Equation values for expression evaluation in the UI.</summary>
    public Func<string, double?> Lookup
    {
        get
        {
            var vals = Regenerator.EvaluateEquations(Doc).values;
            return n => vals.TryGetValue(n, out var v) ? v : null;
        }
    }
}
