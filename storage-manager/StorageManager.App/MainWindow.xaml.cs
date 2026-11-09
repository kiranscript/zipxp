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
    private bool ready, refreshing, syncing, commandBusy;
    private string? selectNext, drill;
    private bool largest;
    private IReadOnlyList<Observation> observations = [];
    private IReadOnlyList<WorkItem> jobs = [];
    private IReadOnlyList<ScanSummary> history = [];
    private IReadOnlyDictionary<string, (long Logical, long Allocated, int Unknown)> folderTotals = new Dictionary<string, (long, long, int)>();
    private ManagedFolder? Selected => RootPicker.SelectedItem as ManagedFolder;
    private DateTime PeriodStart => PeriodBox.SelectedIndex switch { 0 => DateTime.UtcNow.Date.AddDays(-6), 1 => DateTime.UtcNow.Date.AddDays(-29), _ => DateTime.MinValue };

    private sealed record FolderRow(ManagedFolder Root, ScanSummary? Scan, string WorkerStatus)
    {
        public string Path => Root.Path;
        public string Policy => Root.Policy.Paused ? "Paused" : Root.Policy.Enabled ? "Automatic" : "Analysis only";
        public string Logical => Display.Bytes(Scan?.Logical);
        public string Allocated => Display.Bytes(Scan?.Allocated);
        public string Candidates => Display.Bytes(Scan?.Candidates);
        public string Status => Scan == null ? WorkerStatus : $"{WorkerStatus} · {Scan.TimeDisplay} · {Scan.NativeDefaults} native defaults";
    }
    private sealed record InsightRow(Observation Source, long? LogicalBytes, long? AllocatedBytes, string Detail)
    {
        public string Name => Source.Name;
        public string Kind => Source.Kind;
        public string Logical => Display.Bytes(LogicalBytes);
        public string Allocated => Display.Bytes(AllocatedBytes);
        public string Compression => Source.Compression;
        public string Reason => Detail;
    }

    public MainWindow()
    {
        InitializeComponent();
        try { StartupBox.IsChecked = StartupRegistration.Enabled; } catch (Exception ex) { Banner.Text = ex.Message; }
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
                var totals = BuildFolderTotals(entries, roots.FirstOrDefault(r => r.Id == selectedId)?.Path);
                List<VolumeUsage> volumes = [];
                foreach (var driveRoot in roots.Select(r => System.IO.Path.GetPathRoot(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { var drive = new DriveInfo(driveRoot); volumes.Add(new(driveRoot, drive.TotalSize, drive.AvailableFreeSpace, DateTime.UtcNow)); }
                    catch (Exception ex) when (Scope.FileError(ex)) { }
                }
                return (Roots: roots, Rows: latest, Entries: entries, Totals: totals, Allocation: managedAllocation, Jobs: Engine.Store.Jobs(), Scans: scans, Volumes: volumes, Measured: Engine.Store.MeasuredSince(since));
            });
            if (App.Instance.Exiting) return;
            syncing = true;
            RootPicker.ItemsSource = snapshot.Roots;
            RootPicker.SelectedItem = snapshot.Roots.FirstOrDefault(r => r.Id == selectedId);
            FoldersGrid.ItemsSource = snapshot.Rows;
            FoldersGrid.SelectedItem = snapshot.Rows.FirstOrDefault(r => r.Root.Id == selectedId);
            syncing = false; selectNext = null;
            observations = snapshot.Entries; folderTotals = snapshot.Totals; jobs = snapshot.Jobs;
            history = snapshot.Scans.Where(s => s.RootId == selectedId && s.Utc >= since).OrderBy(s => s.Utc).TakeLast(32).ToArray();
            var known = snapshot.Rows.Where(r => r.Scan != null).ToArray();
            AllocatedCard.Text = known.Length == 0 ? "—" : Display.Bytes(snapshot.Allocation);
            EstimateCard.Text = known.Length == 0 ? "—" : Display.Bytes(known.Sum(r => r.Scan!.EstimatedSaving));
            ScopeCard.Text = $"{known.Length}/{snapshot.Roots.Count} folders scanned · {known.Sum(r => r.Scan!.Unknown)} unknown entries · known identities deduplicated across last scans";
            MeasuredCard.Text = Display.Bytes(snapshot.Measured.Delta);
            MeasuredCaption.Text = $"Signed · {snapshot.Measured.Measured} stable successful file measurements · UTC days";
            VolumesText.Text = snapshot.Volumes.Count == 0 ? "No available managed volumes." : string.Join("\n", snapshot.Volumes.Select(v => v.Summary));
            var pending = jobs.Count(j => j.Pending);
            ActivityCard.Text = $"{pending} queued/retrying/in progress · {jobs.Count(j => j.State == "Failed")} retained failures\n{Engine.Status}";
            WorkerText.Text = Engine.CurrentFile == null ? Engine.Status : $"Current: {Engine.CurrentFile} · {pending} pending";
            BusyBar.IsIndeterminate = Engine.CurrentFile != null || commandBusy;
            PauseButton.Content = Engine.Paused ? "Resume all" : "Pause after current";
            if (drill == null || Selected == null || !Scope.Within(drill, Selected.Path)) drill = Selected?.Path;
            RenderInsights(); RenderActivity(); RenderChart();
        }
        catch (Exception ex) { Banner.Text = "Could not refresh dashboard: " + ex.Message; }
        finally { syncing = false; refreshing = false; }
    }

    private async Task Command(Func<Task> action)
    {
        if (commandBusy || App.Instance.Exiting) return;
        commandBusy = true; FolderActions.IsEnabled = false; BusyBar.IsIndeterminate = true;
        try { await action(); }
        catch (Exception ex) { Banner.Text = ex.Message; }
        finally { commandBusy = false; FolderActions.IsEnabled = true; await RefreshAsync(); }
    }

    private ManagedFolder RequireRoot() => Selected ?? throw new InvalidOperationException("Add and select a managed folder first.");
    private void Navigate_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = int.Parse((string)((Button)sender).Tag);
    private async void Root_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing) return;
        drill = Selected?.Path; largest = false; await RefreshAsync();
    }
    private async void Period_Changed(object sender, SelectionChangedEventArgs e) { if (ready) await RefreshAsync(); }
    private void FolderGrid_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || syncing || FoldersGrid.SelectedItem is not FolderRow row) return;
        RootPicker.SelectedItem = RootPicker.Items.Cast<ManagedFolder>().FirstOrDefault(r => r.Id == row.Root.Id);
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var dialog = new OpenFolderDialog { Title = "Choose a local NTFS folder for analysis" };
        if (dialog.ShowDialog(this) != true) return;
        var root = await Engine.AddAsync(dialog.FolderName); selectNext = root.Id;
        Banner.Text = "Folder added in analysis-only mode. Edit its policy to enable automatic compression.";
    });
    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        try { Engine.RequestScan(RequireRoot().Id); Banner.Text = "A fresh read-only analysis is queued. Existing files are unchanged by analysis."; }
        catch (Exception ex) { Banner.Text = ex.Message; }
    }
    private async void Policy_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var defaults = Engine.Store.Scans(root.Id).FirstOrDefault()?.NativeDefaults ?? 0;
        var editor = new PolicyWindow(root, defaults) { Owner = this };
        if (editor.ShowDialog() != true) return;
        await Engine.SavePolicyAsync(root, editor.Policy);
        Banner.Text = editor.Policy.Enabled ? "Automatic policy enabled. Suitable existing and future files will be queued after analysis." : "Policy saved. Selective automation is disabled; existing compressed files and native defaults remain unchanged.";
    });
    private async void FolderPause_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); await Engine.SetFolderPausedAsync(root.Id, !root.Policy.Paused);
        Banner.Text = root.Policy.Paused ? "Folder resumed." : "Folder paused after the current operation. Native defaults remain active.";
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); await Engine.RemoveAsync(root.Id); drill = null;
        Banner.Text = "Stopped managing the folder. Existing files and native directory defaults are unchanged.";
    });
    private async void ScopeJob_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot(); var operation = (string)((Button)sender).Tag;
        Banner.Text = "Preparing a current scope preview…";
        var preview = await Engine.PreviewAsync(root, operation);
        var explanation = operation == "Decompress" ? "Automatic compression for this folder will be disabled first. Existing native defaults stay active."
            : operation == "ClearDefault" ? "Clear the inspected directory defaults only. Existing files remain compressed; future native inheritance from these directories stops."
            : "Compress suitable candidates under the current policy. Analysis-only status will remain unchanged.";
        var text = $"{root.Path}\n\n{preview.Files} eligible file(s) · {Display.Bytes(preview.LogicalBytes)} logical\nAt least {Display.Bytes(preview.RequiredHeadroom)} free headroom for the largest sequential operation.\n{preview.NativeDefaults} active native directory defaults found.\n{preview.Coverage}. Unsupported/excluded entries are omitted.\n\n{explanation}\n\nQueue this operation?";
        if (MessageBox.Show(this, text, operation + " preview", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var count = await Engine.QueueScopeAsync(root.Id, operation);
        Banner.Text = $"{count} operations queued. Large scopes refill the bounded queue after reconciliation. Resume paused work to process them.";
    });
    private async void SelectedDecompress_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var root = RequireRoot();
        var selected = InsightsGrid.SelectedItems.Cast<InsightRow>().Select(r => r.Source).Where(o => o.File is { Directory: false, Compressed: true }).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select supported compressed files first.");
        var space = root.Policy.ReserveBytes + selected.Max(o => o.File!.Logical);
        if (MessageBox.Show(this, $"Decompress {selected.Length} selected file(s)? Requires at least {Display.Bytes(space)} free headroom. Selective automation for this managed folder will be disabled; native defaults remain active.", "Decompression preview", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var count = await Engine.QueueSelectedAsync(root.Id, selected); Banner.Text = $"{count} files queued for decompression. Automatic policy disabled.";
    });
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try { await Engine.SetPausedAsync(!Engine.Paused); PauseButton.Content = Engine.Paused ? "Resume all" : "Pause after current"; }
        catch (Exception ex) { Banner.Text = ex.Message; }
        finally { if (!App.Instance.Exiting) PauseButton.IsEnabled = true; }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { StartupRegistration.Enabled = StartupBox.IsChecked == true; Banner.Text = StartupBox.IsChecked == true ? "Start-at-sign-in enabled for this user. The tray worker will resume after login." : "Start-at-sign-in disabled."; }
        catch (Exception ex) { Banner.Text = ex.Message; StartupBox.IsChecked = false; }
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await App.Instance.ExitAsync();
    public void ShowClosingStatus() { timer.Stop(); FolderActions.IsEnabled = false; PauseButton.IsEnabled = false; Banner.Text = "Exiting after the current operation. Remaining jobs are persisted for the next launch."; }
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
            if (search.Length > 0 && !(entry.Path + " " + entry.Reason).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.File?.Directory == true)
            {
                var total = folderTotals.GetValueOrDefault(entry.Path);
                rows.Add(new(entry, total.Logical, total.Unknown > 0 ? null : total.Allocated, $"Observed policy-scope descendants · {total.Unknown} unknown · " + entry.Reason));
            }
            else rows.Add(new(entry, entry.File?.Logical, entry.File?.Allocated, entry.Reason + (entry.EstimatedSaving.HasValue ? " · estimate " + entry.EstimateDisplay : "")));
        }
        InsightsGrid.ItemsSource = rows.OrderByDescending(r => r.AllocatedBytes ?? -1).Take(500).ToArray();
        DrillPath.Text = (largest ? "Largest observed files in " + Selected?.Path : drill) ?? "Choose a managed folder.";
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
    private void InsightSearch_Changed(object sender, TextChangedEventArgs e) { if (ready) RenderInsights(); }
    private void Largest_Click(object sender, RoutedEventArgs e) { largest = true; RenderInsights(); }
    private void InsightUp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || drill == null) return;
        largest = false; if (!drill.Equals(Selected.Path, StringComparison.OrdinalIgnoreCase)) drill = System.IO.Path.GetDirectoryName(drill);
        RenderInsights();
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
        var filter = (StateFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var search = ActivitySearch.Text.Trim();
        var selected = JobsGrid.SelectedItems.Cast<WorkItem>().Select(j => j.Id).ToHashSet();
        var visible = jobs.Where(j => filter == "All" || (filter == "Pending" ? j.Pending : j.State == filter))
            .Where(j => search.Length == 0 || (j.Path + " " + j.Message + " " + j.Operation).Contains(search, StringComparison.OrdinalIgnoreCase)).Take(1000).ToArray();
        JobsGrid.ItemsSource = visible;
        foreach (var item in visible.Where(j => selected.Contains(j.Id))) JobsGrid.SelectedItems.Add(item);
    }
    private void ActivityFilter_Changed(object sender, SelectionChangedEventArgs e) { if (ready) RenderActivity(); }
    private void ActivitySearch_Changed(object sender, TextChangedEventArgs e) { if (ready) RenderActivity(); }
    private void Job_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (JobsGrid.SelectedItem is WorkItem job) JobDetails.Text = $"{job.Operation} · {job.State} · attempts {job.Attempts}\n{job.Path}\nIdentity: {job.Expected.Identity}\n{job.Message}\nMeasured signed reduction: {job.DeltaDisplay}";
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Command(async () =>
    {
        var count = await Engine.RetryAsync(JobsGrid.SelectedItems.Cast<WorkItem>().ToArray()); Banner.Text = $"{count} failed/skipped operations requeued. Changed files require a fresh scan; disabled automatic policies remain disabled.";
    });

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) { if (ready) RenderChart(); }
    private void RenderChart()
    {
        HistoryChart.Children.Clear();
        HistoryCaption.Text = $"{Selected?.Path ?? "Choose a folder"} · scan allocation · amber = partial coverage · gaps have no bars";
        if (history.Count == 0) { HistoryRange.Text = "No scan history in this period."; return; }
        var width = Math.Max(100, HistoryChart.ActualWidth);
        var maximum = Math.Max(1, history.Max(s => s.Allocated));
        var start = history[0].Utc; var end = history[^1].Utc;
        var span = Math.Max(1, (end - start).TotalSeconds);
        var barWidth = Math.Max(2, Math.Min(24, width / Math.Max(1, history.Count) - 4));
        foreach (var scan in history)
        {
            var height = Math.Max(2, 125.0 * scan.Allocated / maximum);
            var bar = new Rectangle { Width = barWidth, Height = height, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(scan.Complete && scan.Unknown == 0 ? "#278771" : "#CF963D")),
                ToolTip = $"{scan.Utc.ToLocalTime():G}\n{scan.AllocationDisplay}\n{scan.Coverage}" };
            Canvas.SetLeft(bar, (width - barWidth) * (scan.Utc - start).TotalSeconds / span); Canvas.SetBottom(bar, 0); HistoryChart.Children.Add(bar);
        }
        HistoryRange.Text = $"{start.ToLocalTime():g} → {end.ToLocalTime():g} · chart maximum {Display.Bytes(maximum)} · primary streams only";
    }
}
