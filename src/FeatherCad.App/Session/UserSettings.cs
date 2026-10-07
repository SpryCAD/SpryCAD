namespace FeatherCad.App.Session;

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

/// <summary>Per-user view preferences (not part of the document), stored as key=value lines in app data.</summary>
public static class UserSettings
{
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD", "settings.txt");

    public static bool ReverseWheel { get; set; }
    public static bool Perspective { get; set; }
    /// <summary>
    /// Save every part as a container (ZIP with a preview image, which Explorer shows as its thumbnail), not only
    /// parts with embedded files. On by default.
    /// </summary>
    public static bool SaveContainer { get; set; } = true;
    /// <summary>What the view rotates about: by default the selection (its last vertex, edge or face), else the model's middle.</summary>
    public static RotateCenter RotateAbout { get; set; } = RotateCenter.Selection;
    /// <summary>Where the list of open parts was last (screen position and size); null: beside the tabs.</summary>
    public static (int x, int y, int w, int h)? PartsWindow { get; set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            foreach (var line in File.ReadAllLines(StorePath))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
                if (k == nameof(ReverseWheel)) ReverseWheel = v == "1";
                else if (k == nameof(Perspective)) Perspective = v == "1";
                else if (k == nameof(SaveContainer)) SaveContainer = v == "1";
                else if (k == nameof(RotateAbout) && Enum.TryParse<RotateCenter>(v, out var rc)) RotateAbout = rc;
                else if (k == nameof(PartsWindow) && v.Split(',') is { Length: 4 } p && p.All(x => int.TryParse(x, out _)))
                    PartsWindow = (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            }
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
                $"{nameof(ReverseWheel)}={(ReverseWheel ? 1 : 0)}",
                $"{nameof(Perspective)}={(Perspective ? 1 : 0)}",
                $"{nameof(SaveContainer)}={(SaveContainer ? 1 : 0)}",
                $"{nameof(RotateAbout)}={RotateAbout}",
            };
            if (PartsWindow is { } pw) lines.Add($"{nameof(PartsWindow)}={pw.x},{pw.y},{pw.w},{pw.h}");
            File.WriteAllLines(StorePath, lines);
        }
        catch { /* preferences are a convenience only */ }
    }
}
