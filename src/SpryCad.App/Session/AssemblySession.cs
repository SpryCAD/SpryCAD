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
    /// <summary>The assembly changed (components, placements, references, saved state).</summary>
    public event Action? Changed;
    /// <summary>Counts changes to what it looks like (a preview made of an older count is stale).</summary>
    public int Revision { get; private set; }

    readonly Stack<string> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public AssemblySession(AssemblyModel model, bool dirty = false) { Model = model; Dirty = dirty; }

    /// <summary>Opens an assembly file: its parts found (by identity) and built. Slow parts: call off the UI thread.</summary>
    public static AssemblySession Open(string path)
    {
        var doc = AssemblyDocument.Load(path);
        var model = PartsIndexStore.Use(ix => AssemblyModel.Open(doc, path, ix));
        // the mates as the parts are now (a part changed since: its components move to keep them)
        if (doc.Mates.Count > 0) model.SolveMates();
        // references updated (parts found in new places): unsaved until saved
        return new AssemblySession(model, model.Resolution.Changed);
    }

    /// <summary>Before a change: the state to undo to.</summary>
    public void BeginChange() { _undo.Push(Model.Doc.ToJson()); _redo.Clear(); }

    /// <summary>After a change: unsaved, and shown.</summary>
    public void EndChange() { Dirty = true; Revision++; Changed?.Invoke(); }

    /// <summary>Drops the state saved by <see cref="BeginChange"/> (the change did not happen).</summary>
    public void AbandonChange() { if (_undo.Count > 0) _undo.Pop(); }

    public void Undo() { if (_undo.Count == 0) return; _redo.Push(Model.Doc.ToJson()); Restore(_undo.Pop()); }
    public void Redo() { if (_redo.Count == 0) return; _undo.Push(Model.Doc.ToJson()); Restore(_redo.Pop()); }

    void Restore(string json)
    {
        var d = AssemblyDocument.FromJson(json);
        Model.Doc.Components = d.Components;
        Model.Doc.NextComponentId = d.NextComponentId;
        Model.Doc.Mates = d.Mates;
        Model.Doc.NextMateId = d.NextMateId;
        Model.Doc.MateCounters = d.MateCounters;
        // parts no component uses any more are not shown; parts used again are built again
        foreach (var key in Model.Parts.Keys.Where(k => !Model.ComponentsOf(k).Any()).ToList()) Model.Parts.Remove(key);
        foreach (var key in Model.Doc.Components.Select(c => ComponentResolver.Key(c.Part)).Distinct().Where(k => !Model.Parts.ContainsKey(k)).ToList())
            Model.LoadPart(key);
        if (Selected != null && Model.Doc.Find(Selected) == null) Selected = null;
        EndChange();
    }

    public void Save(string path)
    {
        Model.Save(path);
        Dirty = false;
        Changed?.Invoke();
    }
}
