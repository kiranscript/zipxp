using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using StorageManager.Core;
using StorageManager.Core.Services;

namespace StorageManager.App;

public partial class MainWindow : Window
{
    private Engine Engine => App.Instance.Engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool ready, refreshing, syncing, commandBusy, hasFolders;
    private string? selectNext, drill;
    private bool largest;
    private string? activityHint;
    private IReadOnlyList<Observation> observations = [];
    private IReadOnlyList<WorkItem> jobs = [];
    private IReadOnlyList<ScanSummary> history = [];
    private IReadOnlyDictionary<string, (long Logical, long Allocated, int Unknown)> folderTotals = new Dictionary<string, (long, long, int)>();
    private ManagedFolder? Selected => RootPicker.SelectedItem as ManagedFolder;
    private DateTime PeriodStart => PeriodBox.SelectedIndex switch { 0 => DateTime.UtcNow.Date.AddDays(-6), 1 => DateTime.UtcNow.Date.AddDays(-29), _ => DateTime.MinValue };

    private static readonly (string Title, string Hint)[] PageInfo =
    [
        ("Overview", "How much space your folders use, and how much ZipXP has saved."),
        ("Managed folders", "The folders ZipXP looks after. Select one to change what happens to it."),
        ("Storage insights", "See what is taking up space inside a folder."),
        ("Activity", "Everything ZipXP has done, and anything that needs your attention.")
    ];

    private sealed record FolderRow(ManagedFolder Root, ScanSummary? Scan, string WorkerStatus)
    {
        public string Path => Root.Path;
        public string Name => Root.Name;
        public string Parent => Root.Parent;
        public string ShortPath => Plain.MiddleTrim(Root.Path, 38);
        public string Mode => Root.Policy.Paused ? "Paused" : Root.Policy.Enabled ? "On" : "Off";
        public string Tone => Root.Policy.Paused ? "Warn" : Root.Policy.Enabled ? "Good" : "Neutral";
        public string Size => Display.Bytes(Scan?.Logical);
        public string OnDisk => Display.Bytes(Scan?.Allocated);
        public string SavedText => Scan == null ? "—" : Scan.Logical > Scan.Allocated ? $"{Display.Bytes(Scan.Logical - Scan.Allocated)} ({Math.Round(100.0 * (Scan.Logical - Scan.Allocated) / Scan.Logical):0}%)" : "Nothing yet";
        public string Compressible => Scan == null ? "—" : Scan.Candidates == 0 ? "All compressed" : Display.Bytes(Scan.Candidates);
        public string Status => (Plain.Message(WorkerStatus) is "Up to date" ? "Watching" : Plain.Message(WorkerStatus)) + (Scan is { NativeDefaults: > 0 } ? $" · {Plain.BuiltIn} is on in {Plain.Count(Scan.NativeDefaults, "folder")}" : "");
    }
    private sealed record InsightRow(Observation Source, long? LogicalBytes, long? AllocatedBytes, string Notes)
    {
        public double Share { get; init; }
        public string SharePercent => Share < 0.005 ? "<1%" : $"{Math.Round(Share * 100):0}%";
        public long SavedBytes => LogicalBytes is long logical && AllocatedBytes is long used && logical > used ? logical - used : 0;
        public string Saved => SavedBytes > 0 ? $"{Display.Bytes(SavedBytes)} ({Math.Round(100.0 * SavedBytes / Math.Max(1, LogicalBytes ?? 1)):0}%)" : "–";
        public string Name => Source.Name;
        public string Kind => Source.Kind;
        public string Icon => Source.File?.Directory == true ? "\uE8B7" : "\uE7C3";
        public string Size => Display.Bytes(LogicalBytes);
        public string OnDisk => Display.Bytes(AllocatedBytes);
        public string Compressed => Source.File switch
        {
            null => "Unknown",
            { Directory: true, Compressed: true } => "New files (by Windows)",
            { Directory: true } => "",
            { Compressed: true } => "Yes",
            _ => "No"
        };
    }
    private sealed record JobRow(WorkItem Source)
    {
        public string Time => Plain.When(Source.FinishedUtc ?? Source.CreatedUtc);
        public string FileName => System.IO.Path.GetFileName(Source.Path);
        public string FolderName => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Source.Path)?.TrimEnd('\\') ?? "");
        public string Action => Source.State == "Succeeded"
            ? Source.Operation switch { "Compress" => "Compressed", "Decompress" => "Undid compression", "ClearDefault" => "Turned off Windows compression", _ => Plain.Action(Source.Operation) }
            : Plain.Action(Source.Operation);
        public string State => Plain.JobState(Source.State).Text;
        public string Tone => Plain.JobState(Source.State).Tone;
        public string Path => Source.Path;
        public string Saved => Source.Delta == 0 ? "–" : Plain.Saved(Source.Delta);
        public string Details => Source.State == "Succeeded" && Source.Delta is > 0 ? "" : Plain.Message(Source.Message);
    }
    private sealed record DriveRow(string Title, string FreeText, string Detail, GridLength Used, GridLength Free, bool Low);

    public MainWindow()
    {
        InitializeComponent();
        // A little taller than the default when the screen allows, so the Overview fits without scrolling.
        Height = Math.Min(940, SystemParameters.WorkArea.Height * 0.95);
        ShowPage(0);
        try { StartupBox.IsChecked = StartupRegistration.Enabled; } catch (Exception ex) { Fail(ex); }
        ready = true;
        Loaded += async (_, _) => await RefreshAsync();
        timer.Tick += async (_, _) => await RefreshAsync(); timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (!ready || refreshing || !IsVisible || App.Instance.Exiting) return;
        refreshing = true;
        try
        {
            var selectedId = selectNext ?? Selected?.Id;
            var since = PeriodStart;
            var snapshot = await Task.Run(() =>
            {
                var roots = Engine.Store.Roots();
                selectedId ??= roots.FirstOrDefault()?.Id;
                var scans = Engine.Store.Scans();
                var latest = roots.Select(root => new FolderRow(root, scans.FirstOrDefault(s => s.RootId == root.Id), Engine.FolderStatus(root.Id))).ToArray();
                var allEntries = roots.ToDictionary(r => r.Id, r => Engine.Store.Observations(r.Id));
                var entries = selectedId == null ? Array.Empty<Observation>() : allEntries.GetValueOrDefault(selectedId, []);
                var identities = allEntries.Values.SelectMany(e => e).Where(e => e.File is { Directory: false }).Select(e => e.File!).DistinctBy(f => f.Identity).ToArray();
                var managedAllocation = identities.Sum(f => f.Allocated ?? 0);
                // The uncompressed size never changes when a file is compressed, so it stays the fixed point to compare against.
                var managedOriginal = identities.Sum(f => f.Logical);
                var totals = BuildFolderTotals(entries, roots.FirstOrDefault(r => r.Id == selectedId)?.Path);
                List<DriveRow> volumes = [];
                foreach (var driveRoot in roots.Select(r => System.IO.Path.GetPathRoot(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var drive = new DriveInfo(driveRoot);
                        long total = Math.Max(1, drive.TotalSize), free = drive.AvailableFreeSpace;
                        var letter = driveRoot.TrimEnd('\\');
                        var used = Math.Clamp(1.0 - (double)free / total, 0, 1);
                        var low = free < total * 0.10 && free < 20L * 1024 * 1024 * 1024;
                        volumes.Add(new(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({letter})" : $"{drive.VolumeLabel} ({letter})", $"{Display.Bytes(free)} free",
                            $"{Display.Bytes(total - free)} used of {Display.Bytes(total)} on {(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({letter})" : $"{drive.VolumeLabel} ({letter})")}" + (low ? ". Nearly full." : ""),
                            new GridLength(used, GridUnitType.Star), new GridLength(1 - used, GridUnitType.Star), low));
                    }
                    catch (Exception ex) when (Scope.FileError(ex)) { }
                }
                return (Roots: roots, Rows: latest, Entries: entries, Totals: totals, Allocation: managedAllocation, Original: managedOriginal, Jobs: Engine.Store.Jobs(), Scans: scans, Volumes: volumes, Measured: Engine.Store.MeasuredSince(since));
            });
            if (App.Instance.Exiting) return;
            syncing = true;
            RootPicker.ItemsSource = snapshot.Roots;
            RootPicker.SelectedItem = snapshot.Roots.FirstOrDefault(r => r.Id == selectedId);
            if (!ReferenceEquals(ChartRootBox.ItemsSource, snapshot.Roots) && !SameRoots(snapshot.Roots)) ChartRootBox.ItemsSource = snapshot.Roots;
            ChartRootBox.SelectedItem = snapshot.Roots.FirstOrDefault(r => r.Id == selectedId);
            ChartRootBox.Visibility = snapshot.Roots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            FoldersGrid.ItemsSource = snapshot.Rows;
            FoldersGrid.SelectedItem = snapshot.Rows.FirstOrDefault(r => r.Root.Id == selectedId);
            syncing = false; selectNext = null;
            observations = snapshot.Entries; folderTotals = snapshot.Totals; jobs = snapshot.Jobs;
            history = snapshot.Scans.Where(s => s.RootId == selectedId && s.Utc >= since && s.Complete).OrderBy(s => s.Utc).TakeLast(32).ToArray();
            var known = snapshot.Rows.Where(r => r.Scan != null).ToArray();
            var shrunk = snapshot.Original - snapshot.Allocation;
            var any = known.Length > 0 && snapshot.Original > 0;
            var ratio = !any ? 1.0 : Math.Clamp((double)snapshot.Allocation / snapshot.Original, 0.0, 1.0);
            if (shrunk <= 0) ratio = 1.0;
            UsedCol.Width = new GridLength(Math.Max(ratio, 0.02), GridUnitType.Star); RestCol.Width = new GridLength(Math.Max(1 - ratio, 0.0), GridUnitType.Star);
            var percent = any && shrunk > 0 ? Math.Round(100.0 * shrunk / snapshot.Original) : 0;
            SavedHeadline.SetResourceReference(TextBlock.ForegroundProperty, shrunk > 0 ? "SystemFillColorSuccessBrush" : "TextFillColorPrimaryBrush");
            SavedHeadline.Text = known.Length == 0 ? "—" : shrunk > 0 ? $"{Display.Bytes(shrunk)} saved" : "Nothing saved yet";
            SavedPercent.Text = known.Length == 0 ? "Waiting for the first scan" : shrunk > 0 ? $"Your files take up {percent:0}% less room than they did" : "Compression hasn't started. Turn it on in Folder settings, or choose Compress now.";
            AllocatedCard.Text = known.Length == 0 ? "Using now: —" : $"Using now: {Display.Bytes(snapshot.Allocation)}";
            SavedLegend.Text = known.Length == 0 ? "Saved: —" : $"Saved: {(shrunk > 0 ? Display.Bytes(shrunk) : "nothing yet")}";
            OriginalCard.Text = known.Length == 0 ? "Original size: —" : $"Original size: {Display.Bytes(snapshot.Original)}";
            var estimate = known.Sum(r => r.Scan!.EstimatedSaving);
            var allDone = known.Length > 0 && estimate == 0;
            EstimateCard.Text = known.Length == 0 ? "—" : allDone ? "All compressed" : Display.Bytes(estimate);
            EstimateCaption.Text = allDone ? $"{Plain.Count(known.Sum(r => r.Scan!.Files), "file")} checked. Nothing more is worth compressing right now." : known.Length == 0 ? "Available after the first scan." : "A rough estimate. Actual savings may differ.";
            EstimateGlyph.Text = allDone ? "\uE73E" : "\uEA80";


            OverviewCompressButton.Visibility = known.Length > 0 && estimate > 0 ? Visibility.Visible : Visibility.Collapsed;
            var unread = known.Sum(r => r.Scan!.Unknown);
            ScopeCard.Text = snapshot.Roots.Count == 0 ? "No folders added yet"
                : known.Length == snapshot.Roots.Count ? Plain.Count(snapshot.Roots.Count, "folder")
                : $"{known.Length} of {Plain.Count(snapshot.Roots.Count, "folder")} scanned" + (unread > 0 ? $" · {Plain.Count(unread, "item")} couldn't be read" : "");
            var saved = snapshot.Measured.Delta;
            MeasuredCard.Text = snapshot.Roots.Count == 0 ? "—" : Plain.Saved(saved);
            MeasuredCaption.Text = snapshot.Measured.Measured == 0 ? ""
                : $"from {Plain.Count(snapshot.Measured.Measured, "compressed file")}" + (saved < 0 ? ". Files take up more room than before, for example after decompressing." : "");
            VolumesList.ItemsSource = snapshot.Volumes;
            VolumesEmpty.Visibility = snapshot.Volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VolumesNote.Visibility = Visibility.Collapsed;
            int pending = jobs.Count(j => j.Pending), failed = jobs.Count(j => j.State == "Failed");
            NavActivity.Content = failed > 0 ? $"Activity · {failed} failed" : "Activity";
            var lastDone = jobs.Where(j => j.State == "Succeeded" && j.FinishedUtc != null).OrderByDescending(j => j.FinishedUtc).FirstOrDefault();
            var lastText = lastDone == null ? "Nothing compressed yet." : $"{Plain.When(lastDone.FinishedUtc!.Value)} · {(lastDone.Operation switch { "Compress" => "compressed", "Decompress" => "decompressed", _ => "updated" })} {System.IO.Path.GetFileName(lastDone.Path)}";
            ActivityCard.Text = pending == 0 ? "Nothing waiting" : Plain.Count(pending, "file") + " waiting";
            WatchNote.Visibility = pending == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Auto-compress stops when ZipXP is closed: say so, quietly and for as long as it's true.
            var autoOn = snapshot.Roots.Any(r => r.Policy.Enabled);
            StartupHint.Visibility = autoOn && StartupBox.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
            LastActionCard.Text = lastText; LastActionCard.ToolTip = lastText;
            WorkerText.Text = Engine.CurrentFile == null ? Plain.Message(Engine.Status) : $"Working on {Engine.CurrentFile} · {pending} waiting";
            SetBusy(Engine.CurrentFile != null || commandBusy);
            ShowPauseState();
            FolderPauseItem.Header = Selected?.Policy.Paused == true ? "Resume this folder" : "Pause this folder";
            EmptyFolders.Visibility = snapshot.Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FoldersGrid.Visibility = snapshot.Roots.Count == 0 ? Visibility.Hidden : Visibility.Visible;
            // Everything except "Add folder" acts on the selected folder.
            // With no folders yet, the only sensible action is "Add folder", so the rest are hidden rather than greyed out.
            foreach (var action in FolderActions.Children.OfType<UIElement>().Skip(1))
            {
                action.IsEnabled = Selected != null;
                action.Visibility = snapshot.Roots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            var selectedRow = snapshot.Rows.FirstOrDefault(r => r.Root.Id == Selected?.Id);
            FolderCompressButton.IsEnabled = Selected != null && selectedRow?.Scan is { Candidates: > 0 };
            FolderCompressButton.ToolTip = FolderCompressButton.IsEnabled ? "Compress the files worth compressing once, without turning on automatic compression."
                : selectedRow?.Scan is { Candidates: 0 } ? "Everything worth compressing in this folder is already compressed." : "Select a folder that has been scanned.";
            var anyCompressed = selectedRow?.Scan is { } sc && sc.Logical > sc.Allocated;
            FolderUndoButton.IsEnabled = Selected != null && anyCompressed;
            FolderUndoButton.ToolTip = FolderUndoButton.IsEnabled ? "Decompress every file in the selected folder, putting it back to normal, and stop compressing it automatically."
                : "Nothing in the selected folder is compressed right now.";
            var selectedDone = selectedRow?.Scan is { Candidates: 0 };
            FoldersNote.Text = selectedDone ? "Everything worth compressing in this folder is already compressed."
                : "New folders are only scanned. Turn on Auto-compress in Folder settings, or choose Compress now. You can undo it at any time.";
            FoldersNote.Visibility = snapshot.Roots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            hasFolders = snapshot.Roots.Count > 0; ShowPage(Math.Max(0, Pages.SelectedIndex));
            WelcomeCard.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
            WelcomeSteps.Visibility = WelcomeCard.Visibility;
            // First run: just the welcome card and its one button. Everything else appears once there is something to show.
            OverviewBody.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            WorkerText.Visibility = hasFolders && (Pages.SelectedIndex != 0 || Engine.CurrentFile != null || Engine.Paused) ? Visibility.Visible : Visibility.Hidden;
            PauseButton.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            FolderActions.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            if (drill == null || Selected == null || !Scope.Within(drill, Selected.Path)) drill = Selected?.Path;
            RenderInsights(); RenderActivity(); RenderChart();
        }
        catch (Exception ex) { Notify("Couldn't update this window: " + Plain.Message(ex.Message), true); }
        finally { syncing = false; refreshing = false; }
    }

    private async Task Command(Func<Task> action)
    {
        if (commandBusy || App.Instance.Exiting) return;
        commandBusy = true; FolderActions.IsEnabled = false; SetBusy(true);
        try { await action(); }
        catch (Exception ex) { Fail(ex); }
        finally { commandBusy = false; FolderActions.IsEnabled = true; await RefreshAsync(); }
    }

    private ManagedFolder RequireRoot() => Selected ?? throw new InvalidOperationException("Add a folder first, then select it.");

    /// <summary>Shows a message above the page. Errors are shown in red and reworded in plain language.</summary>
    private void Notify(string text, bool error = false)
    {
        Banner.Text = text;
        BannerIcon.Text = error ? "\uEA39" : "\uE946";
        BannerIcon.SetResourceReference(TextBlock.ForegroundProperty, error ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        BannerHost.SetResourceReference(Border.BackgroundProperty, error ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorAttentionBackgroundBrush");
        BannerHost.Visibility = Visibility.Visible;
    }
    private void Fail(Exception ex) => Notify(Plain.Message(ex.Message), true);
    private void BannerClose_Click(object sender, RoutedEventArgs e) => BannerHost.Visibility = Visibility.Collapsed;
    private void SetBusy(bool busy) { BusyBar.IsIndeterminate = busy; BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden; }
    private void ShowPauseState()
    {
        PauseButton.Content = Engine.Paused ? "Resume all" : "Pause all";
        PauseButton.Glyph = Engine.Paused ? "\uE102" : "\uE103";
    }

    private void ShowPage(int index)
    {
        if (Pages.SelectedIndex != index) Pages.SelectedIndex = index;
        (PageTitle.Text, PageHint.Text) = PageInfo[index];
        if (index == 3 && activityHint != null) PageHint.Text = activityHint;
        if (index == 0 && !hasFolders) PageHint.Text = "Add a folder to see how much space you could save.";
        GlyphButton[] buttons = [NavOverview, NavFolders, NavInsights, NavActivity];
        for (var i = 0; i < buttons.Length; i++) buttons[i].IsSelected = i == index;
        // Activity lists every folder, so the folder picker would do nothing there.
        // The Managed folders list is itself the folder chooser, and Activity covers every folder.
        FolderPickerPanel.Visibility = index != 2 || !hasFolders || RootPicker.Items.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        var page = int.Parse((string)((FrameworkElement)sender).Tag);
        if (page != Pages.SelectedIndex) BannerHost.Visibility = Visibility.Collapsed;
        ShowPage(page);
    }
    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Lists and drop-downs inside the pages raise the same event; only react to the page itself changing.
        if (ReferenceEquals(e.OriginalSource, Pages) && Pages.SelectedIndex >= 0) ShowPage(Pages.SelectedIndex);
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
    private async void Root_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing) return;
        drill = Selected?.Path; largest = false; await RefreshAsync();
    }
    private bool SameRoots(IReadOnlyList<ManagedFolder> roots) =>
        ChartRootBox.ItemsSource is IEnumerable<ManagedFolder> current && current.Select(r => r.Id).SequenceEqual(roots.Select(r => r.Id));
    private void ChartRoot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing || ChartRootBox.SelectedItem is not ManagedFolder root) return;
        RootPicker.SelectedItem = RootPicker.Items.Cast<ManagedFolder>().FirstOrDefault(r => r.Id == root.Id);
    }
    private async void QuickAdd_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var special = (string)((FrameworkElement)sender).Tag switch
        {
            "Downloads" => (string?)null, "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), _ => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        } ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!System.IO.Directory.Exists(special)) throw new InvalidOperationException("That folder doesn't exist on this PC. Use Add folder to pick another one.");
        var root = await Engine.AddAsync(special); selectNext = root.Id;
        Notify("Folder added. ZipXP is scanning it now and hasn't changed anything. To start saving space, open Folder settings and turn on automatic compression.");
    });
    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row) return;
        try
        {
            if (System.IO.File.Exists(row.Path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
            else if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(row.Path))) System.Diagnostics.Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(row.Path)}\"");
            else Notify("That item is no longer there.");
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void StartupHint_Click(object sender, RoutedEventArgs e) { StartupBox.IsChecked = true; Startup_Click(StartupBox, e); }
    private void ScopeLink_Click(object sender, RoutedEventArgs e) => ShowPage(1);
    private async void Period_Changed(object sender, SelectionChangedEventArgs e) { if (ready) await RefreshAsync(); }
    private void FolderGrid_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing || FoldersGrid.SelectedItem is not FolderRow row) return;
        RootPicker.SelectedItem = RootPicker.Items.Cast<ManagedFolder>().FirstOrDefault(r => r.Id == row.Root.Id);
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder on this PC for ZipXP to look after" };
        if (dialog.ShowDialog(this) != true) return;
        var root = await Engine.AddAsync(dialog.FolderName); selectNext = root.Id;
        Notify("Folder added. ZipXP is scanning it now and hasn't changed anything. To start saving space, open Folder settings and turn on automatic compression.");
    });
    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        try { Engine.RequestScan(RequireRoot().Id); Notify("Scan started. Scanning only looks at your files; it never changes them."); }
        catch (Exception ex) { Fail(ex); }
    }
    private async void Policy_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var defaults = Engine.Store.Scans(root.Id).FirstOrDefault()?.NativeDefaults ?? 0;
        var editor = new PolicyWindow(root, defaults) { Owner = this };
        if (editor.ShowDialog() != true) return;
        await Engine.SavePolicyAsync(root, editor.Policy);
        Notify(editor.Policy.Enabled ? "Automatic compression is on for this folder. Files worth compressing, now and in future, are handled in the background."
            + (StartupBox.IsChecked == true ? "" : " It only works while ZipXP is running, so consider turning on \"Start when I sign in\" at the bottom left.")
            : "Settings saved. Automatic compression is off for this folder. Files that are already compressed stay compressed.");
    });
    private async void FolderPause_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); await Engine.SetFolderPausedAsync(root.Id, !root.Policy.Paused);
        Notify(root.Policy.Paused ? "Folder resumed." : "Folder paused. If a file is in progress, it will finish first.");
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); await Engine.RemoveAsync(root.Id); drill = null;
        Notify("ZipXP no longer manages that folder. Its files are exactly as they were.");
    });
    private async void ScopeJob_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); var operation = (string)((FrameworkElement)sender).Tag;
        Notify("Checking the folder…");
        var preview = await Engine.PreviewAsync(root, operation);
        var builtIn = preview.NativeDefaults > 0 ? $"{Plain.BuiltIn} is switched on in {Plain.Count(preview.NativeDefaults, "folder")} here." : null;
        var (title, summary, explanation) = operation switch
        {
            "Decompress" => ("Undo compression preview", $"{Plain.Count(preview.Files, "compressed file")} to decompress · {Display.Bytes(preview.LogicalBytes)} in total",
                "Automatic compression for this folder will be turned off first, so the files aren't compressed again." + (builtIn == null ? "" : $"\n\n{builtIn} Windows may still compress new files there until you turn that off from the More menu.")),
            "ClearDefault" => ("Turn off " + Plain.BuiltIn, $"{Plain.Count(preview.Files, "folder")} to update",
                "Windows will stop compressing new files in these folders by itself. Files that are already compressed stay compressed."),
            _ => ("Compress preview", $"{Plain.Count(preview.Files, "file")} worth compressing · {Display.Bytes(preview.LogicalBytes)} in total",
                "They will be compressed once, in the background. This does not turn on automatic compression." + (builtIn == null ? "" : "\n\n" + builtIn))
        };
        var coverage = preview.Coverage == "Complete primary-stream scan" ? "Everything in the folder was checked." : Plain.Message(preview.Coverage) + ", so those are left out.";
        var text = $"{root.Path}\n\n{summary}\nNeeds at least {Display.Bytes(preview.RequiredHeadroom)} of free space on the drive while it works.\n{coverage}\n\n{explanation}\n\nGo ahead?";
        if (MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) { BannerHost.Visibility = Visibility.Collapsed; return; }
        var count = await Engine.QueueScopeAsync(root.Id, operation);
        Notify($"{Plain.Count(count, "item")} added to the queue." + (Engine.Paused ? " ZipXP is paused, so choose Resume all to start." : " See Activity for progress."));
    });
    /// <summary>Overview: compress everything worth compressing in every managed folder, after one confirmation.</summary>
    private async void CompressAll_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var roots = Engine.Store.Roots().Where(r => !r.Policy.Paused).ToArray();
        if (roots.Length == 0) throw new InvalidOperationException("Add a folder first.");
        Notify("Checking your folders…");
        long files = 0, bytes = 0, headroom = 0; var complete = true; var targets = new List<ManagedFolder>();
        foreach (var root in roots)
        {
            var preview = await Engine.PreviewAsync(root, "Compress");
            if (preview.Files == 0) continue;
            targets.Add(root); files += preview.Files; bytes += preview.LogicalBytes; headroom = Math.Max(headroom, preview.RequiredHeadroom);
            complete &= preview.Coverage == "Complete primary-stream scan";
        }
        if (targets.Count == 0) { Notify("Nothing is worth compressing right now."); return; }
        var text = $"{Plain.Count(files, "file")} worth compressing in {Plain.Count(targets.Count, "folder")} · {Display.Bytes(bytes)} in total\n"
            + $"Needs at least {Display.Bytes(headroom)} of free space on the drive while it works.\n"
            + (complete ? "Everything in those folders was checked.\n" : "Items that couldn't be read are left out.\n")
            + "\nThey will be compressed once, in the background, and you can undo it later from Managed folders > Undo compression. This does not turn on automatic compression.\n\nGo ahead?";
        if (MessageBox.Show(this, text, "Compress preview", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) { BannerHost.Visibility = Visibility.Collapsed; return; }
        var queued = 0;
        foreach (var root in targets) queued += await Engine.QueueScopeAsync(root.Id, "Compress");
        Notify($"{Plain.Count(queued, "item")} added to the queue." + (Engine.Paused ? " ZipXP is paused, so choose Resume all to start." : " See Activity for progress."));
    });
    private async void SelectedDecompress_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var selected = InsightsGrid.SelectedItems.Cast<InsightRow>().Select(r => r.Source).Where(o => o.File is { Directory: false, Compressed: true }).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select one or more compressed files in the list first.");
        var space = root.Policy.ReserveBytes + selected.Max(o => o.File!.Logical);
        if (MessageBox.Show(this, $"Decompress {Plain.Count(selected.Length, "selected file")}?\n\nNeeds at least {Display.Bytes(space)} of free space on the drive.\n\nAutomatic compression for this folder will be turned off, so the files aren't compressed again.", "Undo compression for selected files", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var count = await Engine.QueueSelectedAsync(root.Id, selected);
        Notify($"{Plain.Count(count, "file")} queued to decompress. Automatic compression is now off for this folder.");
    });
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try { await Engine.SetPausedAsync(!Engine.Paused); ShowPauseState(); }
        catch (Exception ex) { Fail(ex); }
        finally { if (!App.Instance.Exiting) PauseButton.IsEnabled = true; }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { StartupRegistration.Enabled = StartupBox.IsChecked == true; StartupHint.Visibility = Visibility.Collapsed; Notify(StartupBox.IsChecked == true ? "ZipXP will now start quietly in the background each time you sign in to Windows." : "ZipXP will no longer start when you sign in."); }
        catch (Exception ex) { Fail(ex); StartupBox.IsChecked = false; }
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await App.Instance.ExitAsync();
    public void ShowClosingStatus() { timer.Stop(); FolderActions.IsEnabled = false; PauseButton.IsEnabled = false; Notify("Finishing the file in progress, then quitting. Anything still waiting carries on next time you open ZipXP."); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.Instance.Exiting) return;
        e.Cancel = true; Hide(); App.Instance.ExplainTray();
    }

    private void RenderInsights()
    {
        if (!ready) return;
        var search = InsightSearch.Text.Trim();
        var entries = observations.Where(o => largest ? o.File?.Directory == false : string.Equals(System.IO.Path.GetDirectoryName(o.Path), drill, StringComparison.OrdinalIgnoreCase));
        List<InsightRow> rows = [];
        foreach (var entry in entries)
        {
            var reason = entry.Reason == "Already compressed" ? (entry.File?.Directory == false ? "Compressed" : "") : Plain.Message(entry.Reason);
            if (search.Length > 0 && !(entry.Path + " " + entry.Reason + " " + reason).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.File?.Directory == true)
            {
                var total = folderTotals.GetValueOrDefault(entry.Path);
                var unread = total.Unknown > 0 ? $"{Plain.Count(total.Unknown, "item")} inside couldn't be measured" : "";
                rows.Add(new(entry, total.Logical, total.Unknown > 0 ? null : total.Allocated, string.Join(" · ", new[] { reason, unread }.Where(part => part.Length > 0))));
            }
            else rows.Add(new(entry, entry.File?.Logical, entry.File?.Allocated, reason + (entry.Candidate && entry.EstimatedSaving.HasValue ? $" · could save about {entry.EstimateDisplay}" : "")));
        }
        var ordered = rows.OrderByDescending(r => r.AllocatedBytes ?? -1).Take(500).ToArray();
        // Each row's share of what's listed, so the biggest space users stand out without reading numbers.
        var listed = Math.Max(1.0, ordered.Sum(r => (double)(r.AllocatedBytes ?? r.LogicalBytes ?? 0)));
        var shown = ordered.Select(r => r with { Share = (r.AllocatedBytes ?? r.LogicalBytes ?? 0) / listed }).ToArray();
        InsightsGrid.ItemsSource = shown;
        DecompressSelectedButton.IsEnabled = false;
        NotesColumn.Visibility = shown.Any(r => r.Notes.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        // One joined switch: the chosen view gets a soft fill and bold text, so solid blue stays reserved for main actions.
        foreach (var (button, on) in new[] { (LargestButton, largest), (ByFolderButton, !largest) })
        {
            if (on) button.SetResourceReference(Control.BackgroundProperty, "ControlFillColorSecondaryBrush"); else button.Background = Brushes.Transparent;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        InsightsFooter.Text = largest ? "Showing the biggest files anywhere inside this folder. Choose By folder to browse folder by folder."
            : shown.Any(r => r.Source.File?.Directory == true) ? "Double-click a folder to open it, or use the path above to jump back. The biggest items are listed first."
            : "Select compressed files, then choose Undo for selected to put them back to normal.";
        BuildCrumbs();
    }
    private static IReadOnlyDictionary<string, (long Logical, long Allocated, int Unknown)> BuildFolderTotals(IReadOnlyList<Observation> entries, string? root)
    {
        Dictionary<string, (long Logical, long Allocated, int Unknown)> totals = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> links = new(StringComparer.OrdinalIgnoreCase);
        if (root == null) return totals;
        foreach (var entry in entries.Where(e => e.File?.Directory != true))
        {
            for (var parent = System.IO.Path.GetDirectoryName(entry.Path); parent != null && Scope.Within(parent, root); parent = System.IO.Path.GetDirectoryName(parent))
            {
                if (entry.File is { Links: > 1 } linked)
                {
                    if (!links.TryGetValue(parent, out var identities)) links[parent] = identities = [];
                    if (!identities.Add(linked.Identity)) continue;
                }
                var old = totals.GetValueOrDefault(parent);
                totals[parent] = (old.Logical + (entry.File?.Logical ?? 0), old.Allocated + (entry.File?.Allocated ?? 0), old.Unknown + (entry.File?.Allocated == null ? 1 : 0));
            }
        }
        return totals;
    }
    private void InsightSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        InsightSearchHint.Visibility = InsightSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderInsights();
    }
    private void Largest_Click(object sender, RoutedEventArgs e) { largest = true; RenderInsights(); }
    private void ByFolder_Click(object sender, RoutedEventArgs e) { largest = false; drill ??= Selected?.Path; RenderInsights(); }
    /// <summary>The path above the list, as clickable pieces from the managed folder down to where you are.</summary>
    private void BuildCrumbs()
    {
        DrillCrumbs.Children.Clear();
        if (Selected == null || drill == null || largest)
        {
            DrillCrumbs.Visibility = Visibility.Collapsed; DrillPath.Visibility = Visibility.Visible;
            DrillPath.Text = largest && Selected != null ? $"The biggest files anywhere in {Selected.Name}" : "Add a folder first to see what's inside.";
            return;
        }
        DrillPath.Visibility = Visibility.Collapsed; DrillCrumbs.Visibility = Visibility.Visible;
        var parts = new List<(string Label, string Path)> { (Selected.Name, Selected.Path) };
        var relative = System.IO.Path.GetRelativePath(Selected.Path, drill);
        var current = Selected.Path;
        if (relative != ".") foreach (var piece in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries)) { current = System.IO.Path.Combine(current, piece); parts.Add((piece, current)); }
        for (var i = 0; i < parts.Count; i++)
        {
            var (label, path) = parts[i]; var last = i == parts.Count - 1;
            if (i > 0) DrillCrumbs.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") });
            if (last)
            {
                var here = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = path };
                here.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                DrillCrumbs.Children.Add(here); continue;
            }
            var crumb = new Button { Content = label, Padding = new Thickness(6, 2, 6, 3), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(crumb, "Open " + label);
            crumb.Click += (_, _) => { drill = path; largest = false; RenderInsights(); };
            DrillCrumbs.Children.Add(crumb);
        }
    }
    private void Insight_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow) element = VisualTreeHelper.GetParent(element);
        if (element is DataGridRow { Item: InsightRow { Source.File.Directory: true } row }) { drill = row.Source.Path; largest = false; RenderInsights(); }
    }
    private void RenderActivity()
    {
        if (!ready) return;
        var filter = (StateFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        var search = ActivitySearch.Text.Trim();
        var selected = JobsGrid.SelectedItems.Cast<JobRow>().Select(j => j.Source.Id).ToHashSet();
        var visible = jobs.Where(j => filter == "All" || (filter == "Pending" ? j.Pending : j.State == filter)).Select(j => new JobRow(j))
            .Where(j => search.Length == 0 || string.Join(" ", j.Path, j.Source.Message, j.Details, j.Source.Operation, j.Action, j.State).Contains(search, StringComparison.OrdinalIgnoreCase)).Take(1000).ToArray();
        JobsGrid.ItemsSource = visible;
        foreach (var item in visible.Where(j => selected.Contains(j.Source.Id))) JobsGrid.SelectedItems.Add(item);
        DetailsColumn.Visibility = visible.Any(j => j.Details.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        UpdateRetry(); UpdateRetry2();
        RetryButton.Visibility = jobs.Any(j => j.State is "Failed" or "Skipped") ? Visibility.Visible : Visibility.Collapsed;
        var todayDone = jobs.Where(j => j.State == "Succeeded" && (j.FinishedUtc ?? j.CreatedUtc).ToLocalTime().Date == DateTime.Now.Date).ToArray();
        var compressedToday = todayDone.Count(j => j.Operation == "Compress"); var savedToday = todayDone.Where(j => j.Operation == "Compress").Sum(j => j.Delta ?? 0);
        var failedNow = jobs.Count(j => j.State == "Failed");
        activityHint = jobs.Count == 0 ? null : $"Today: {(compressedToday == 0 ? "nothing compressed yet" : $"{Plain.Count(compressedToday, "file")} compressed" + (savedToday > 0 ? $", {Display.Bytes(savedToday)} saved" : ""))}" + (failedNow > 0 ? $" · {Plain.Count(failedNow, "file")} couldn't be handled" : "") + ".";
        if (Pages.SelectedIndex == 3) PageHint.Text = activityHint ?? PageInfo[3].Hint;
        EmptyJobs.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        JobsGrid.Visibility = visible.Length == 0 ? Visibility.Hidden : Visibility.Visible;
        (EmptyJobsTitle.Text, EmptyJobsHint.Text) = jobs.Count == 0
            ? ("Nothing here yet", "Every file ZipXP compresses or decompresses is listed here, along with anything it had to skip.")
            : ("Nothing matches", "Try a different search, or choose Everything in the Show list.");
    }
    private void InsightsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        DecompressSelectedButton.IsEnabled = InsightsGrid.SelectedItems.Cast<InsightRow>().Any(r => r.Source.File is { Directory: false, Compressed: true });
        DecompressSelectedButton.ToolTip = DecompressSelectedButton.IsEnabled ? "Decompress the selected files, putting them back to normal." : "Select one or more compressed files first.";
    }
    private void UpdateRetry() => ShowFolderButton.Visibility = JobsGrid.SelectedItems.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
    private void UpdateRetry2() => RetryButton.IsEnabled = JobsGrid.SelectedItems.Cast<JobRow>().Any(j => j.Source.State is "Failed" or "Skipped");
    private void ActivityFilter_Changed(object sender, SelectionChangedEventArgs e) { if (ready) RenderActivity(); }
    private void ActivitySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        ActivitySearchHint.Visibility = ActivitySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderActivity();
    }
    private void Job_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateRetry(); UpdateRetry2();
        var any = JobsGrid.SelectedItem is JobRow;
        JobDetails.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        JobDetailsLabel.Visibility = Visibility.Visible;
        JobDetailsLabel.Text = any ? "Details for this item. Copy them if you need help." : "Select an item to see its details or show it in File Explorer. To undo compression, use Storage insights.";
        if (JobsGrid.SelectedItem is not JobRow row) return;
        var job = row.Source;
        JobDetails.Text = $"{row.Action} · {row.State} · tried {Plain.Count(job.Attempts, "time")}\n{job.Path}\n{row.Details}\nSpace saved: {row.Saved}\n\nTechnical details: {job.Operation} / {job.State} · {job.Message} · file ID {job.Expected.Identity}";
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var count = await Engine.RetryAsync(JobsGrid.SelectedItems.Cast<JobRow>().Select(j => j.Source).ToArray());
        Notify(count > 0 ? $"{Plain.Count(count, "file")} queued to try again."
            : "Nothing was queued. Select failed or skipped items first. Files that have changed since are picked up by the next scan instead.");
    });

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) { if (ready) RenderChart(); }
    private void RenderChart()
    {
        HistoryChart.Children.Clear();
        var folderName = Selected == null ? "" : System.IO.Path.GetFileName(Selected.Path);
        HistoryCaption.Text = Selected == null ? "Add a folder to start tracking how much space it uses."
            : history.Count == 0 ? $"No scans of {folderName} in this period yet."
            : $"Space used by {folderName} after each scan. The dashed line is its original size." + (history.Any(s => s.Unknown > 0) ? " Orange bars are scans where some items couldn't be read." : "");
        HistoryChart.Visibility = history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryRange.Visibility = Selected == null ? Visibility.Collapsed : Visibility.Visible;
        if (history.Count == 0) { HistoryRange.Text = "No scans in this period yet."; return; }
        const double axis = 22, top = 22, chartHeight = 104;
        var width = Math.Max(100, HistoryChart.ActualWidth);
        var original = history[^1].Logical;
        var maximum = Math.Max(1, Math.Max(history.Max(s => s.Allocated), original));
        var plot = chartHeight - axis - top;
        var baseline = new Rectangle { Width = width, Height = 1 };
        baseline.SetResourceReference(Shape.FillProperty, "ControlStrongFillColorDefaultBrush");
        Canvas.SetBottom(baseline, axis); HistoryChart.Children.Add(baseline);
        var slot = width / history.Count;
        var barWidth = Math.Clamp(slot * 0.6, 2, 28);
        // Reference line: what the folder would take up uncompressed.
        var referenceY = plot * original / maximum;
        var dash = new Line { X1 = 0, X2 = width, StrokeThickness = 1, StrokeDashArray = [4, 4] };
        dash.SetResourceReference(Shape.StrokeProperty, "TextFillColorTertiaryBrush");
        Canvas.SetBottom(dash, axis + referenceY); HistoryChart.Children.Add(dash);
        var originalLabel = new TextBlock { Text = $"Original size {Display.Bytes(original)} · now {Display.Bytes(history[^1].Allocated)}", FontSize = 12 };
        originalLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Canvas.SetRight(originalLabel, 0); Canvas.SetBottom(originalLabel, axis + referenceY + 3); HistoryChart.Children.Add(originalLabel);
        var index = 0;
        foreach (var scan in history)
        {
            var height = Math.Max(2, plot * scan.Allocated / maximum);
            var complete = scan.Unknown == 0;
            var description = $"{scan.Utc.ToLocalTime():g}: {scan.AllocationDisplay} used" + (complete ? "" : ". " + Plain.Message(scan.Coverage));
            var bar = new Rectangle { Width = barWidth, Height = height, RadiusX = 2, RadiusY = 2, ToolTip = description.Replace(": ", "\n") };
            System.Windows.Automation.AutomationProperties.SetName(bar, description);
            bar.SetResourceReference(Shape.FillProperty, complete ? "AccentFillColorDefaultBrush" : "SystemFillColorCautionBrush");
            // One evenly spaced slot per scan, so bars don't bunch up when scans are close together in time.
            Canvas.SetLeft(bar, slot * index + (slot - barWidth) / 2); Canvas.SetBottom(bar, axis); HistoryChart.Children.Add(bar);
            index++;
        }
        // Dates under the first and last bars, so the time period is visible on the chart itself.
        static string Day(DateTime utc) { var l = utc.ToLocalTime(); return l.Date == DateTime.Now.Date ? "Today" : l.Date == DateTime.Now.Date.AddDays(-1) ? "Yesterday" : l.ToString("d MMM"); }
        var sameDay = history[0].Utc.ToLocalTime().Date == history[^1].Utc.ToLocalTime().Date;
        var startText = new TextBlock { Text = sameDay ? Plain.When(history[0].Utc) : Day(history[0].Utc), FontSize = 12 };
        var endText = new TextBlock { Text = sameDay ? history[^1].Utc.ToLocalTime().ToString("t") : Day(history[^1].Utc), FontSize = 12 };
        startText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        endText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Canvas.SetLeft(startText, 0); Canvas.SetBottom(startText, 0); Canvas.SetRight(endText, 0); Canvas.SetBottom(endText, 0);
        HistoryChart.Children.Add(startText); if (history.Count > 1) HistoryChart.Children.Add(endText);
        HistoryRange.Text = history.Count == 1 ? $"One scan so far ({Plain.When(history[0].Utc)})."
            : $"{Plain.Count(history.Count, "scan")} in this period.";
    }
}
