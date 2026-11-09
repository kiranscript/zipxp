using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using StorageExplorer.Models;
using StorageExplorer.Services;

namespace StorageExplorer;

public partial class MainWindow : Window
{
    private readonly FolderScanner scanner = new();
    private readonly CompressionService compression = new();
    private readonly FileLauncher launcher = new();
    private string? root;
    private string? directory;
    private bool busy;
    private bool directoryKnown;
    private bool closeRequested;
    private CancellationTokenSource? cancellation;

    public MainWindow() => InitializeComponent();

    private void UpdateControls()
    {
        if (ChooseButton == null) return;
        ChooseButton.IsEnabled = !busy;
        UpButton.IsEnabled = !busy && directory != null && !string.Equals(root, directory, StringComparison.OrdinalIgnoreCase);
        RefreshButton.IsEnabled = !busy && directory != null;
        var selectedFiles = FileGrid.SelectedItems.Cast<FileEntry>().Any(e => !e.IsDirectory);
        CompressButton.IsEnabled = DecompressButton.IsEnabled = OpenButton.IsEnabled = !busy && directoryKnown && selectedFiles;
        DirectoryDefault.IsEnabled = !busy && directoryKnown;
        StopButton.IsEnabled = busy && cancellation != null && !cancellation.IsCancellationRequested;
        JobBar.IsIndeterminate = busy;
    }

    private void FinishWork()
    {
        cancellation?.Dispose();
        cancellation = null;
        busy = false;
        UpdateControls();
        if (closeRequested) Close();
    }

    private async Task LoadFolderAsync(string chosenRoot, string target)
    {
        busy = true;
        directoryKnown = false;
        root = chosenRoot;
        directory = target;
        PathBox.Text = target;
        FileGrid.SelectedItems.Clear();
        FileGrid.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        DirectoryDefault.IsChecked = null;
        StatusText.Text = "Reading immediate children…";
        JobText.Text = "Scanning folder…";
        CurrentFileText.Text = "";
        UpdateControls();
        try
        {
            var scan = await Task.Run(() => scanner.Scan(chosenRoot, target));
            FileGrid.ItemsSource = scan.Entries;
            // Only Click mutates the default; assigning its observed state never writes it.
            DirectoryDefault.IsChecked = scan.Directory.IsCompressed;
            directoryKnown = true;
            EmptyText.Visibility = scan.Entries.Count == 0 && scan.Error == null ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = scan.Error ?? $"{scan.Entries.Count} immediate entries. Double-click a folder to enter or a file to open its original path. Select multiple files with Ctrl or Shift.";
            JobText.Text = "No operation running.";
        }
        catch (Exception ex) when (ServiceErrors.IsFileError(ex))
        {
            StatusText.Text = $"Cannot read this folder: {ex.Message} Use Refresh to retry, Up, or Choose folder.";
            JobText.Text = "Scan failed.";
        }
        finally { FinishWork(); }
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a disposable local NTFS test folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var path = FolderScope.Normalize(dialog.FolderName);
                await LoadFolderAsync(path, path);
            }
            catch (Exception ex) when (ServiceErrors.IsFileError(ex)) { StatusText.Text = ex.Message; }
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && root != null && directory != null && !directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            await LoadFolderAsync(root, Path.GetDirectoryName(directory)!);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && root != null && directory != null) await LoadFolderAsync(root, directory);
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void FileGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (busy || !directoryKnown || root == null || directory == null) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow) element = VisualTreeHelper.GetParent(element);
        if (element is not DataGridRow { Item: FileEntry entry }) return;
        if (entry.IsDirectory)
        {
            if (entry.Snapshot == null) { StatusText.Text = entry.Result; return; }
            await LoadFolderAsync(root, entry.FullPath);
        }
        else await OpenFilesAsync([entry]);
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenFilesAsync(FileGrid.SelectedItems.Cast<FileEntry>().Where(f => !f.IsDirectory).ToArray());

    private async Task OpenFilesAsync(IReadOnlyList<FileEntry> files)
    {
        if (busy || root == null || directory == null) return;
        var chosenRoot = root;
        var currentDirectory = directory;
        busy = true;
        cancellation = new();
        UpdateControls();
        int failures = 0, opened = 0;
        try
        {
            foreach (var entry in files)
            {
                if (cancellation.IsCancellationRequested) break;
                StatusText.Text = $"Opening original path: {entry.FullPath}";
                try
                {
                    await Task.Run(() => launcher.Open(chosenRoot, currentDirectory, entry));
                    opened++;
                }
                catch (Exception ex) when (ServiceErrors.IsFileError(ex) || ex is InvalidOperationException)
                {
                    failures++;
                    entry.Apply(new(entry.FullPath, JobOutcome.Failed, $"Open failed: {ex.Message}"));
                }
            }
            StatusText.Text = $"Sent {opened} original path(s) to their registered applications. {failures} failed to open{(failures == 0 ? "." : "; see each entry's result.")}";
        }
        finally { FinishWork(); }
    }

    private async void Compress_Click(object sender, RoutedEventArgs e) => await RunJobAsync(true);
    private async void Decompress_Click(object sender, RoutedEventArgs e) => await RunJobAsync(false);

    private async Task RunJobAsync(bool compress)
    {
        if (busy || !directoryKnown || root == null || directory == null) return;
        var selection = FileGrid.SelectedItems.Cast<FileEntry>().Where(e => !e.IsDirectory).ToArray();
        if (selection.Length == 0) return;
        var chosenRoot = root;
        var currentDirectory = directory;
        busy = true;
        cancellation = new();
        UpdateControls();
        StatusText.Text = compress ? "Compressing selected files sequentially…" : "Decompressing selected files sequentially…";
        var progress = new Progress<JobProgress>(p =>
        {
            if (p.Result != null) selection.First(f => f.FullPath == p.Result.FullPath).Apply(p.Result);
            ShowProgress(p);
        });
        try
        {
            var summary = await Task.Run(() => compression.RunAsync(chosenRoot, currentDirectory, selection, compress, progress, cancellation.Token));
            ShowProgress(summary);
            StatusText.Text = $"{(summary.Completed < summary.Queued ? "Stopped before the next file" : "Job finished")}. Per-file results and compact diagnostics are available below. Refresh to rescan filesystem state.";
        }
        catch (Exception ex) when (ServiceErrors.IsFileError(ex) || ex is InvalidOperationException)
        { StatusText.Text = $"Job failed: {ex.Message}. Completed files retain their current filesystem state. Refresh to inspect."; }
        finally { FinishWork(); }
    }

    private void ShowProgress(JobProgress p)
    {
        JobText.Text = $"{p.Completed}/{p.Queued} processed · {p.Succeeded} succeeded · {p.Skipped} skipped · {p.Failed} failed · measured allocation reduction: {FileEntry.FormatBytes(p.AllocationDelta)} (signed, {p.MeasuredFiles} stable successful file(s))";
        CurrentFileText.Text = p.CurrentFile == null ? "" : $"Current file: {p.CurrentFile}";
    }

    private async void DirectoryDefault_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !directoryKnown || root == null || directory == null) return;
        var desired = DirectoryDefault.IsChecked == true;
        var chosenRoot = root;
        var currentDirectory = directory;
        busy = true;
        cancellation = new();
        UpdateControls();
        StatusText.Text = "Updating this directory's default only…";
        JobText.Text = "Waiting for compact; existing files and subfolders are unchanged.";
        try
        {
            var result = await Task.Run(() => compression.SetDirectoryDefaultAsync(chosenRoot, currentDirectory, desired));
            StatusText.Text = $"{result.Outcome}: {result.Message}";
            StatusText.ToolTip = result.Diagnostics;
            // Re-read even after failure; never leave a speculative checkbox state behind.
            try
            {
                var state = await Task.Run(() => new FolderScope().Inspect(currentDirectory));
                DirectoryDefault.IsChecked = state.IsCompressed;
            }
            catch (Exception ex) when (ServiceErrors.IsFileError(ex))
            {
                DirectoryDefault.IsChecked = null;
                directoryKnown = false;
                StatusText.Text += $" State unavailable: {ex.Message}. Refresh to retry.";
            }
            JobText.Text = "Directory operation finished. No existing-file allocation change is counted.";
        }
        finally { FinishWork(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        StatusText.Text = "Stop requested. Waiting for the current operation to exit; no further files will start.";
        UpdateControls();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        closeRequested = true;
        cancellation?.Cancel();
        StatusText.Text = "Closing after the current operation finishes. Remaining files will not start.";
        UpdateControls();
    }
}
