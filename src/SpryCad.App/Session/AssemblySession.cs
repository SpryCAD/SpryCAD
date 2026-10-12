using SpryCad.Core.Assembly;

namespace SpryCad.App.Session;

/// <summary>
/// An open assembly in a document tab: the model (<see cref="AssemblyModel"/>), unsaved state, undo / redo (the
/// assembly's JSON before each change; embedded files are only ever added while it is open, so they are kept) and
/// the selected component.
/// </summary>
public sealed class AssemblySession
{
    public AssemblyModel Model { get; }
    public string? FilePath => Model.Path;
    public bool Dirty { get; private set; }
    /// <summary>The name of an assembly not saved yet ("Assembly1", …).</summary>
    public string UntitledName { get; set; } = "Assembly1";
    public string Title => (FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : UntitledName) + (Dirty ? " *" : "");
    /// <summary>The selected component's id (null: none).</summary>
    public string? Selected { get; set; }
    /// <summary>The mate selected in the tree (its id): its faces shown highlighted. Never both this and a component.</summary>
    public string? SelectedMate { get; set; }
    /// <summary>The assembly changed (components, placements, references, saved state).</summary>
    public event Action? Changed;
    /// <summary>Counts changes to what it looks like (a preview made of an older count is stale).</summary>
    public int Revision { get; private set; }

    /// <summary>The states to undo / redo to: the document's JSON and its embedded files (replaced, never changed in place: a copy of the map is enough).</summary>
    readonly Stack<(string json, Dictionary<string, byte[]> files)> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public AssemblySession(AssemblyModel model, bool dirty = false) { Model = model; Dirty = dirty; }

    /// <summary>
    /// Opens an assembly file: its parts found (by identity) and read; each shown from its cache when that is current,
    /// the others built afterwards (<see cref="BuildPending"/>). Call off the UI thread.
    /// </summary>
    public static AssemblySession Open(string path)
    {
        var doc = AssemblyDocument.Load(path);
        var model = PartsIndexStore.Use(ix => AssemblyModel.Open(doc, path, ix, defer: true));
        // the mates as the parts are now (a part changed since: its components move to keep them)
        if (model.HasMates) model.SolveMates();
        // references updated (parts found in new places): unsaved until saved
        return new AssemblySession(model, model.Resolution.Changed);
    }

    /// <summary>Displays are being built in the background (<see cref="BuildPending"/>).</summary>
    public bool Building { get; private set; }

    /// <summary>
    /// Builds the displays waiting to be built, several parts at once off the UI thread (<see cref="PartWork"/>), each
    /// shown as it is done; then the mates solved again (a part that changed moves its components).
    /// <paramref name="progress"/> is told how far it has got ("Building parts, 3 of 7 done…") and null when done. Call
    /// on the UI thread.
    /// </summary>
    public async Task BuildPending(Action<string?>? progress = null)
    {
        if (Building) return;
        Building = true;
        try
        {
            bool any = false;
            for (var jobs = Model.PendingJobs(); jobs.Count > 0; jobs = Model.PendingJobs())
            {
                var built = new System.Collections.Concurrent.ConcurrentQueue<(AssemblyModel.PendingJob job, PartDisplay? display, string? problem)>();
                var run = Task.Run(() => PartWork.Each(jobs, j => j.Placeholder.Part!, job =>
                {
                    try { built.Enqueue((job, job.Build(), null)); }
                    catch (Exception ex) { App.Log(ex, "Build component part " + job.Name); built.Enqueue((job, null, ex.Message)); }
                }));
                // each shown as it comes (here, on the UI thread)
                int done = 0;
                progress?.Invoke($"Building parts, 0 of {jobs.Count} done…");
                while (true)
                {
                    var finished = run.IsCompleted;
                    while (built.TryDequeue(out var b))
                    {
                        done++;
                        progress?.Invoke($"Building parts, {done} of {jobs.Count} done…");
                        if (Model.Install(b.job, b.display, b.problem)) { any = true; Shown(); }
                    }
                    if (finished) break;
                    await Task.WhenAny(run, Task.Delay(50));
                }
                await run;
            }
            if (any && Model.HasMates) { Model.SolveMates(); Shown(); }
        }
        finally { Building = false; progress?.Invoke(null); }
    }

    /// <summary>Watches its parts' and sub-assemblies' files (set by the window while the assembly is open in a tab).</summary>
    public SourceWatcher? Watcher { get; set; }

    /// <summary>
    /// Parts or sub-assemblies whose files were written: those saved with changes shown as they now are (builds left
    /// for <see cref="BuildPending"/>), the mates solved again. Not a change to the assembly itself (nothing to save).
    /// What was shown again (none: nothing changed).
    /// </summary>
    public List<string> Reload(IReadOnlyCollection<string> files)
    {
        var changed = Model.ReloadChanged(files);
        if (changed.Count == 0) return changed;
        if (Model.HasMates) Model.SolveMates();
        Shown();
        return changed;
    }

    /// <summary>What it looks like changed, not the document (nothing to save): shown again.</summary>
    void Shown() { Revision++; Changed?.Invoke(); }

    /// <summary>Before a change: the state to undo to.</summary>
    public void BeginChange() { _undo.Push(State()); _redo.Clear(); }

    (string, Dictionary<string, byte[]>) State()
    {
        // (the active configuration's record up to date, so undoing to it restores it)
        Model.Doc.RecordActiveConfiguration();
        return (Model.Doc.ToJson(), new Dictionary<string, byte[]>(Model.Doc.Files, StringComparer.Ordinal));
    }

    /// <summary>
    /// Forgets undo and redo (after a change undo here cannot reverse: a sub-assembly's file saved).
    /// </summary>
    public void ClearUndo() { _undo.Clear(); _redo.Clear(); }

    /// <summary>After a change: unsaved, and shown.</summary>
    public void EndChange() { Dirty = true; Revision++; Changed?.Invoke(); }

    /// <summary>Drops the state saved by <see cref="BeginChange"/> (the change did not happen).</summary>
    public void AbandonChange() { if (_undo.Count > 0) _undo.Pop(); }

    public void Undo() { if (_undo.Count == 0) return; _redo.Push(State()); Restore(_undo.Pop()); }
    public void Redo() { if (_redo.Count == 0) return; _undo.Push(State()); Restore(_redo.Pop()); }

    void Restore((string json, Dictionary<string, byte[]> files) s)
    {
        var d = AssemblyDocument.FromJson(s.json);
        Model.Doc.Components = d.Components;
        Model.Doc.NextComponentId = d.NextComponentId;
        Model.Doc.Mates = d.Mates;
        Model.Doc.NextMateId = d.NextMateId;
        Model.Doc.MateCounters = d.MateCounters;
        Model.Doc.Configurations = d.Configurations;
        Model.Doc.ActiveConfiguration = d.ActiveConfiguration;
        Model.Doc.Explodes = d.Explodes;
        Model.Doc.Patterns = d.Patterns;
        Model.Doc.NextPatternId = d.NextPatternId;
        Model.Doc.Caches = d.Caches;
        // embedded files as they were (an embedded part edited: its displays made again)
        var changed = s.files.Where(kv => !Model.Doc.Files.TryGetValue(kv.Key, out var now) || !ReferenceEquals(now, kv.Value)).Select(kv => kv.Key).ToList();
        Model.Doc.Files = new Dictionary<string, byte[]>(s.files, StringComparer.Ordinal);
        foreach (var name in changed)
            if (Model.Doc.Components.Any(c => c.Part.Embedded == name)) Model.UpdateEmbedded(name, s.files[name]);
        // parts and sub-assemblies no component uses any more are not shown; those used again are loaded again
        // (and configurations: each configuration of a part has a display of its own)
        Model.Sync();
        if (Selected != null && Model.Find(Selected) == null) Selected = null;
        EndChange();
    }

    public void Save(string path)
    {
        Model.Save(path);
        Dirty = false;
        Changed?.Invoke();
    }
}
