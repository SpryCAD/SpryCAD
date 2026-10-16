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
    /// <summary>
    /// Parts' displays arrived while they are built in the background (<see cref="BuildPending"/>): only what is drawn
    /// changed (the tree, the components and their mates are as they were); <see cref="Changed"/> follows when all are done.
    /// </summary>
    public event Action? DisplaysChanged;
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

    /// <summary>
    /// Opens an assembly embedded in another from its bytes, in memory: no file of its own (saved back into that
    /// assembly, <see cref="SaveToBytes"/>), named <paramref name="name"/>'s file name. Call off the UI thread.
    /// </summary>
    public static AssemblySession FromEmbedded(byte[] bytes, string name)
    {
        var doc = AssemblyContainer.Load(bytes, name);
        var model = PartsIndexStore.Use(ix => AssemblyModel.Open(doc, null, ix, defer: true));
        if (model.HasMates) model.SolveMates();
        return new AssemblySession(model, model.Resolution.Changed) { UntitledName = Path.GetFileNameWithoutExtension(name) };
    }

    /// <summary>
    /// Its bytes, to embed in the assembly it belongs to (its history going on from <paramref name="previous"/>, the
    /// bytes it had there): saved, as far as this tab goes.
    /// </summary>
    public byte[] SaveToBytes(byte[]? previous)
    {
        var bytes = Model.ToBytes(previous);
        Dirty = false;
        Changed?.Invoke();
        return bytes;
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
                // a part used in several sub-assemblies (one document: a STEP assembly's): built once, shown in each
                // (a document's jobs run one after another, so no two build it at once)
                var made = new System.Collections.Concurrent.ConcurrentDictionary<(object doc, string config, PartSource source, SpryCad.Core.Regen.ThreadDisplay threads), PartDisplay>();
                var run = Task.Run(() => PartWork.Each(jobs, j => j.Placeholder.Part!, job =>
                {
                    try
                    {
                        var p = job.Placeholder;
                        var k = ((object)p.Part!, p.Config, p.Source, job.Threads);
                        if (!made.TryGetValue(k, out var d)) made[k] = d = job.Build();
                        built.Enqueue((job, d, null));
                    }
                    catch (Exception ex) { App.Log(ex, "Build component part " + job.Name); built.Enqueue((job, null, ex.Message)); }
                }));
                // each shown as it comes (here, on the UI thread): what is drawn, a few times a second at most (the
                // tree and the rest once all are done: rebuilt for every part, hundreds of rows each time, they were
                // most of opening a large assembly)
                int done = 0;
                progress?.Invoke($"Building parts, 0 of {jobs.Count} done…");
                long drawnAt = 0; bool undrawn = false;
                while (true)
                {
                    var finished = run.IsCompleted;
                    while (built.TryDequeue(out var b))
                    {
                        done++;
                        progress?.Invoke($"Building parts, {done} of {jobs.Count} done…");
                        if (Model.Install(b.job, b.display, b.problem)) { any = true; undrawn = true; }
                    }
                    if (undrawn && (finished || System.Diagnostics.Stopwatch.GetElapsedTime(drawnAt).TotalMilliseconds >= 300))
                    {
                        Revision++; DisplaysChanged?.Invoke();
                        undrawn = false; drawnAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    if (finished) break;
                    await Task.WhenAny(run, Task.Delay(50));
                }
                await run;
            }
            if (any && Model.HasMates) Model.SolveMates();
            if (any) Shown();
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
