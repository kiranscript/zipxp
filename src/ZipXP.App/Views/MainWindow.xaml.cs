using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using ZipXP.Core;
using ZipXP.Core.Services;

namespace ZipXP.App;

public partial class MainWindow : Window
{
    private Engine Engine => App.Instance.Engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool ready, refreshing, refreshAgain, syncing, commandBusy, hasFolders;
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
        ("Overview", "Check possible savings, ZipXP’s results, and available drive space."),
        ("Managed folders", "Select a folder to choose how ZipXP handles it."),
        ("Storage insights", "See what is taking up space inside a folder."),
        ("Activity", "Everything ZipXP has done, and anything that needs your attention.")
    ];

    private sealed record FolderRow(ManagedFolder Root, ScanSummary? Scan, string WorkerStatus, bool CanRestore)
    {
        public string Path => Root.Path;
        public string Name => Root.Name;
        public string Parent => Root.Parent;
        public string ShortPath => Plain.MiddleTrim(Root.Path, 38);
        public string Mode => !Root.Policy.Enabled ? "Off" : Root.Policy.Paused ? "On · paused" : "On";
        public string Tone => !Root.Policy.Enabled ? "Neutral" : Root.Policy.Paused ? "Warn" : "Good";
        public string OnDisk => Display.Bytes(Scan?.Allocated);
        public string Estimate => Scan == null ? "—" : Scan.Candidates == 0 ? "None found" : Display.Bytes(Scan.EstimatedSaving);
        public string Status => Plain.Message(WorkerStatus) + (Scan is { NativeDefaults: > 0 } ? " · Windows compression on" : "");
    }
    private sealed record InsightRow(Observation Source, long? LogicalBytes, long? AllocatedBytes, string Notes)
    {
        public double Share { get; init; }
        public string SharePercent => Share < 0.005 ? "<1%" : $"{Math.Round(Share * 100):0}%";
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
            ? Source.Operation switch { "Compress" => "Compressed", "Decompress" => "Restored original size", "ClearDefault" => "Turned off Windows compression", _ => Plain.Action(Source.Operation) }
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
        if (!ready || !IsVisible || App.Instance.Exiting) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            var requestedId = selectNext ?? Selected?.Id;
            var since = PeriodStart;
            var snapshot = await Task.Run(() =>
            {
                var roots = Engine.Store.Roots();
                var selectedId = requestedId ?? roots.FirstOrDefault()?.Id;
                var allEntries = roots.ToDictionary(r => r.Id, r => Engine.Store.Observations(r.Id));
                var scans = Engine.Store.Scans();
                var latest = roots.Select(root => new FolderRow(root, scans.FirstOrDefault(s => s.RootId == root.Id), Engine.FolderStatus(root.Id),
                    Engine.RestorableFiles(root, allEntries[root.Id]).Any())).ToArray();
                var entries = selectedId == null ? Array.Empty<Observation>() : allEntries.GetValueOrDefault(selectedId, []);
                var measuredFiles = allEntries.Values.SelectMany(e => e).Where(e => e.File is { Directory: false, Allocated: not null })
                    .Select(e => e.File!).DistinctBy(f => f.Identity).ToArray();
                var totals = BuildFolderTotals(entries, roots.FirstOrDefault(r => r.Id == selectedId)?.Path);
                var recentScans = selectedId == null ? [] : Engine.Store.Scans(selectedId).Where(s => s.Complete && s.Unknown == 0).Take(32).Reverse().ToArray();
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
                return (Roots: roots, SelectedId: selectedId, Rows: latest, Entries: entries, Totals: totals,
                    Allocation: measuredFiles.Sum(f => f.Allocated!.Value), FileSize: measuredFiles.Sum(f => f.Logical), MeasuredFiles: measuredFiles.Length,
                    Jobs: Engine.Store.Jobs(), History: recentScans, Volumes: volumes, Measured: Engine.Store.MeasuredSince(since));
            });
            if (App.Instance.Exiting || requestedId != (selectNext ?? Selected?.Id) || since != PeriodStart) return;
            var selectedId = snapshot.SelectedId;
            syncing = true;
            RootPicker.ItemsSource = snapshot.Roots;
            RootPicker.SelectedItem = snapshot.Roots.FirstOrDefault(r => r.Id == selectedId);
            if (!ReferenceEquals(ChartRootBox.ItemsSource, snapshot.Roots) && !SameRoots(snapshot.Roots)) ChartRootBox.ItemsSource = snapshot.Roots;
            ChartRootBox.SelectedValue = selectedId;
            ChartRootBox.Visibility = snapshot.Roots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            FoldersGrid.ItemsSource = snapshot.Rows;
            FoldersGrid.SelectedItem = snapshot.Rows.FirstOrDefault(r => r.Root.Id == selectedId);
            syncing = false; selectNext = null;
            observations = snapshot.Entries; folderTotals = snapshot.Totals; jobs = snapshot.Jobs;
            history = snapshot.History;
            var known = snapshot.Rows.Where(r => r.Scan != null).ToArray();
            var candidateFolders = known.Where(r => r.Scan!.Candidates > 0).ToArray();
            var estimate = known.Sum(r => r.Scan!.EstimatedSaving);
            var partial = known.Any(r => r.Scan is { Complete: false } or { Unknown: > 0 });
            EstimateCard.Text = known.Length == 0 ? "Waiting for scan" : estimate == 0 ? "None found yet" : Display.Bytes(estimate);
            EstimateCaption.Text = estimate > 0 ? "Based on the latest scans. Actual savings may differ."
                : known.Length > 0 ? "No additional savings found in the files checked so far."
                : "An estimate appears after ZipXP scans a folder.";
            OverviewCompressButton.Visibility = candidateFolders.Any(r => !r.Root.Policy.Paused) ? Visibility.Visible : Visibility.Collapsed;
            OverviewRestoreButton.Visibility = snapshot.Rows.Any(r => r.CanRestore) ? Visibility.Visible : Visibility.Collapsed;

            var sizeUnavailable = snapshot.MeasuredFiles == 0 && partial;
            AllocatedCard.Text = known.Length == 0 ? "Waiting for scan" : sizeUnavailable ? "Size unavailable" : $"{Display.Bytes(snapshot.Allocation)} on disk";
            OriginalCard.Text = known.Length == 0 || sizeUnavailable ? "" : $"{Display.Bytes(snapshot.FileSize)} in file sizes";
            ScopeCard.Text = partial ? "Some items were skipped or could not be measured."
                : known.Length < snapshot.Roots.Count ? $"{known.Length} of {Plain.Count(snapshot.Roots.Count, "folder")} scanned."
                : "From files measured within your folder settings.";
            var saved = snapshot.Measured.Delta;
            MeasuredCard.Text = snapshot.Measured.Measured == 0 ? "None recorded"
                : saved < 0 ? $"{Display.Bytes(-saved)} more used" : saved == 0 ? "No net change" : Display.Bytes(saved);
            MeasuredCaption.Text = snapshot.Measured.Measured == 0 ? "ZipXP has no measured changes in this period."
                : $"Measured after {Plain.Count(snapshot.Measured.Measured, "file operation")}, including restores.";
            VolumesList.ItemsSource = snapshot.Volumes;
            VolumesEmpty.Visibility = snapshot.Volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int pending = jobs.Count(j => j.Pending), failed = jobs.Count(j => j.State == "Failed");
            NavActivity.Content = failed > 0 ? $"Activity · {failed} failed" : "Activity";
            var lastDone = jobs.Where(j => j.State == "Succeeded" && j.FinishedUtc != null).OrderByDescending(j => j.FinishedUtc).FirstOrDefault();
            var lastText = lastDone == null ? "No completed operations yet." : $"{Plain.When(lastDone.FinishedUtc!.Value)} · {(lastDone.Operation switch { "Compress" => "compressed", "Decompress" => "restored", _ => "updated" })} {System.IO.Path.GetFileName(lastDone.Path)}";
            ActivityCard.Text = Engine.Paused ? "Paused" : pending == 0 ? "Nothing waiting" : Plain.Count(pending, "file") + " waiting";
            var autoOn = snapshot.Roots.Any(r => r.Policy.Enabled);
            WatchNote.Text = Engine.Paused ? $"{Plain.Count(pending, "file")} waiting. Choose Resume all to continue."
                : !autoOn ? "Auto-compression is off for all folders." : pending == 0 ? "Auto-compression is on for at least one folder." : "";
            StartupHint.Visibility = autoOn && StartupBox.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
            LastActionCard.Text = lastText; LastActionCard.ToolTip = lastText;
            WorkerText.Text = Engine.CurrentFile == null ? Plain.Message(Engine.Status) : $"Working on {Engine.CurrentFile} · {pending} waiting";
            SetBusy(Engine.CurrentFile != null || commandBusy);
            ShowPauseState();
            hasFolders = snapshot.Roots.Count > 0;
            ShowPage(Math.Max(0, Pages.SelectedIndex));
            EmptyFolders.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
            FoldersGrid.Visibility = hasFolders ? Visibility.Visible : Visibility.Hidden;
            UpdateFolderCard(FoldersGrid.SelectedItem as FolderRow);
            WelcomeCard.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
            OverviewBody.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            WorkerText.Visibility = hasFolders && (Pages.SelectedIndex != 0 || Engine.CurrentFile != null || Engine.Paused) ? Visibility.Visible : Visibility.Hidden;
            PauseButton.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            if (drill == null || Selected == null || !Scope.Within(drill, Selected.Path)) drill = Selected?.Path;
            RenderInsights(); RenderActivity(); RenderChart();
        }
        catch (Exception ex) { Notify("Couldn't update this window: " + Plain.Message(ex.Message), true); }
        finally
        {
            syncing = false;
            refreshing = false;
            if (refreshAgain)
            {
                refreshAgain = false;
                _ = RefreshAsync();
            }
        }
    }

    private void UpdateFolderCard(FolderRow? row)
    {
        FolderCard.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        if (row == null) return;

        var root = row.Root;
        SelectedFolderTitle.Text = root.Name;
        SelectedFolderTitle.ToolTip = root.Path;
        var state = root.Policy.Enabled ? "Auto-compression is on." : "Auto-compression is off.";
        SelectedFolderState.Text = state + (root.Policy.Paused ? " This folder is paused. Use More → Resume this folder."
            : Engine.Paused ? " ZipXP is paused. Choose Resume all to continue."
            : root.Policy.Enabled ? " ZipXP checks existing, new and changed files." : " Turn it on for existing and future files, or compress once.");
        if (row.Scan is { NativeDefaults: > 0 })
            SelectedFolderState.Text += " Windows also compresses new files in some of these folders.";

        FoldersNote.Text = row.WorkerStatus.StartsWith("Scan failed:", StringComparison.Ordinal) ? Plain.Message(row.WorkerStatus)
            : row.Scan == null ? "A savings estimate will appear after the first scan. Scanning leaves your files as they are."
            : row.Scan.Candidates > 0 ? $"Estimated savings of {row.Estimate} based on the latest scan."
            : "No additional savings found in the files checked so far.";
        FolderAutomationButton.Content = root.Policy.Enabled ? "Turn off auto-compression…" : "Turn on auto-compression…";
        FolderAutomationButton.SetResourceReference(StyleProperty, root.Policy.Enabled ? "CommandButton" : "PrimaryButton");
        FolderPauseItem.Header = root.Policy.Paused ? "Resume this folder" : "Pause this folder";
        FolderCompressButton.IsEnabled = !root.Policy.Paused && row.Scan is { Candidates: > 0 };
        FolderCompressButton.ToolTip = root.Policy.Paused ? "Resume this folder before compressing it."
            : row.Scan == null ? "Available after ZipXP scans this folder."
            : row.Scan.Candidates == 0 ? "The latest scan found no files to compress right now."
            : "Preview one-time compression of eligible files in this folder.";
        FolderUndoButton.IsEnabled = row.CanRestore;
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
        await AddFolderAsync(special);
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
        UpdateFolderCard(row);
        RootPicker.SelectedItem = RootPicker.Items.Cast<ManagedFolder>().FirstOrDefault(r => r.Id == row.Root.Id);
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder on this PC for ZipXP to look after" };
        if (dialog.ShowDialog(this) != true) return;
        await AddFolderAsync(dialog.FolderName);
    });
    private async Task AddFolderAsync(string path)
    {
        var root = await Engine.AddAsync(path);
        selectNext = root.Id;
        drill = root.Path;
        largest = false;
        ShowPage(1);
        Notify($"Added {root.Name}. ZipXP will scan it to estimate savings.");
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        drill = Selected?.Path;
        largest = false;
        ShowPage(2);
        RenderInsights();
    }

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
        if (editor.ShowDialog() != true || editor.Policy == root.Policy) return;
        var queueNotice = PendingWorkNotice(root);
        if (queueNotice != "" && MessageBox.Show(this, "Save these settings?" + queueNotice,
                "Change folder settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SaveFolderPolicyAsync(root, editor.Policy);
    });

    private async void FolderAutomation_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var enable = !root.Policy.Enabled;
        var title = enable ? "Turn on auto-compression" : "Turn off auto-compression";
        var explanation = enable
            ? "ZipXP will compress eligible existing, new and changed files in the background while it is running. Your files will still open as usual."
            : "ZipXP will stop compressing files automatically in this folder. Already compressed files will stay compressed.";
        var text = $"{title} for {root.Name}?\n\n{explanation}" + PendingWorkNotice(root);
        if (MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SaveFolderPolicyAsync(root, root.Policy with { Enabled = enable });
    });

    private string PendingWorkNotice(ManagedFolder root) => Engine.Store.ActiveJobs().Any(j => j.RootId == root.Id)
        ? "\n\nThis will cancel any work still queued for this folder." : "";

    private async Task SaveFolderPolicyAsync(ManagedFolder root, FolderPolicy policy)
    {
        await Engine.SavePolicyAsync(root, policy);
        Notify(policy.Enabled
            ? $"Auto-compression is on for {root.Name}." + (root.Policy.Paused ? " Resume this folder from More to start." : Engine.Paused ? " Choose Resume all to start." : "")
            : $"Auto-compression is off for {root.Name}. Already compressed files stay compressed.");
    }

    private async void FolderPause_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); await Engine.SetFolderPausedAsync(root.Id, !root.Policy.Paused);
        Notify(root.Policy.Paused ? "Folder resumed." : "Folder paused. If a file is in progress, it will finish first.");
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var entries = Engine.Store.Observations(root.Id);
        var restore = Engine.RestorableFiles(root, entries).Any() ? "\n\nTo restore compressed files first, choose No and use Restore files." : "";
        var text = $"Stop managing {root.Name}?\n\nZipXP will remove this folder from its list and leave the files as they are." + restore;
        if (MessageBox.Show(this, text, "Stop managing this folder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Engine.RemoveAsync(root.Id); drill = null;
        Notify("ZipXP no longer manages that folder. Its files are exactly as they were.");
    });
    private async void ScopeJob_Click(object sender, RoutedEventArgs e) => await Command(() => RunScopeJob(RequireRoot(), (string)((FrameworkElement)sender).Tag));
    private async Task RunScopeJob(ManagedFolder root, string operation)
    {
        Notify("Checking the folder…");
        var preview = await Engine.PreviewAsync(root, operation);
        if (preview.Files == 0)
        {
            Notify(operation switch
            {
                "Decompress" => "ZipXP found no compressed files it can restore in this folder.",
                "ClearDefault" => "ZipXP found no folders with Windows compression to turn off.",
                _ => "ZipXP found no files it can compress right now."
            });
            return;
        }
        var builtIn = preview.NativeDefaults > 0 ? $"{Plain.BuiltIn} is switched on in {Plain.Count(preview.NativeDefaults, "folder")} here." : null;
        var (title, summary, explanation) = operation switch
        {
            "Decompress" => ("Restore original size", $"{Plain.Count(preview.Files, "compressed file")} to restore",
                "The files will take up more space and still open normally. ZipXP will turn off auto-compression for this folder." + (builtIn == null ? "" : $"\n\n{builtIn} Windows may still compress new files there until you turn that off from the More menu.")),
            "ClearDefault" => ("Turn off " + Plain.BuiltIn, $"{Plain.Count(preview.Files, "folder")} to update",
                "Windows will stop compressing new files in these folders by itself. Files that are already compressed stay compressed."),
            _ => ("Compress once", $"{Plain.Count(preview.Files, "file")} worth compressing · {Display.Bytes(preview.LogicalBytes)} in total",
                "ZipXP will compress these files once. Your auto-compression setting stays as it is." + (builtIn == null ? "" : "\n\n" + builtIn))
        };
        var coverage = preview.Coverage == "Complete primary-stream scan" ? "The scan covered the files within your folder settings." : "Some items were skipped or could not be checked.";
        var text = $"{root.Path}\n\n{summary}\nNeeds at least {Display.Bytes(preview.RequiredHeadroom)} of free space on the drive while it works.\n{coverage}\n\n{explanation}\n\nGo ahead?";
        if (MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) { BannerHost.Visibility = Visibility.Collapsed; return; }
        var count = await Engine.QueueScopeAsync(root.Id, operation);
        Notify($"{Plain.Count(count, "item")} added to the queue." + (Engine.Paused ? " ZipXP is paused, so choose Resume all to start." : " See Activity for progress."));
    }
    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var roots = Engine.Store.Roots().Where(r => Engine.RestorableFiles(r, Engine.Store.Observations(r.Id)).Any()).ToArray();
        Notify("Checking your folders…");
        long files = 0, headroom = 0;
        var targets = new List<ManagedFolder>();
        var windowsCompression = false;
        var complete = true;
        foreach (var root in roots)
        {
            var preview = await Engine.PreviewAsync(root, "Decompress");
            if (preview.Files == 0) continue;
            targets.Add(root);
            files += preview.Files;
            headroom = Math.Max(headroom, preview.RequiredHeadroom);
            windowsCompression |= preview.NativeDefaults > 0;
            complete &= preview.Coverage == "Complete primary-stream scan";
        }
        if (targets.Count == 0) { Notify("ZipXP found no compressed files it can restore."); return; }
        var text = $"Restore {Plain.Count(files, "compressed file")} in {Plain.Count(targets.Count, "folder")}?\n" +
            $"Needs at least {Display.Bytes(headroom)} of free space while it works.\n\n" +
            "The files will take up more space and still open normally. ZipXP will turn off auto-compression for those folders." +
            (complete ? "" : "\nSome items were skipped or could not be checked.") +
            (windowsCompression ? "\nWindows may still compress new files in folders where its own compression is on." : "");
        if (MessageBox.Show(this, text, "Restore original size", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            BannerHost.Visibility = Visibility.Collapsed;
            return;
        }
        var queued = 0;
        foreach (var root in targets) queued += await Engine.QueueScopeAsync(root.Id, "Decompress");
        Notify($"{Plain.Count(queued, "file")} queued to restore." + (Engine.Paused ? " Choose Resume all to start." : " See Activity for progress."));
    });
    /// <summary>Overview: compress everything worth compressing in every managed folder, after one confirmation.</summary>
    private async void CompressAll_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var allRoots = Engine.Store.Roots();
        var roots = allRoots.Where(r => !r.Policy.Paused).ToArray();
        if (roots.Length == 0) { Notify("Resume a folder from Managed folders before compressing it."); return; }
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
            + (complete ? "" : "Some items were skipped or could not be checked.\n")
            + (allRoots.Count == roots.Length ? "" : $"{Plain.Count(allRoots.Count - roots.Length, "paused folder")} left out.\n")
            + "\nYour auto-compression settings stay as they are. To undo, use Restore files in Managed folders.\n\nGo ahead?";
        if (MessageBox.Show(this, text, "Compress once", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) { BannerHost.Visibility = Visibility.Collapsed; return; }
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
        var text = $"Restore the original size of {Plain.Count(selected.Length, "selected file")}?\n\n" +
            $"The files will take up more space and still open normally. Needs at least {Display.Bytes(space)} of free space.\n\n" +
            "ZipXP will turn off auto-compression for this folder.";
        if (MessageBox.Show(this, text, "Restore selected files", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var count = await Engine.QueueSelectedAsync(root.Id, selected);
        Notify($"{Plain.Count(count, "file")} queued to restore. Automatic compression is now off for this folder.");
    });
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try { await Engine.SetPausedAsync(!Engine.Paused); ShowPauseState(); await RefreshAsync(); }
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
            : "Select compressed files, then choose Restore selected to restore their original size.";
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
            ? ("Nothing here yet", "Every file ZipXP compresses or restores is listed here, along with anything it had to skip.")
            : ("Nothing matches", "Try a different search, or choose Everything in the Show list.");
    }
    private void InsightsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        DecompressSelectedButton.IsEnabled = InsightsGrid.SelectedItems.Cast<InsightRow>().Any(r => r.Source.File is { Directory: false, Compressed: true });
        DecompressSelectedButton.ToolTip = DecompressSelectedButton.IsEnabled ? "Uncompress the selected files so they take their full, original space again. No files are deleted." : "Select one or more compressed files first.";
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
        JobDetailsLabel.Text = any ? "Details for this item. Copy them if you need help." : "Select an item to see its details or show it in File Explorer. To undo compression, use Restore files in Managed folders.";
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
            : history.Count == 0 ? $"No scans with full measurements for {folderName} yet."
            : $"Size on disk for {folderName} at each recent scan with full measurements.";
        HistoryChart.Visibility = history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryRange.Visibility = HistoryChart.Visibility;
        if (history.Count == 0) return;
        const double axis = 22, top = 22, chartHeight = 104;
        var width = Math.Max(100, HistoryChart.ActualWidth);
        var maximum = Math.Max(1, history.Max(s => s.Allocated));
        var plot = chartHeight - axis - top;
        var baseline = new Rectangle { Width = width, Height = 1 };
        baseline.SetResourceReference(Shape.FillProperty, "ControlStrongFillColorDefaultBrush");
        Canvas.SetBottom(baseline, axis); HistoryChart.Children.Add(baseline);
        var slot = width / history.Count;
        var barWidth = Math.Clamp(slot * 0.6, 2, 28);
        var index = 0;
        foreach (var scan in history)
        {
            var height = Math.Max(2, plot * scan.Allocated / maximum);
            var description = $"{scan.Utc.ToLocalTime():g}: {scan.AllocationDisplay} on disk";
            var bar = new Rectangle { Width = barWidth, Height = height, RadiusX = 2, RadiusY = 2, ToolTip = description.Replace(": ", "\n") };
            System.Windows.Automation.AutomationProperties.SetName(bar, description);
            bar.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
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
            : $"Showing the latest {Plain.Count(history.Count, "scan")}.";
    }
}
