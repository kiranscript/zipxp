using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ZipXP.Core;
using ZipXP.Core.Services;

namespace ZipXP.App;

/// <summary>Explicit desktop commands. Paths to mutate always come from the engine's managed records.</summary>
internal sealed class DesktopBridge(Window owner)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Engine Engine => App.Instance.Engine;
    private ManagedFolder Root(JsonElement args) => Engine.Store.Roots().FirstOrDefault(r => r.Id == Text(args, "rootId"))
        ?? throw new ArgumentException("This folder is no longer managed. Refresh and select another folder.");
    private static string Text(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException($"Missing {key}.");
    private static bool Flag(JsonElement args, string key) => args.GetProperty(key).GetBoolean();
    private bool Confirm(string text, string title) => !App.Instance.Exiting && MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, title.Contains("Restore", StringComparison.Ordinal) || title.Contains("Stop managing", StringComparison.Ordinal) ? MessageBoxImage.Warning : MessageBoxImage.Information) == MessageBoxResult.Yes;

    public async Task<object?> ExecuteAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "snapshot": return await Task.Run(Snapshot);
            case "insights": return await Task.Run(() => Insights(args));
            case "addFolder":
                var picker = new OpenFolderDialog { Title = "Choose a folder for ZipXP to look after" };
                return picker.ShowDialog(owner) == true ? (await Engine.AddAsync(picker.FolderName)).Id : null;
            case "scan": Engine.RequestScan(Root(args).Id); return "Scan requested. Scanning never changes your files.";
            case "pause": await Engine.SetPausedAsync(Flag(args, "paused")); return Engine.Paused ? "Paused. The current file will finish first." : "Background work resumed.";
            case "pauseFolder": await Engine.SetFolderPausedAsync(Root(args).Id, Flag(args, "paused")); return "Folder pause setting updated.";
            case "startup": StartupRegistration.Enabled = Flag(args, "enabled"); return "Sign-in preference saved.";
            case "savePolicy":
                var policy = args.GetProperty("policy").Deserialize<FolderPolicy>(Json) ?? throw new ArgumentException("Missing folder settings.");
                // Pause is a separate action and cannot be accidentally overwritten by an open settings form.
                var root = Root(args);
                await Engine.SavePolicyAsync(root, policy with { Paused = root.Policy.Paused });
                return "Settings saved. Files already compressed stay compressed until you restore them.";
            case "queue": return await QueueAsync(args);
            case "restoreSelected":
                var selectedRoot = Root(args);
                var paths = args.GetProperty("paths").Deserialize<string[]>(Json) ?? [];
                var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = Engine.Store.Observations(selectedRoot.Id).Where(o => wanted.Contains(o.Path) && o.File is { Directory: false, Compressed: true }).ToArray();
                if (selected.Length == 0) return "No compressed files selected.";
                var headroom = checked(selectedRoot.Policy.ReserveBytes + selected.Max(o => o.File!.Logical));
                if (!Confirm($"Restore the original size of {selected.Length} selected files?\n\nNeeds at least {Display.Bytes(headroom)} free on the drive. Automatic compression for this folder will be turned off. No files are deleted.", "Restore selected files")) return "No changes made.";
                return Queued(await Engine.QueueSelectedAsync(selectedRoot.Id, selected));
            case "remove":
                var removing = Root(args);
                if (!Confirm($"Stop managing {removing.Path}?\n\nFiles stay exactly as they are, including any existing compression. To uncompress them first, cancel and choose Restore original size.", "Stop managing folder")) return "No changes made.";
                await Engine.RemoveAsync(removing.Id); return "Folder removed from ZipXP. Its files stay as they are.";
            case "retry":
                var retry = Engine.Store.Jobs().FirstOrDefault(j => j.Id == Text(args, "jobId")) ?? throw new ArgumentException("Activity item no longer exists.");
                return Queued(await Engine.RetryAsync([retry]));
            case "showFile":
                var job = Engine.Store.Jobs().FirstOrDefault(j => j.Id == Text(args, "jobId")) ?? throw new ArgumentException("Activity item no longer exists.");
                if (!File.Exists(job.Path) && !Directory.Exists(Path.GetDirectoryName(job.Path))) throw new IOException("That item is no longer there.");
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = File.Exists(job.Path) ? $"/select,\"{job.Path}\"" : $"\"{Path.GetDirectoryName(job.Path)}\"", UseShellExecute = true });
                return "Opened in File Explorer.";
            case "quit":
                // Reply before disposing WebView2 and closing the dispatcher.
                _ = owner.Dispatcher.BeginInvoke(async () => await App.Instance.ExitAsync());
                return "Finishing the current file, then quitting.";
            default: throw new ArgumentException("Unknown desktop command.");
        }
    }

    private object Snapshot()
    {
        var roots = Engine.Store.Roots();
        var scans = Engine.Store.Scans();
        var entries = roots.SelectMany(r => Engine.Store.Observations(r.Id)).ToArray();
        var files = entries.Where(o => o.File is { Directory: false }).Select(o => o.File!).DistinctBy(f => f.Identity).ToArray();
        var measured = files.Where(f => f.Allocated != null).ToArray();
        var volumes = new List<object>();
        foreach (var name in roots.Select(r => Path.GetPathRoot(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var drive = new DriveInfo(name); volumes.Add(new { Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({name.TrimEnd('\\')})" : $"{drive.VolumeLabel} ({name.TrimEnd('\\')})", Total = drive.TotalSize, Free = drive.AvailableFreeSpace }); }
            catch (Exception ex) when (Scope.FileError(ex)) { }
        }
        return new
        {
            Folders = roots.Select(r => new { r.Id, r.Name, r.Path, r.Policy, Scan = scans.FirstOrDefault(s => s.RootId == r.Id), Status = Plain.Message(Engine.FolderStatus(r.Id)) }),
            Jobs = Engine.Store.Jobs().Select(j => new { j.Id, j.RootId, j.Path, FileName = Path.GetFileName(j.Path), j.State, StateText = Plain.JobState(j.State).Text, Action = Plain.Action(j.Operation), Message = Plain.Message(j.Message), j.Delta, Utc = j.FinishedUtc ?? j.CreatedUtc, j.Pending }),
            Engine.Paused, Startup = StartupRegistration.Enabled, Status = Plain.Message(Engine.Status), Engine.CurrentFile,
            Original = measured.Sum(f => f.Logical), Allocated = measured.Sum(f => f.Allocated ?? 0), Unknown = entries.Count(e => e.File == null || (e.File.Directory == false && e.File.Allocated == null)), Volumes = volumes
        };
    }

    private object Insights(JsonElement args)
    {
        var root = Root(args);
        var path = Text(args, "path");
        if (string.IsNullOrEmpty(path)) path = root.Path;
        if (!Scope.Within(path, root.Path)) throw new ArgumentException("Choose a folder inside the managed root.");
        var largest = Flag(args, "largest");
        var search = Text(args, "search");
        var entries = Engine.Store.Observations(root.Id);
        var totals = new Dictionary<string, (long Logical, long Allocated, int Unknown)>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e.File?.Directory != true))
        {
            for (var parent = Path.GetDirectoryName(entry.Path); parent != null && Scope.Within(parent, root.Path); parent = Path.GetDirectoryName(parent))
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
        var rows = entries.Where(e => largest ? e.File?.Directory == false : string.Equals(Path.GetDirectoryName(e.Path), path, StringComparison.OrdinalIgnoreCase))
            .Where(e => search.Length == 0 || (e.Path + " " + Plain.Message(e.Reason)).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                var directory = e.File?.Directory == true;
                var total = totals.GetValueOrDefault(e.Path);
                return new StorageItem(e.Path, e.Name, directory, directory ? total.Logical : e.File?.Logical,
                    directory ? total.Unknown == 0 ? (long?)total.Allocated : null : e.File?.Allocated,
                    e.File?.Compressed == true,
                    Plain.Message(e.Reason) + (directory && total.Unknown > 0 ? $" · {total.Unknown} items inside couldn't be measured" : ""));
            });
        var page = StorageInsights.CreatePage(rows, Text(args, "sort"));
        return new { Path = path, Parent = string.Equals(path, root.Path, StringComparison.OrdinalIgnoreCase) ? null : Path.GetDirectoryName(path), page.Rows, page.TotalRows };
    }

    private string Queued(int count) => $"{count} items queued." + (Engine.Paused ? " Choose Resume all to start." : " See Activity for progress.");

    private async Task<string> QueueAsync(JsonElement args)
    {
        var operation = Text(args, "operation");
        if (operation is not ("Compress" or "Decompress" or "ClearDefault")) throw new ArgumentException("Unknown operation.");
        var roots = args.TryGetProperty("rootId", out var id) && id.ValueKind == JsonValueKind.String
            ? new[] { Root(args) }
            : Engine.Store.Roots().Where(r => operation != "Compress" || !r.Policy.Paused).ToArray();
        if (roots.Length == 0) return "Add a folder first.";
        var previews = new List<(ManagedFolder Root, JobPreview Preview)>();
        foreach (var root in roots) previews.Add((root, await Engine.PreviewAsync(root, operation)));
        // A restore with zero eligible files still disables automatic compression, matching the native UI.
        var count = previews.Sum(p => p.Preview.Files);
        var headroom = previews.Max(p => p.Preview.RequiredHeadroom);
        var explanation = operation switch
        {
            "Decompress" => "Files will take their original space again. Automatic compression will be turned off for these folders. No files are deleted. Windows folder compression, if enabled, can still compress new files independently.",
            "ClearDefault" => "Windows will stop compressing new files in these folders by itself. Existing compressed files stay compressed.",
            _ => "Files will be compressed once in the background. Automatic compression will not be enabled. You can restore their original size later."
        };
        var title = operation switch { "Decompress" => "Restore original size", "ClearDefault" => "Turn off Windows compression", _ => "Compress preview" };
        var coverage = previews.All(p => p.Preview.Coverage == "Complete primary-stream scan") ? "Everything in these folders was checked." : "Partial scan: items that couldn't be read are left out.";
        var scope = roots.Length == 1 ? roots[0].Path : $"{roots.Length} managed folders";
        if (!Confirm($"{scope}\n\n{count} eligible items · {Display.Bytes(previews.Sum(p => p.Preview.LogicalBytes))} in total\nNeeds at least {Display.Bytes(headroom)} free on each affected drive while it works.\n{coverage}\n\n{explanation}\n\nGo ahead?", title)) return "No changes made.";
        var queued = 0;
        foreach (var (root, _) in previews) queued += await Engine.QueueScopeAsync(root.Id, operation);
        return Queued(queued);
    }
}
