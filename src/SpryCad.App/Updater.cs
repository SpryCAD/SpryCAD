using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// Updates on Windows, for copies installed by the setup. The packages workflow publishes a feed per channel
/// (stable: tagged releases; dev: every push to master) with the latest version and its installer's URL, SHA-256
/// and size, and signs it (build/publish-r2.py). At start (unless turned off in Options) and from Help > Check for
/// Updates, the app reads its channel's feed, checks the signature against the key below, and offers a newer
/// version. The installer it downloads must match the signed SHA-256; it runs silently once SpryCAD has closed
/// (after the usual questions about unsaved parts), into the same install (per user or all users), and starts
/// SpryCAD again if asked to. Linux updates through the package manager (apt).
/// The feed's URL and the build's channel are built in by the workflow (SPRYCAD_UPDATE_URL, SPRYCAD_UPDATE_CHANNEL
/// in the .csproj); a local build has neither and never checks. The user can pick the other channel in Options
/// (UserSettings.UpdateChannel), which then holds through updates: a dev build installed from the dev feed keeps
/// following stable if that was picked, and the other way round.
/// </summary>
static class Updater
{
    /// <summary>
    /// The public half of the feed's signing key (ECDSA P-256; the private half is the UPDATE_SIGNING_KEY secret).
    /// A new key means a release signed with the old one that carries the new one, before the feed switches.
    /// </summary>
    const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdO4Cv6Bn22+QJMGVQBjr29py1oDK
        Qx8xG0GkofnhgJVEkpjEO0tTchFtgrLFEEBDrV1DSMPqM0t4l2cNNNkOkQ==
        -----END PUBLIC KEY-----
        """;

    /// <summary>The newest version in a feed, and its Windows installer.</summary>
    internal sealed record Release(string Version, string Name, Uri Url, string Sha256, long Size);

    static readonly string? BaseUrl = Metadata("UpdateUrl"), BuildChannel = Metadata("UpdateChannel");

    /// <summary>The channels, as the feeds are named: tagged releases, and every change on master.</summary>
    public static readonly string[] Channels = { "stable", "dev" };

    /// <summary>The channel this copy follows: the user's choice, else the one it was built for.</summary>
    public static string? Channel => UserSettings.UpdateChannel is { } c && Channels.Contains(c) ? c : BuildChannel;

    static string? Metadata(string key) => Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    /// <summary>This copy's version without the commit: 0.2.0 or 0.2.0-dev.3.</summary>
    static string CurrentVersion => (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "").Split('+')[0];

    /// <summary>Installed by the setup (which leaves its uninstaller beside the app), so a setup can update it.</summary>
    static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>This copy can update itself: Windows, a published build with a feed, installed by the setup.</summary>
    public static bool CanUpdate => OperatingSystem.IsWindows() && BaseUrl != null && BuildChannel != null && Installed;

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"SpryCAD/{CurrentVersion}");
        h.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return h;
    }

    // the installer to run once the app has closed (Program.Main), and whether to start SpryCAD after it
    static string? _pending;
    static bool _relaunch;

    /// <summary>At start: offers a newer version (not one the user skipped), quietly; failures wait for the next start.</summary>
    public static async Task CheckAtStart(MainWindow w)
    {
        if (!CanUpdate || !UserSettings.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(5)); // after the window and any files opened with it
        Release? r;
        try { r = await Fetch(); }
        catch (Exception ex) { App.Log(ex, "Checking for updates"); return; }
        if (r != null && IsNewer(r.Version, CurrentVersion) && r.Version != UserSettings.SkippedUpdate) await Offer(w, r);
    }

    /// <summary>Help > Check for Updates: says what it found, or why this copy cannot update itself.</summary>
    public static async Task CheckNow(MainWindow w)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Dialogs.Message(w, "Check for Updates", "On Linux, SpryCAD updates with the system's packages (sudo apt update && sudo apt upgrade, with the SpryCAD repository added). The download page has the latest tarball.");
            return;
        }
        if (!CanUpdate)
        {
            await Dialogs.Message(w, "Check for Updates", BaseUrl == null
                ? "This is a local build of SpryCAD: it does not check for updates. Installed releases do."
                : "This copy of SpryCAD was not installed by its setup, so it cannot update itself. Install SpryCAD with the setup to get updates.");
            return;
        }
        if (_pending != null) { await Dialogs.Message(w, "Check for Updates", "An update is ready: it installs when SpryCAD closes."); return; }
        w.Status("Checking for updates...");
        Release? r;
        try { r = await Fetch(); }
        catch (Exception ex)
        {
            w.Status("");
            App.Log(ex, "Checking for updates");
            await Dialogs.Message(w, "Update check failed", $"Could not check for updates: {ex.Message}");
            return;
        }
        w.Status("");
        if (r == null) await Dialogs.Message(w, "Update check failed", "The update information could not be verified. Try again later.");
        else if (!IsNewer(r.Version, CurrentVersion)) await Dialogs.Message(w, "Check for Updates", $"SpryCAD {CurrentVersion} is the latest version ({Channel} channel).");
        else await Offer(w, r);
    }

    /// <summary>Asks about a newer version; downloads, verifies and queues it if wanted.</summary>
    static async Task Offer(MainWindow w, Release r)
    {
        var mb = r.Size / (1024.0 * 1024.0);
        var text = $"You have {CurrentVersion}. The update downloads now ({mb:0} MB) and installs when SpryCAD closes.";
        var buttons = new[] { "Update", "Later", "Skip This Version" };
        var choice = Card.ShowAsync(w, CardKind.Question, $"SpryCAD {r.Version} is available", new[] { Card.Text(text) }, buttons, 0, 1) is { } shown
            ? await shown
            : await Dialogs.YesNo(w, "Update available", $"SpryCAD {r.Version} is available. {text}", "Update", "Later") ? 0 : 1;
        if (choice == 2) { UserSettings.SkippedUpdate = r.Version; UserSettings.Save(); return; }
        if (choice != 0) return;

        string file;
        try
        {
            var progress = new Progress<double>(f => w.Status($"Downloading SpryCAD {r.Version}... {f:P0}"));
            file = await Download(r, progress);
        }
        catch (Exception ex)
        {
            w.Status("");
            App.Log(ex, "Downloading an update");
            await Dialogs.Message(w, "Update failed", $"The update could not be downloaded: {ex.Message}");
            return;
        }
        w.Status("");
        _pending = file;
        var now = Card.ShowAsync(w, CardKind.Info, $"SpryCAD {r.Version} is ready to install",
            new[] { Card.Text("SpryCAD closes (asking about unsaved parts first), installs the update and starts again.") },
            new[] { "Restart Now", "When I Close SpryCAD" }, 0, 1) is { } ready ? await ready == 0 : false;
        if (!now) { w.Status($"SpryCAD {r.Version} installs when SpryCAD closes."); return; }
        _relaunch = true;
        w.Close(); // closing can still be cancelled: then it installs at the next close, and starts SpryCAD again
    }

    /// <summary>The channel's feed, verified; null when its signature does not match (or it is for another channel).</summary>
    internal static async Task<Release?> Fetch()
    {
        var channel = Channel!;
        var url = $"{BaseUrl!.TrimEnd('/')}/updates/{channel}.json";
        var feed = await Http.GetByteArrayAsync(url);
        var sig = await Http.GetStringAsync(url + ".sig");
        return Parse(feed, sig, PublicKey, channel);
    }

    /// <summary>
    /// Reads a feed after checking its signature (ECDSA P-256 over SHA-256, DER, base64): null when the signature
    /// does not match, the feed is for another channel (a dev feed served as stable), or it has no Windows installer.
    /// </summary>
    internal static Release? Parse(byte[] feed, string signature, string publicKeyPem, string channel)
    {
        using (var ec = ECDsa.Create())
        {
            ec.ImportFromPem(publicKeyPem);
            byte[] sig;
            try { sig = Convert.FromBase64String(signature.Trim()); }
            catch (FormatException) { return null; }
            if (!ec.VerifyData(feed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
        }
        using var doc = JsonDocument.Parse(feed);
        var root = doc.RootElement;
        if (root.GetProperty("channel").GetString() != channel) return null;
        if (!root.TryGetProperty("windows", out var win) || !win.TryGetProperty("installer", out var inst)) return null;
        var name = Path.GetFileName(inst.GetProperty("name").GetString() ?? "");
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new Release(root.GetProperty("version").GetString()!, name, new Uri(inst.GetProperty("url").GetString()!),
            inst.GetProperty("sha256").GetString()!.ToLowerInvariant(), inst.GetProperty("size").GetInt64());
    }

    /// <summary>
    /// The version as a sortable key: 0.2.0 is (0, 2, 0, 0), 0.2.0-dev.3 (0, 2, 0, 3), three commits after the 0.2.0
    /// tag and so after it (the build scripts' scheme); null when it is neither form.
    /// </summary>
    internal static (int, int, int, int)? Key(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version.Split('+')[0], @"^(\d+)\.(\d+)\.(\d+)(?:-dev\.(\d+))?$");
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return (G(1), G(2), G(3), G(4));
    }

    internal static bool IsNewer(string candidate, string current) =>
        Key(candidate) is { } a && Key(current) is { } b && a.CompareTo(b) > 0;

    /// <summary>Downloads the installer into a temporary folder and checks its size and SHA-256 against the feed.</summary>
    static async Task<string> Download(Release r, IProgress<double> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SpryCAD-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.Name);
        using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                got += n;
                if (got > r.Size) break;
                progress.Report((double)got / r.Size);
            }
        }
        if (!Matches(file, r)) { File.Delete(file); throw new InvalidDataException("the download does not match the published checksum."); }
        _release = r;
        return file;
    }
    static Release? _release;

    static bool Matches(string file, Release r)
    {
        using var s = File.OpenRead(file);
        return Matches(s, r);
    }

    static bool Matches(Stream s, Release r) =>
        s.Length == r.Size && Convert.ToHexStringLower(SHA256.HashData(s)) == r.Sha256;

    /// <summary>
    /// After the app has closed (Program.Main): runs the downloaded installer silently into the same install, per user
    /// or for all users as this one is (all users asks for elevation). The file is checked again and held open until
    /// the installer has started, so nothing can swap it after the check.
    /// </summary>
    public static void RunPending()
    {
        if (_pending == null || _release == null || !OperatingSystem.IsWindows()) return;
        try
        {
            using var held = new FileStream(_pending, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Matches(held, _release)) return;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var allUsers = AppContext.BaseDirectory.StartsWith(pf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- " + (allUsers ? "/ALLUSERS" : "/CURRENTUSER") + (_relaunch ? " /RELAUNCH=1" : "");
            Process.Start(new ProcessStartInfo(_pending, args) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex, "Starting the update"); }
    }
}
