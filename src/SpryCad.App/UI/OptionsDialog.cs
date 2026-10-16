using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.Core;
using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.App.UI;

/// <summary>
/// The Options window, laid out as SolidWorks': two tabs, System Options (the user's own, for every document,
/// remembered in app data) and Document Properties (the active part's, saved with it and undone as one step), each a
/// list of pages on the left and the chosen page on the right. A new setting is a row on a page (<see cref="Form"/>),
/// a new group of them a page in <see cref="SystemPages"/> or <see cref="DocumentPages"/>: the window needs nothing else.
/// OK checks every page that was opened, then applies them; pages never opened are left as they were.
/// </summary>
public static class OptionsDialog
{
    /// <summary>
    /// What a page does on OK: <see cref="Validate"/> first (null: fine; else the problem, shown with OK refused), then
    /// <see cref="Pending"/>: the change to make (null: nothing changed on the page).
    /// </summary>
    sealed record Commit(Func<string?> Validate, Func<Action?> Pending);

    sealed record Context(MainWindow Owner, DocumentSession Part);

    sealed record Page(string Title, string Icon, Func<Form, Context, Commit> Build);

    // Updates: Windows only (Linux updates with its packages)
    static readonly Page[] SystemPages = OperatingSystem.IsWindows()
        ? new Page[] { new("View", "views", ViewPage), new("Graphics", "display", GraphicsPage), new("Files", "folder", FilesPage), new("Libraries", "table", LibrariesPage), new("Updates", "rebuild", UpdatesPage) }
        : new Page[] { new("View", "views", ViewPage), new("Graphics", "display", GraphicsPage), new("Files", "folder", FilesPage), new("Libraries", "table", LibrariesPage) };

    static readonly Page[] DocumentPages =
    {
        new("Units", "measure", UnitsPage),
        new("Appearance", "display", AppearancePage),
        new("Sheet Metal", "flatpattern", SheetMetalPage),
    };

    // the tab and page shown last, shown again next time (this run only)
    static int _lastTab, _lastPage;

    public static async Task Show(MainWindow owner)
    {
        var ctx = new Context(owner, owner.Session);
        var asm = owner.IsAssemblyActive;
        var tabs = new (string title, Page[] pages, string? note)[]
        {
            ("System Options", SystemPages, null),
            (asm ? "Document Properties" : $"Document Properties ({owner.Session.Title.TrimEnd(' ', '*')})",
             asm ? Array.Empty<Page>() : DocumentPages,
             asm ? "Document properties are a part's (its units, color, sheet metal): an assembly has none yet." : null),
        };
        var built = new Dictionary<Page, (Control view, Commit commit)>();

        var header = new TextBlock { FontSize = TypeScale.Title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading, Margin = new Thickness(0, 0, 0, 10) };
        var body = new ContentControl();
        var right = new DockPanel { Margin = new Thickness(18, 14, 12, 0) };
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        right.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(4) };
        var left = new Border { Child = list, Background = Palette.Panel, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 1, 0) };
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        Grid.SetColumn(right, 1);
        middle.Children.Add(left); middle.Children.Add(right);

        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } };
        var bottom = new DockPanel { Margin = new Thickness(14, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons); bottom.Children.Add(error);
        var bottomBar = new Border { Child = bottom, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0) };

        // the tabs, as the CommandManager's (UI/Styles.axaml ToggleButton.tab)
        var tabButtons = tabs.Select(t => new ToggleButton { Content = t.title, Classes = { "tab" } }).ToArray();
        var tabBar = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 0), Children = { tabButtons[0], tabButtons[1] } },
            Background = Palette.Strip, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var root = new DockPanel();
        DockPanel.SetDock(tabBar, Dock.Top); DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(tabBar); root.Children.Add(bottomBar); root.Children.Add(middle);

        int tab = -1;
        bool switching = false;
        void ShowPage(int p)
        {
            var pages = tabs[tab].pages;
            if (p < 0 || p >= pages.Length) return;
            var page = pages[p];
            if (!built.TryGetValue(page, out var b))
            {
                var form = new Form();
                b = (form.Root, page.Build(form, ctx));
                built[page] = b;
            }
            header.Text = page.Title;
            body.Content = b.view;
            _lastPage = p;
        }
        void ShowTab(int t, int page)
        {
            tab = t; _lastTab = t;
            for (int i = 0; i < tabButtons.Length; i++) tabButtons[i].IsChecked = i == t;
            switching = true;
            list.Items.Clear();
            foreach (var pg in tabs[t].pages)
                list.Items.Add(new ListBoxItem
                {
                    Padding = new Thickness(8, 5),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Icons.Make(pg.Icon, 16), new TextBlock { Text = pg.Title, VerticalAlignment = VerticalAlignment.Center } } },
                });
            switching = false;
            if (tabs[t].pages.Length == 0)
            {
                header.Text = tabs[t].title;
                body.Content = new TextBlock { Text = tabs[t].note, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted };
                return;
            }
            page = Math.Clamp(page, 0, tabs[t].pages.Length - 1);
            list.SelectedIndex = page;
            ShowPage(page);
        }
        list.SelectionChanged += (_, _) => { if (!switching && list.SelectedIndex >= 0) ShowPage(list.SelectedIndex); };
        for (int i = 0; i < tabButtons.Length; i++)
        {
            int t = i;
            tabButtons[i].Click += (_, _) => { if (t != tab) ShowTab(t, 0); else tabButtons[t].IsChecked = true; };
        }
        ShowTab(_lastTab, _lastPage);

        var w = new Window
        {
            Title = "Options", Width = 760, Height = 520, MinWidth = 620, MinHeight = 420,
            CanResize = true, CanMinimize = false, CanMaximize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root,
        };
        Dialogs.Compact(w);

        ok.Click += (_, _) =>
        {
            // every opened page checked first: nothing is applied while one has a problem, which is shown on its page
            foreach (var (t, (title, pages, _)) in tabs.Select((x, i) => (i, x)))
                for (int p = 0; p < pages.Length; p++)
                    if (built.TryGetValue(pages[p], out var b) && b.commit.Validate() is { } problem)
                    {
                        if (tab != t) ShowTab(t, p); else { list.SelectedIndex = p; ShowPage(p); }
                        error.Text = $"{pages[p].Title}: {problem}";
                        return;
                    }
            // the user's settings at once and remembered; the part's in one undo step (only if something changed)
            foreach (var page in SystemPages)
                if (built.TryGetValue(page, out var b)) b.commit.Pending()?.Invoke();
            UserSettings.Save();
            var docChanges = DocumentPages.Where(built.ContainsKey).Select(pg => built[pg].commit.Pending()).OfType<Action>().ToList();
            if (docChanges.Count > 0) ctx.Part.Change(() => { foreach (var c in docChanges) c(); });
            owner.Viewport.Redraw();
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    // ------------------------------------------------------------------------------------------
    // the pages

    static Commit ViewPage(Form f, Context c)
    {
        var vp = c.Owner.Viewport;
        f.Section("Mouse");
        var wheel = f.Check("Reverse mouse wheel zoom direction", vp.ReverseWheel);
        f.Section("Rotation");
        var pivots = new[] { RotateCenter.Selection, RotateCenter.ModelCenter, RotateCenter.UnderCursor };
        var pivot = f.Row("Rotate about", Combo(new[] { "The selection (default)", "The model center", "The point under the cursor" }, Array.IndexOf(pivots, vp.RotateAbout)),
            "Middle-click a vertex, edge or face to rotate about that spot instead, until you middle-click empty space.");
        f.Section("Projection");
        var persp = f.Check("Perspective view", vp.Camera.Perspective, "Otherwise orthographic (parallel lines stay parallel), as drawings are.");
        return new Commit(() => null, () => () =>
        {
            // the user's, not the document's: remembered, no undo step, the part not marked modified
            vp.ReverseWheel = UserSettings.ReverseWheel = wheel.IsChecked == true;
            vp.Camera.Perspective = UserSettings.Perspective = persp.IsChecked == true;
            if (pivot.SelectedIndex >= 0) vp.RotateAbout = UserSettings.RotateAbout = pivots[pivot.SelectedIndex];
        });
    }

    static Commit GraphicsPage(Form f, Context c)
    {
        var vp = c.Owner.Viewport;
        f.Section("Graphics card");
        // the driver's renderer (vendor / renderer / version as logged)
        var renderer = App.GlInfo?.Split(" / ") is { Length: >= 2 } parts ? parts[1] : App.GlInfo;
        f.Row("Drawing with", new SelectableTextBlock { Text = renderer ?? "(not started yet)", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (Viewport.ViewportControl.IsSoftwareRenderer(renderer))
            f.Hint("A software renderer: no graphics card draws the view, so large models are slow to turn. On a laptop with two "
                + "graphics chips, choose High performance below. If that does not help, the graphics driver may be missing or "
                + "disabled (Device Manager > Display adapters).", 150, Palette.Danger);
        ComboBox? gpu = null, api = null;
        if (OperatingSystem.IsWindows())
        {
            gpu = f.Row("Graphics processor", Combo(new[] { "Let Windows decide", "Power saving", "High performance" }, (int)GpuPreference.Get()),
                "Windows' own setting for SpryCAD (as in Settings > System > Display > Graphics): on a laptop with two graphics "
                + "chips, which one draws the view. Takes effect when SpryCAD starts again.");
            api = f.Row("Draw through", Combo(new[] { "Direct3D (default)", "OpenGL (the card's own driver)" }, UserSettings.PreferOpenGL ? 1 : 0),
                "Try OpenGL if the view draws wrongly or slowly; where it cannot start, Direct3D is used. Takes effect when SpryCAD starts again.");
        }
        f.Section("Quality");
        var levels = new[] { 0, 2, 4 };
        var aa = f.Row("Anti-aliasing", Combo(new[] { "Off", "2x", "4x (default)" }, Math.Max(0, Array.IndexOf(levels, UserSettings.AntiAliasing))),
            "Smooths the edges of faces and lines. Off draws fastest, which helps most without a graphics card.");
        return new Commit(() => null, () => () =>
        {
            if (aa.SelectedIndex >= 0) vp.AntiAliasing = UserSettings.AntiAliasing = levels[aa.SelectedIndex];
            if (!OperatingSystem.IsWindows()) return;
            if (api is { SelectedIndex: >= 0 }) UserSettings.PreferOpenGL = api.SelectedIndex == 1;
            if (gpu is { SelectedIndex: >= 0 } && (GpuPreference.Choice)gpu.SelectedIndex != GpuPreference.Get())
            {
                try { GpuPreference.Set((GpuPreference.Choice)gpu.SelectedIndex); }
                catch (Exception ex) { App.Log(ex, "Setting the graphics processor"); }
            }
        });
    }

    static Commit FilesPage(Form f, Context c)
    {
        f.Section("Saving parts");
        var author = new TextBox { Text = UserSettings.AuthorName, MinWidth = 200 };
        f.Row("Your name", author, "Every save of a part is kept in its history, under this name.");
        var modes = new[] { KeepDisplay.Always, KeepDisplay.SlowParts, KeepDisplay.Never };
        var keep = Combo(new[] { "Of every part", "Of slow parts", "Never" }, Array.IndexOf(modes, UserSettings.KeepDisplay));
        keep.MinWidth = 150;
        var secs = new TextBox { Text = Expr.Format(UserSettings.KeepDisplaySeconds), Width = 60 };
        var slower = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "slower than", VerticalAlignment = VerticalAlignment.Center }, secs, new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center } } };
        void Sync() => slower.IsEnabled = keep.SelectedIndex == 1;
        keep.SelectionChanged += (_, _) => Sync();
        Sync();
        f.Row("Keep the display", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { keep, slower } },
            "The part keeps its display as last built, so it shows at once when opened while it rebuilds (larger files).");
        f.Section("Saving assemblies");
        var meshes = f.Check("Keep the parts' meshes", UserSettings.AssemblyMeshes,
            "The assembly opens without rebuilding its parts, and shows parts that have gone missing (larger files). Otherwise its parts are rebuilt when it opens; lightweight parts always keep theirs.");
        double seconds = UserSettings.KeepDisplaySeconds;
        return new Commit(
            () => keep.SelectedIndex != 1 || (Expr.TryEval(secs.Text, null, out seconds, out _) && seconds >= 0) ? null : "Keep the display: a number of seconds, 0 or more.",
            () => () =>
            {
                if (!string.IsNullOrWhiteSpace(author.Text)) UserSettings.AuthorName = author.Text.Trim();
                UserSettings.AssemblyMeshes = meshes.IsChecked == true;
                if (keep.SelectedIndex >= 0) UserSettings.KeepDisplay = modes[keep.SelectedIndex];
                if (keep.SelectedIndex == 1) UserSettings.KeepDisplaySeconds = seconds;
            });
    }

    static Commit LibrariesPage(Form f, Context c)
    {
        var (path, source) = Libraries.Location;
        var byAdmin = source is LibraryConfigSource.Environment or LibraryConfigSource.MachinePolicy;
        f.Section("Library set");
        var box = new TextBox { Text = UserSettings.LibrariesPath ?? "", MinWidth = 300, IsEnabled = !byAdmin, Watermark = "None: SpryCAD's built-in standards" };
        var browse = new Button { Content = "Browse...", IsEnabled = !byAdmin };
        f.Row("Location", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, browse } },
            "The folder on a shared drive (or its libraries.json) listing the libraries everyone uses: thread sizes, clearance holes, a company's preferred sizes. "
            + "They are copied to this computer and read from there, so SpryCAD works offline with the copies last read.");
        if (byAdmin)
            f.Hint($"Set by your administrator ({(source == LibraryConfigSource.Environment ? $"the {LibrarySync.EnvironmentVariable} environment variable" : LibrarySync.MachinePolicyFile)}): {path}", 150, Palette.Warning);
        async Task<string?> PickFolder(string title)
        {
            var picked = await c.Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        }
        browse.Click += async (_, _) => { if (await PickFolder("Library set folder") is { } p) box.Text = p; };

        f.Section("In use");
        foreach (var lib in LibraryLink.Current.Libraries)
            f.Row(lib.Name, new SelectableTextBlock
            {
                Text = $"{lib.Layer}, {lib.Tables.Count} tables{(lib.Restrictions.Count > 0 ? $", {lib.Restrictions.Count} restrictions" : "")}{(lib.Path != null ? " · " + lib.Path : " · built in")}",
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            });
        if (LibraryLink.Current.Libraries.Any(l => l.Tables.Any(t => !t.Verified)))
            f.Hint("Some tables are not yet verified against their published standards: check values before relying on them.", 0, Palette.Warning);
        foreach (var p in Libraries.Problems) f.Hint(p, 0, Palette.Danger);
        var reload = new Button { Content = "Reload Now" };
        reload.Click += (_, _) => { Libraries.Reload(); c.Owner.Status("Reading the standards libraries again (Options shows them next time it opens)."); };
        f.Row("", reload, "SpryCAD reads them again every few minutes; parts keep the values they were made with until you accept an update (Tools > Library Updates).");

        f.Section("Set up a shared library set");
        var create = new Button { Content = "Create in a Folder..." };
        f.Row("Administrators", create,
            "Writes standards.sprlib (SpryCAD's standards, to correct or extend) and libraries.json listing it into a folder, e.g. on a file server. "
            + $"Give everyone read access and only the libraries' owners write access, then point each computer at it: the {LibrarySync.EnvironmentVariable} environment variable, "
            + $"the first line of {LibrarySync.MachinePolicyFile}, or this page.");
        create.Click += async (_, _) =>
        {
            if (await PickFolder("Folder for the shared library set") is not { } folder) return;
            var json = Path.Combine(folder, LibraryConfig.FileName);
            if (File.Exists(json)) { await Dialogs.Message(c.Owner, "Libraries", $"{json} already exists: it was left as it is."); return; }
            try
            {
                var lib = Path.Combine(folder, "standards" + LibraryFile.Extension);
                if (!File.Exists(lib)) LibraryFile.WriteBuiltIn(lib, UserSettings.AuthorName);
                File.WriteAllText(json, System.Text.Json.JsonSerializer.Serialize(new LibraryConfig { Libraries = { Path.GetFileName(lib) } }, Serialization.Options));
                if (!byAdmin) box.Text = folder;
                await Dialogs.Message(c.Owner, "Libraries", $"Created {json} and {Path.GetFileName(lib)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Message(c.Owner, "Libraries", "The library set could not be created: " + ex.Message);
            }
        };

        return new Commit(() => null, () =>
        {
            var v = box.Text?.Trim();
            if (string.IsNullOrEmpty(v)) v = null;
            if (v == UserSettings.LibrariesPath) return null;
            return () => { UserSettings.LibrariesPath = v; Libraries.Reload(); };
        });
    }

    static Commit UpdatesPage(Form f, Context c)
    {
        var can = Updater.CanUpdate;
        f.Section("Version");
        f.Row("Installed", new SelectableTextBlock { Text = Updater.CurrentVersion, VerticalAlignment = VerticalAlignment.Center });
        var channel = f.Row("Channel", Combo(new[] { "Stable: releases", "Dev: every change, as it is made" },
                Math.Max(0, Array.IndexOf(Updater.Channels, Updater.Channel ?? "stable"))),
            "Dev builds have the newest features and fixes first, and are tested less. Switching to Stable moves you to the next release newer than your version; nothing is downgraded.");
        channel.IsEnabled = can;
        string Picked() => Updater.Channels[Math.Max(0, channel.SelectedIndex)];

        // the newest version on the picked channel, checked when the page opens, with Check Now, Update and Install Now
        var latest = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var checkNow = new Button { Content = "Check Now", IsEnabled = can };
        var update = new Button { IsVisible = false };
        var restart = new Button { Content = "Install Now", IsVisible = false };
        ToolTip.SetTip(restart, "Closes SpryCAD, installs the update and reopens. Prompts to save files with changes.");
        f.Row("Latest", new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6, Children = { latest, checkNow, update, restart } });
        var changes = f.Row("Changes", new Button { Content = "Change Log...", HorizontalAlignment = HorizontalAlignment.Left },
            "What each build of the picked channel changed, newest first.");
        changes.Click += (_, _) => _ = Updater.ShowChangelog(TopLevel.GetTopLevel(changes) as Window ?? c.Owner, Picked());

        f.Section("Automatic checks");
        var check = f.Check("Check for updates when SpryCAD starts", UserSettings.CheckForUpdates,
            can ? "A newer version is offered, never installed without asking."
                : "This copy cannot update itself (a local build, or not installed by the setup).");

        Updater.Release? found = null;
        int checks = 0;
        async Task Check()
        {
            var mine = ++checks; // a newer check (the channel changed again) wins
            checkNow.IsEnabled = false; update.IsVisible = false; found = null;
            latest.Text = "Checking...";
            string text;
            try
            {
                var r = await Updater.Fetch(Picked());
                if (mine != checks) return;
                if (r == null) text = "The update information could not be verified. Try again later.";
                else if (Updater.IsNewer(r.Version, Updater.CurrentVersion))
                {
                    found = r;
                    text = $"{r.Version} is available";
                    update.Content = $"Update to {r.Version}";
                    update.IsVisible = true;
                }
                else text = $"{r.Version}: you are up to date";
            }
            catch (Exception ex)
            {
                if (mine != checks) return;
                App.Log(ex, "Checking for updates");
                text = Updater.FailureReason(ex);
            }
            latest.Text = text;
            checkNow.IsEnabled = true;
        }
        void Ready(string version)
        {
            latest.Text = $"{version} is downloaded: it installs when SpryCAD closes.";
            checkNow.IsEnabled = channel.IsEnabled = update.IsVisible = false;
            restart.IsVisible = true;
        }
        checkNow.Click += (_, _) => _ = Check();
        channel.SelectionChanged += (_, _) => { if (can && Updater.PendingVersion == null) _ = Check(); };
        // (a download already under way, started here or from the update card, is joined: never a second one)
        async Task Get(Updater.Release r)
        {
            update.Content = $"Update to {r.Version}";
            update.IsVisible = true;
            update.IsEnabled = checkNow.IsEnabled = channel.IsEnabled = false;
            try { await Updater.Download(r, new Progress<double>(p => latest.Text = $"Downloading {r.Version}… {p:P0}")); }
            catch (Exception ex)
            {
                App.Log(ex, "Downloading an update");
                latest.Text = "Download failed: " + Updater.FailureReason(ex);
                update.IsEnabled = checkNow.IsEnabled = channel.IsEnabled = true;
                return;
            }
            Ready(r.Version);
        }
        update.Click += (_, _) =>
        {
            if (found is not { } r) return;
            // the channel the update came from is the one followed from now on, even if Options is cancelled
            if (Picked() != Updater.Channel) { UserSettings.UpdateChannel = Picked(); UserSettings.SkippedUpdate = null; UserSettings.Save(); }
            _ = Get(r);
        };
        restart.Click += (_, _) => { (TopLevel.GetTopLevel(restart) as Window)?.Close(); Updater.RestartNow(c.Owner); };

        if (Updater.PendingVersion is { } pending) Ready(pending);
        else if (Updater.Downloading is { } dl) { found = dl; _ = Get(dl); }
        else if (can) _ = Check();
        else latest.Text = "Not checked: this copy cannot update itself.";

        return new Commit(() => null, () => () =>
        {
            var on = check.IsChecked == true;
            if (on && !UserSettings.CheckForUpdates) UserSettings.SkippedUpdate = null; // turned back on: offer everything again
            UserSettings.CheckForUpdates = on;
            if (Updater.CanUpdate && channel.SelectedIndex >= 0 && Updater.Channels[channel.SelectedIndex] != Updater.Channel)
            {
                UserSettings.UpdateChannel = Updater.Channels[channel.SelectedIndex];
                UserSettings.SkippedUpdate = null; // a version skipped on one channel is no reason to skip on the other
            }
        });
    }

    static Commit UnitsPage(Form f, Context c)
    {
        var doc = c.Part.Doc;
        f.Section("Unit system");
        var units = f.Row("Units", Combo(new[] { "MMGS (millimeter, gram, second)", "IPS (inch, pound, second)" }, doc.Units == UnitSystem.IPS ? 1 : 0),
            "What values are shown in, and what a value typed without a unit is in. Changing it changes nothing in the model: "
            + "values keep the units they were typed in (10mm stays 10mm in an IPS part), and any unit can be typed in either.");
        return new Commit(() => null, () =>
        {
            var u = units.SelectedIndex == 1 ? UnitSystem.IPS : UnitSystem.MMGS;
            // (new parts start in them too)
            return u == doc.Units ? null : () => { doc.Units = u; UserSettings.Units = u; };
        });
    }

    static Commit AppearancePage(Form f, Context c)
    {
        var doc = c.Part.Doc;
        f.Section("Part");
        var text = new TextBox { Text = string.Join(", ", doc.Color.Select(x => ((int)Math.Round(x * 255)).ToString())), Width = 140 };
        var swatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderBrush = Palette.FieldBorder, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        double[]? Parse()
        {
            var p = (text.Text ?? "").Split(',').Select(s => double.TryParse(s.Trim(), out var v) && v >= 0 && v <= 255 ? v / 255 : -1).ToArray();
            return p.Length == 3 && p.All(v => v >= 0) ? p : null;
        }
        void Preview() => swatch.Background = Parse() is { } rgb
            ? new SolidColorBrush(Color.FromRgb((byte)Math.Round(rgb[0] * 255), (byte)Math.Round(rgb[1] * 255), (byte)Math.Round(rgb[2] * 255)))
            : Brushes.Transparent;
        text.TextChanged += (_, _) => Preview();
        Preview();
        f.Row("Color", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, swatch } }, "Red, green, blue: 0 to 255 each. Its finish, opacity and the presets: Tools > Appearance.");
        // shown rounded to whole numbers: untouched, the stored color is kept as it is (not marked changed)
        var shown = text.Text;
        return new Commit(
            () => Parse() == null ? "Color: three numbers from 0 to 255, separated by commas." : null,
            () => text.Text != shown && Parse() is { } rgb && !rgb.SequenceEqual(doc.Color) ? () => doc.Color = rgb : null);
    }

    static Commit SheetMetalPage(Form f, Context c)
    {
        var s = c.Part;
        var sheet = s.Doc.SheetMetal ?? new SheetMetalSettings();
        f.Section("Bend allowance");
        var method = f.Row("Method", Combo(new[] { "K-factor", "DIN 6935" }, sheet.Method == BendAllowanceMethod.Din6935 ? 1 : 0));
        var k = f.Row("K-factor", new TextBox { Text = sheet.KFactor, Width = 120, HorizontalAlignment = HorizontalAlignment.Left },
            "0 to 1, or an expression of the part's variables. For this part's flat patterns (a Flat Pattern can override it).");
        void Sync() => k.IsEnabled = method.SelectedIndex == 0;
        method.SelectionChanged += (_, _) => Sync();
        Sync();
        return new Commit(
            () => method.SelectedIndex != 0 || (Expr.TryEvaluate((k.Text ?? "").Trim(), s.Lookup, out var v, out _) && v.IsNumber && v.Value >= 0 && v.Value <= 1) ? null
                : "K-factor: a number from 0 to 1, or an expression of the part's variables.",
            () =>
            {
                var m = method.SelectedIndex == 1 ? BendAllowanceMethod.Din6935 : BendAllowanceMethod.KFactor;
                var kText = m == BendAllowanceMethod.KFactor ? (k.Text ?? "").Trim() : sheet.KFactor;
                return m == sheet.Method && kText == sheet.KFactor ? null : () => s.Doc.SheetMetal = new SheetMetalSettings { Method = m, KFactor = kText };
            });
    }

    static ComboBox Combo(string[] items, int selected) => new() { ItemsSource = items, SelectedIndex = selected, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A page's form: sections, rows of a label and its control (the controls lined up in one column), check boxes and
    /// grey hints under what they explain. Kept narrow enough to read when the window is made wide.
    /// </summary>
    sealed class Form
    {
        const double LabelWidth = 150;
        public readonly StackPanel Root = new() { Spacing = 6, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        bool _first = true;

        public void Section(string title)
        {
            Root.Children.Add(new TextBlock
            {
                Text = title, FontSize = TypeScale.Header, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading,
                Margin = new Thickness(0, _first ? 0 : 14, 0, 2),
            });
            _first = false;
        }

        public T Row<T>(string label, T control, string? hint = null) where T : Control
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            Root.Children.Add(g);
            if (hint != null) Hint(hint, LabelWidth);
            return control;
        }

        public CheckBox Check(string text, bool on, string? hint = null)
        {
            var cb = new CheckBox { Content = text, IsChecked = on };
            Root.Children.Add(cb);
            if (hint != null) Hint(hint, 28); // under the text, past the box
            return cb;
        }

        public void Hint(string text, double indent = 0, IBrush? color = null) => Root.Children.Add(new TextBlock
        {
            Text = text, FontSize = TypeScale.Small, Foreground = color ?? Palette.TextMuted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, -2, 0, 4),
        });
    }
}
