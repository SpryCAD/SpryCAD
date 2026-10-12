using System.Text.Json;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// An assembly: components (placed instances of parts) and, later, the mates between them. Saved as a *.sasm
/// container (<see cref="AssemblyContainer"/>): the assembly's JSON, the embedded parts, and the display caches of
/// the parts it uses (so it opens, shows and shares without them).
/// <para>
/// Parts are linked by identity, not by path: a component remembers its part's GUID, where the file was (relative
/// to the assembly and absolute) and which revision it last saw; <see cref="ComponentResolver"/> finds the file
/// again wherever it went.
/// </para>
/// </summary>
public sealed partial class AssemblyDocument
{
    public const string FileExtension = ".sasm";
    /// <summary>2: its parts' random ids in mate references; 1: their old counted ids, renewed on opening (<see cref="RenewLegacyIds"/>).</summary>
    public const int CurrentFormatVersion = 2;

    public string Format { get; set; } = "SpryCAD-Assembly";
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    /// <summary>The assembly's identity (a GUID, given on its first save), as for parts.</summary>
    public string? Id { get; set; }
    /// <summary>The head revision's id (<see cref="Storage.AssemblyFile"/>): a new one when a save changed the assembly.</summary>
    public string? Revision { get; set; }
    public string? DerivedFrom { get; set; }
    public UnitSystem Units { get; set; } = UnitSystem.MMGS;
    public List<Component> Components { get; set; } = new();
    public int NextComponentId { get; set; } = 1;
    /// <summary>The mates, in the order they were added (solved together).</summary>
    public List<Mate> Mates { get; set; } = new();
    public int NextMateId { get; set; } = 1;
    /// <summary>Per mate type, the last number used in a name ("Coincident3").</summary>
    public Dictionary<string, int> MateCounters { get; set; } = new();
    /// <summary>
    /// The display caches by part revision ("&lt;part id&gt;/&lt;revision&gt;"): the attachment holding its mesh
    /// (<see cref="MeshCache"/>). Kept for the revisions the components last saw.
    /// </summary>
    public Dictionary<string, string> Caches { get; set; } = new();
    /// <summary>The configurations (null: one, "Default", as the components are).</summary>
    public List<AssemblyConfiguration>? Configurations { get; set; }
    /// <summary>The active configuration's name (null: "Default").</summary>
    public string? ActiveConfiguration { get; set; }
    /// <summary>Exploded views.</summary>
    public List<ExplodeView>? Explodes { get; set; }
    /// <summary>Component patterns (and mirrors).</summary>
    public List<ComponentPattern>? Patterns { get; set; }
    public int NextPatternId { get; set; } = 1;
    /// <summary>Index of the embedded files (name, size, SHA-256), written on save.</summary>
    public List<AttachmentInfo>? Attachments { get; set; }

    /// <summary>Embedded files: embedded parts (parts/…) and display caches (cache/…).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, byte[]> Files { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore] public byte[]? Thumbnail { get; set; }

    /// <summary>
    /// What the next save records the revision as (a part made external or virtual), with its message; cleared by the
    /// save. Null: an ordinary save.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SaveMessage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Storage.RevisionKind SaveKind { get; set; }

    public Component? Find(string id) => Components.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Adds a component of a part: named after the part's file ("Bolt&lt;1&gt;", "Bolt&lt;2&gt;", …), placed as
    /// given. The first component is fixed (the assembly's ground), like SolidWorks.
    /// </summary>
    public Component AddComponent(PartRef part, Transform placement, string? name = null)
    {
        var baseName = name ?? (part.Path != null ? System.IO.Path.GetFileNameWithoutExtension(part.Path) : "Part");
        int n = 1;
        while (Components.Any(c => c.Name == $"{baseName}<{n}>")) n++;
        var c = new Component
        {
            Id = "C" + NextComponentId++,
            Name = $"{baseName}<{n}>",
            Part = part,
            Placement = Placement.From(placement),
            Fixed = Components.Count == 0,
        };
        Components.Add(c);
        return c;
    }

    /// <summary>Adds a mate: its id ("M1", …) and name ("Concentric2") given.</summary>
    public Mate AddMate(Mate m)
    {
        Mate.Normalize(m);
        m.Id = "M" + NextMateId++;
        var key = m.Type.ToString();
        var n = MateCounters.GetValueOrDefault(key) + 1;
        MateCounters[key] = n;
        if (string.IsNullOrEmpty(m.Name)) m.Name = key + n;
        Mates.Add(m);
        return m;
    }

    /// <summary>The mates a component takes part in.</summary>
    public IEnumerable<Mate> MatesOf(string componentId) => Mates.Where(m => m.A.Component == componentId || m.B.Component == componentId);

    /// <summary>The cache key of a part revision, as configured (<see cref="Configurations.KeyOf"/>; "": as it is).</summary>
    public static string CacheKey(string partId, string? revision, string config = "") => partId + "/" + (revision ?? "none") + (config.Length > 0 ? "|" + config : "");

    /// <summary>The document as JSON: indented as files are written, or compact (copies, undo snapshots).</summary>
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this, indented ? Serialization.Options : Serialization.Compact);

    public static AssemblyDocument FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<AssemblyDocument>(json, Serialization.Options) ?? throw new InvalidDataException("Empty assembly");
        if (d.Format == "FeatherCAD-Assembly") d.Format = "SpryCAD-Assembly"; // made before the rename
        if (d.Format != "SpryCAD-Assembly") throw new InvalidDataException("Not a SpryCAD assembly");
        foreach (var m in d.Mates) Mate.Normalize(m);
        if (d.FormatVersion <= Model.LegacyIds.LastVersion)
        {
            // the parts placed directly are known here; those in sub-assemblies once they are open (AssemblyModel.Open)
            d.LegacyIds = true;
            d.FormatVersion = CurrentFormatVersion;
            foreach (var c in d.Components) if (!c.IsAssembly && c.Part.PartId is { } id) c.Part.Revision = Model.LegacyIds.Revision(id, c.Part.Revision);
            d.RenewLegacyIds(path => !path.Contains('/') && d.Find(path) is { IsAssembly: false, Part.PartId: { } id } ? id : null);
        }
        return d;
    }

    /// <summary>Read from a file made before random ids (format version 1): its mates name part faces by their old ids.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LegacyIds { get; private set; }

    /// <summary>
    /// The display caches of its parts, from a file made before random ids: their faces renamed and their keys given
    /// the parts' renewed revisions, as the parts themselves are when opened (<see cref="Model.LegacyIds"/>). A part
    /// not found still shows from its cache, and its mates still find their faces on it.
    /// </summary>
    public void RenewLegacyCaches()
    {
        var parts = Components.Where(c => !c.IsAssembly && c.Part.PartId != null).Select(c => c.Part.PartId!).ToHashSet();
        foreach (var (key, file) in Caches.ToList())
        {
            // "<part id>/<revision>" or "<part id>/<revision>|<configuration>"
            int slash = key.IndexOf('/'), bar = key.IndexOf('|');
            if (slash < 0 || key[..slash] is not { } partId || !parts.Contains(partId)) continue;
            var revision = bar < 0 ? key[(slash + 1)..] : key[(slash + 1)..bar];
            if (revision == "none") continue; // (a part with no revision keeps none)
            Caches.Remove(key);
            Caches[CacheKey(partId, Model.LegacyIds.Revision(partId, revision), bar < 0 ? "" : key[(bar + 1)..])] = file;
            if (Files.TryGetValue(file, out var data))
            {
                try { Files[file] = MeshCache.Renamed(data, n => Model.LegacyIds.Name(partId, n)); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { } // (not a cache it can read: left, rebuilt when used)
            }
        }
    }

    /// <summary>
    /// Renames the part faces mate and pattern references name, as their parts were re-id'd (<see cref="Model.LegacyIds"/>):
    /// each by the identity of its component's part, given by <paramref name="partIdOf"/> (null: not known).
    /// </summary>
    public void RenewLegacyIds(Func<string, string?> partIdOf)
    {
        var refs = Mates.SelectMany(m => new[] { m.A, m.B }).Concat(Patterns?.Select(p => p.Reference) ?? Enumerable.Empty<MateRef>());
        foreach (var r in refs) if (r.Component != null && partIdOf(r.Component) is { } id) r.Face = Model.LegacyIds.Name(id, r.Face);
    }

    public AssemblyDocument Clone()
    {
        var d = FromJson(ToJson(indented: false));
        d.Files = new Dictionary<string, byte[]>(Files, StringComparer.Ordinal);
        d.Thumbnail = Thumbnail;
        return d;
    }

    public void Save(string path) => AssemblyContainer.Save(this, path);
    public static AssemblyDocument Load(string path) => AssemblyContainer.Load(path);
}

/// <summary>A placed instance of a part, or of another assembly (a sub-assembly: <see cref="PartRef.Assembly"/>).</summary>
public sealed class Component
{
    /// <summary>Instance id within the assembly ("C1", "C2", …): mates refer to it.</summary>
    public string Id { get; set; } = "";
    /// <summary>Shown in the tree: the part's name and the instance number ("Bolt&lt;2&gt;").</summary>
    public string Name { get; set; } = "";
    public PartRef Part { get; set; } = new();
    /// <summary>Where the part's origin and axes are in the assembly.</summary>
    public Placement Placement { get; set; } = Placement.From(Transform.Identity);
    /// <summary>Fixed in place (not moved by mates or dragging); otherwise floating.</summary>
    public bool Fixed { get; set; }
    public bool Suppressed { get; set; }
    /// <summary>Not shown (still in the assembly: mated, counted, solved).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }
    /// <summary>Shown from the display cache only (no rebuild of the part): fast to open, nothing to select but the mesh.</summary>
    public bool Lightweight { get; set; }
    /// <summary>Appearance override (RGB 0-1); null: the part's own color.</summary>
    public double[]? Color { get; set; }
    /// <summary>
    /// The part's configuration for this instance: values of its user-facing parameters (a design table's row, a
    /// variable's value) by name, only those that differ from the part's own (<see cref="Configurations"/>). Null: the
    /// part as it is.
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }

    /// <summary>A sub-assembly (its <see cref="Part"/> is an assembly file).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsAssembly => Part.Assembly;
    /// <summary>
    /// A sub-assembly whose own mates are solved with this assembly's, so its components move (a linkage that works
    /// in the assembly using it). Otherwise rigid: it moves as one body, its components where its own file has them.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Flexible { get; set; }
    /// <summary>
    /// A flexible sub-assembly's components where this instance has them, by their path in it ("C2", "C4/C1" in a
    /// sub-assembly of it), each relative to the sub-assembly holding it; those not listed are where the sub-assembly's
    /// own file has them. Null: all of them.
    /// </summary>
    public Dictionary<string, Placement>? Poses { get; set; }
    /// <summary>
    /// A flexible sub-assembly's gear and screw mates' counted turns (<see cref="Mate.AngleA"/>, <see cref="Mate.AngleB"/>)
    /// in this instance, by the mate's path ("M3", "C4/M1"); those not listed as its file has them.
    /// </summary>
    public Dictionary<string, double[]>? Turns { get; set; }
    /// <summary>A sub-assembly's configuration for this instance (null: the one its file has active).</summary>
    public string? Configuration { get; set; }
    /// <summary>
    /// A sub-assembly's components as this instance shows them (hidden, suppressed, colored), by their path in it; the
    /// sub-assembly's file is not changed.
    /// </summary>
    public Dictionary<string, ComponentOverride>? Overrides { get; set; }
    /// <summary>The component pattern this is a copy made by (<see cref="ComponentPattern.Id"/>): placed by it.</summary>
    public string? Pattern { get; set; }
}

/// <summary>
/// Which part a component is, and where it was last found. The identity decides; the paths are where to look first.
/// </summary>
public sealed class PartRef
{
    /// <summary>The part's identity (<see cref="CadDocument.Id"/>).</summary>
    public string? PartId { get; set; }
    /// <summary>The revision the assembly last saw (its display cache is that revision's).</summary>
    public string? Revision { get; set; }
    /// <summary>The file, relative to the assembly's folder (forward slashes), when it was last found.</summary>
    public string? Path { get; set; }
    /// <summary>The file's absolute path when it was last found (a part moved with the assembly is found relatively).</summary>
    public string? AbsolutePath { get; set; }
    /// <summary>The part embedded in the assembly (an attachment, "parts/…") instead of a file: a virtual part.</summary>
    public string? Embedded { get; set; }
    /// <summary>An assembly file (<see cref="AssemblyDocument.Id"/> its identity), not a part: a sub-assembly.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Assembly { get; set; }

    public PartRef Clone() => (PartRef)MemberwiseClone();
}

/// <summary>A rigid placement: rotation (rows of a 3×3 matrix) and translation, as stored.</summary>
public sealed class Placement
{
    /// <summary>Row-major rotation, 9 numbers.</summary>
    public double[] R { get; set; } = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    public Vec3 T { get; set; }

    public static Placement From(in Transform t) => new()
    {
        R = new[] { t.M00, t.M01, t.M02, t.M10, t.M11, t.M12, t.M20, t.M21, t.M22 },
        T = t.T,
    };

    public Transform ToTransform() => new(R[0], R[1], R[2], R[3], R[4], R[5], R[6], R[7], R[8], T);
}
