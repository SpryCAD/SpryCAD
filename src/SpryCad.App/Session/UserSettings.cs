namespace SpryCad.App.Session;

/// <summary>What the view rotates about when no rotation center has been set by a middle-click.</summary>
public enum RotateCenter
{
    /// <summary>The middle of the model (SolidWorks' default).</summary>
    ModelCenter,
    /// <summary>The most recently selected vertex, edge or face.</summary>
    Selection,
    /// <summary>The point on the model under the cursor when the rotation starts (as Onshape/Fusion).</summary>
    UnderCursor,
}

/// <summary>The order of the list of open parts.</summary>
public enum PartsOrder
{
    /// <summary>As the document tabs are.</summary>
    Tabs,
    /// <summary>By name (Part2 before Part10).</summary>
    Name,
    /// <summary>The most recently opened first.</summary>
    RecentlyOpened,
}

/// <summary>When a part saved as a container keeps its display (shown at once when opened, while it rebuilds).</summary>
public enum KeepDisplay
{
    Never,
    /// <summary>When rebuilding it takes at least <see cref="UserSettings.KeepDisplaySeconds"/>.</summary>
    SlowParts,
    Always,
}

/// <summary>Per-user view preferences (not part of the document), stored as key=value lines in app data.</summary>
public static class UserSettings
{
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpryCAD", "settings.txt");

    public static bool ReverseWheel { get; set; }
    public static bool Perspective { get; set; }
    /// <summary>
    /// Save every part as a container (ZIP with a preview image, which Explorer shows as its thumbnail), not only
    /// parts with embedded files. On by default.
    /// </summary>
    public static bool SaveContainer { get; set; } = true;
    /// <summary>When saved parts keep their display (in containers only). Every part by default: it shows at once when opened.</summary>
    public static KeepDisplay KeepDisplay { get; set; } = KeepDisplay.Always;
    /// <summary>How long a part takes to rebuild for it to keep its display (<see cref="KeepDisplay.SlowParts"/>).</summary>
    public static double KeepDisplaySeconds { get; set; } = 1;
    /// <summary>Saved assemblies keep their parts' meshes (open without rebuilding them). On by default.</summary>
    public static bool AssemblyMeshes
    {
        get => Core.Assembly.AssemblyModel.KeepMeshes;
        set => Core.Assembly.AssemblyModel.KeepMeshes = value;
    }
    /// <summary>What the view rotates about: by default the selection (its last vertex, edge or face), else the model's middle.</summary>
    public static RotateCenter RotateAbout { get; set; } = RotateCenter.Selection;
    /// <summary>Where the list of open parts was last (screen position and size); null: beside the tabs.</summary>
    public static (int x, int y, int w, int h)? PartsWindow { get; set; }
    /// <summary>The task pane stays open beside the graphics area (else it slides out over it and tucks away).</summary>
    public static bool TaskPinned { get; set; }
    /// <summary>The task pane was open when the app last closed (pinned, it opens again at start).</summary>
    public static bool TaskOpen { get; set; }
    public static int TaskWidth { get; set; } = 300;
    /// <summary>How the list of open parts is ordered.</summary>
    public static PartsOrder PartsSort { get; set; } = PartsOrder.Tabs;
    /// <summary>Look for a newer version at start (Windows, installed copies only: see Updater). On by default.</summary>
    public static bool CheckForUpdates { get; set; } = true;
    /// <summary>The version the user chose to skip: not offered again at start (Help > Check for Updates still does).</summary>
    public static string? SkippedUpdate { get; set; }
    /// <summary>The update channel picked in Options ("stable" or "dev"); null: the one the installed build came from.</summary>
    public static string? UpdateChannel { get; set; }

    /// <summary>
    /// The settings file's layout. 2: keep every part's display by default. A file before it holds the old default
    /// (slow parts only), saved like any choice: taken as the default, now every part.
    /// </summary>
    const int Version = 2;

    public static void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            int version = 1;
            foreach (var line in File.ReadAllLines(StorePath))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
                if (k == nameof(ReverseWheel)) ReverseWheel = v == "1";
                else if (k == nameof(Perspective)) Perspective = v == "1";
                else if (k == nameof(SaveContainer)) SaveContainer = v == "1";
                else if (k == nameof(KeepDisplay) && Enum.TryParse<KeepDisplay>(v, out var kd)) KeepDisplay = kd;
                else if (k == nameof(KeepDisplaySeconds) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ks) && ks >= 0) KeepDisplaySeconds = ks;
                else if (k == nameof(AssemblyMeshes)) AssemblyMeshes = v == "1";
                else if (k == nameof(RotateAbout) && Enum.TryParse<RotateCenter>(v, out var rc)) RotateAbout = rc;
                else if (k == nameof(PartsWindow) && v.Split(',') is { Length: 4 } p && p.All(x => int.TryParse(x, out _)))
                    PartsWindow = (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                else if (k == nameof(TaskPinned)) TaskPinned = v == "1";
                else if (k == nameof(TaskOpen)) TaskOpen = v == "1";
                else if (k == nameof(TaskWidth) && int.TryParse(v, out var dw)) TaskWidth = dw;
                else if (k == nameof(PartsSort) && Enum.TryParse<PartsOrder>(v, out var po)) PartsSort = po;
                else if (k == nameof(CheckForUpdates)) CheckForUpdates = v == "1";
                else if (k == nameof(SkippedUpdate)) SkippedUpdate = v.Length > 0 ? v : null;
                else if (k == nameof(UpdateChannel)) UpdateChannel = v.Length > 0 ? v : null;
                else if (k == nameof(Version) && int.TryParse(v, out var sv)) version = sv;
            }
            if (version < 2 && KeepDisplay == KeepDisplay.SlowParts) KeepDisplay = KeepDisplay.Always;
        }
        catch { /* defaults */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var lines = new List<string>
            {
                $"{nameof(Version)}={Version}",
                $"{nameof(ReverseWheel)}={(ReverseWheel ? 1 : 0)}",
                $"{nameof(Perspective)}={(Perspective ? 1 : 0)}",
                $"{nameof(SaveContainer)}={(SaveContainer ? 1 : 0)}",
                $"{nameof(KeepDisplay)}={KeepDisplay}",
                $"{nameof(KeepDisplaySeconds)}={KeepDisplaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"{nameof(AssemblyMeshes)}={(AssemblyMeshes ? 1 : 0)}",
                $"{nameof(RotateAbout)}={RotateAbout}",
            };
            if (PartsWindow is { } pw) lines.Add($"{nameof(PartsWindow)}={pw.x},{pw.y},{pw.w},{pw.h}");
            lines.Add($"{nameof(TaskPinned)}={(TaskPinned ? 1 : 0)}");
            lines.Add($"{nameof(TaskOpen)}={(TaskOpen ? 1 : 0)}");
            lines.Add($"{nameof(TaskWidth)}={TaskWidth}");
            lines.Add($"{nameof(PartsSort)}={PartsSort}");
            lines.Add($"{nameof(CheckForUpdates)}={(CheckForUpdates ? 1 : 0)}");
            if (SkippedUpdate != null) lines.Add($"{nameof(SkippedUpdate)}={SkippedUpdate}");
            if (UpdateChannel != null) lines.Add($"{nameof(UpdateChannel)}={UpdateChannel}");
            File.WriteAllLines(StorePath, lines);
        }
        catch { /* preferences are a convenience only */ }
    }
}
