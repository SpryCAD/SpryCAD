using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FeatherCad.Core.Assembly;

namespace FeatherCad.App;

/// <summary>Assemblies, each in its own window (<see cref="AssemblyWindow"/>), opened and made from here.</summary>
public partial class MainWindow
{
    readonly List<AssemblyWindow> _assemblies = new();
    bool _assemblyCloseHooked;

    static readonly FilePickerFileType AssemblyType = new("FeatherCAD Assembly") { Patterns = new[] { "*" + AssemblyDocument.FileExtension } };

    static bool IsAssembly(string path) => Path.GetExtension(path).Equals(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase);

    void NewAssembly() => ShowAssembly(AssemblyWindow.New(this));

    /// <summary>Opens an assembly (its window brought forward if it is open already).</summary>
    async Task OpenAssembly(string path)
    {
        if (_assemblies.FirstOrDefault(a => a.FilePath != null && PartIndex.SamePath(a.FilePath, path)) is { } open) { open.Activate(); return; }
        Status("Opening " + Path.GetFileName(path) + "…");
        if (await AssemblyWindow.Open(this, path) is { } w) { ShowAssembly(w); Status("Opened " + path); }
    }

    void ShowAssembly(AssemblyWindow w)
    {
        _assemblies.Add(w);
        w.Closed += (_, _) => _assemblies.Remove(w);
        if (!_assemblyCloseHooked)
        {
            _assemblyCloseHooked = true;
            // the main window closing closes the assemblies first, each asking about its changes
            Closing += (_, e) =>
            {
                if (_assemblies.Count == 0) return;
                e.Cancel = true;
                foreach (var a in _assemblies.ToList()) a.Close();
                if (_assemblies.Count == 0) Close();
            };
        }
        w.Show();
    }
}
