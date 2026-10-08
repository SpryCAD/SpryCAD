namespace SpryCad.App.Session;

/// <summary>Most-recently-used part files (newest first), kept in the user's application data folder.</summary>
public static class RecentFiles
{
    const int Max = 10;
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpryCAD", "recent.txt");

    public static List<string> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? File.ReadAllLines(StorePath).Where(l => !string.IsNullOrWhiteSpace(l) && File.Exists(l)).Distinct(StringComparer.OrdinalIgnoreCase).Take(Max).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    public static void Add(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = Load();
            list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, full);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllLines(StorePath, list.Take(Max));
        }
        catch { /* the list is a convenience only */ }
    }
}
