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
    // The folder picker is native; the page only ever receives its result and can't name an arbitrary path.
    private string? pendingPick;
    private ManagedFolder Root(JsonElement args) => Engine.Store.Roots().FirstOrDefault(r => r.Id == Text(args, "rootId"))
        ?? throw new ArgumentException("This folder is no longer managed. Refresh and select another folder.");
    private static string Text(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException($"Missing {key}.");
    private static bool Flag(JsonElement args, string key) => args.GetProperty(key).GetBoolean();
    private bool Confirm(string text, string title) => !App.Instance.Exiting &&
        MessageBox.Show(owner, text, title, MessageBoxButton.YesNo,
            title.Contains("Restore", StringComparison.Ordinal) || title.Contains("Stop managing", StringComparison.Ordinal) ? MessageBoxImage.Warning : MessageBoxImage.Information,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private string PendingWorkNotice(ManagedFolder root) =>
        Engine.Store.ActiveJobs().Any(j => j.RootId == root.Id)
            ? "\n\nThis will cancel the work still queued for this folder." : "";

    private async Task<string> SavePolicyAsync(ManagedFolder root, FolderPolicy policy)
    {
        await Engine.SavePolicyAsync(root, policy);
        if (!policy.Enabled) return "Settings saved. Auto-compression is off. Compressed files stay compressed.";
        return "Settings saved. Auto-compression is on for existing, new and changed files." +
            (Engine.Paused ? " Choose Resume all folders to continue." :
                policy.Paused ? " Choose Resume folder from More actions to continue." : "");
    }

    public async Task<object?> ExecuteAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "snapshot": return await Task.Run(Snapshot);
            case "insights": return await Task.Run(() => Insights(args));
            case "suggestions": return await Task.Run(() => Suggestions().Select(s => new { s.Path, s.Name, s.Note }).ToArray());
            case "chooseFolder":
                var picker = new OpenFolderDialog { Title = "Choose a folder for ZipXP to look after" };
                if (picker.ShowDialog(owner) != true) return null;
                pendingPick = picker.FolderName;
                var pickedName = Path.GetFileName(pendingPick.TrimEnd('\\', '/'));
                return new { Path = pendingPick, Name = pickedName.Length > 0 ? pickedName : pendingPick };
            case "addFolder":
                string target;
                if (args.TryGetProperty("path", out var suggested) && suggested.ValueKind == JsonValueKind.String)
                {
                    // Only a folder ZipXP itself suggested can be added without the picker.
                    var wantedPath = suggested.GetString()!;
                    var matches = Suggestions().Where(s => string.Equals(s.Path, wantedPath, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) throw new ArgumentException("That folder is no longer suggested. Choose it with Choose a folder instead.");
                    target = matches[0].Path;
                }
                else target = pendingPick ?? throw new ArgumentException("Choose a folder first.");
                var added = await Engine.AddAsync(target);
                pendingPick = null; // keep it when the add fails, so the dialog can be retried
                // Choosing "keep compressed automatically" in the add dialog is the user's consent, so no second prompt.
                if (args.TryGetProperty("autoCompress", out var auto) && auto.ValueKind == JsonValueKind.True)
                {
                    // The folder exists now; a failure here must not strand the dialog. Auto-compression simply stays off, as the folder row shows.
                    try { await Engine.SavePolicyAsync(added, added.Policy with { Enabled = true }); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
                }
                return added.Id;
            case "discardPick": pendingPick = null; return "Cancelled.";
            case "stop":
                var stopping = Root(args);
                var stopped = await Engine.StopWorkAsync(stopping.Id);
                return stopped == 0 ? "Nothing is waiting. The current file will finish." : $"Stopped. {Plain.Count(stopped, "waiting file")} won’t be processed, and the current file will finish.";
            case "scan": Engine.RequestScan(Root(args).Id); return "Scan requested. Scanning never changes your files.";
            case "pause": await Engine.SetPausedAsync(Flag(args, "paused")); return Engine.Paused ? "Paused. The current file will finish first." : "Background work resumed.";
            case "pauseFolder": await Engine.SetFolderPausedAsync(Root(args).Id, Flag(args, "paused")); return "Folder pause setting updated.";
            case "startup": StartupRegistration.Enabled = Flag(args, "enabled"); return "Sign-in preference saved.";
            case "setAutoCompression":
                var folder = Root(args);
                var enabled = Flag(args, "enabled");
                if (enabled == folder.Policy.Enabled) return "No changes made.";
                var title = enabled ? "Turn on auto-compression" : "Turn off auto-compression";
                var detail = enabled
                    ? "ZipXP will compress eligible existing, new and changed files while it runs. Files will open as usual.\n\nCompressed files use a little extra CPU when opened. That is usually unnoticeable on an SSD, but can be slower on a hard disk for large files you open often. You can restore files at any time."
                    : "ZipXP will stop compressing files automatically here. Compressed files will stay compressed.";
                if (!Confirm($"{title} for {folder.Name}?\n\n{detail}" + PendingWorkNotice(folder), title)) return "No changes made.";
                return await SavePolicyAsync(folder, folder.Policy with { Enabled = enabled });
            case "savePolicy":
                var submitted = args.GetProperty("policy").Deserialize<FolderPolicy>(Json) ?? throw new ArgumentException("Missing folder settings.");
                var root = Root(args);
                // Pause is a separate action and cannot be accidentally overwritten by an open settings form.
                var policy = submitted with { Paused = root.Policy.Paused };
                if (policy == root.Policy) return "No changes made.";
                var pendingNotice = PendingWorkNotice(root);
                if (pendingNotice.Length > 0 && !Confirm("Save changes to this folder’s settings?" + pendingNotice, "Save folder settings"))
                    return "No changes made.";
                return await SavePolicyAsync(root, policy);
            case "queue": return await QueueAsync(args);
            case "restoreSelected":
                var selectedRoot = Root(args);
                var paths = args.GetProperty("paths").Deserialize<string[]>(Json) ?? [];
                var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = Engine.Store.Observations(selectedRoot.Id).Where(o => wanted.Contains(o.Path)).ToArray();
                var restorable = Engine.RestorableFiles(selectedRoot, selected).ToArray();
                if (restorable.Length == 0) return "ZipXP found no selected files it can restore.";
                var headroom = checked(selectedRoot.Policy.ReserveBytes + restorable.Max(f => f.Logical));
                if (!Confirm($"Restore the original size of {Plain.Count(restorable.Length, "selected file")}?\n\nNeeds at least {Display.Bytes(headroom)} free on the drive. Automatic compression for this folder will be turned off. No files are deleted.", "Restore selected files")) return "No changes made.";
                return Queued(await Engine.QueueSelectedAsync(selectedRoot.Id, selected));
            case "remove":
                var removing = Root(args);
                if (!Confirm($"Stop managing {removing.Path}?\n\nFiles stay exactly as they are, including any existing compression. To uncompress them first, cancel and choose Restore files.", "Stop managing folder")) return "No changes made.";
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
        var observations = roots.ToDictionary(r => r.Id, r => Engine.Store.Observations(r.Id));
        var entries = observations.Values.SelectMany(items => items).ToArray();
        var measured = entries.Where(o => o.File is { Directory: false, Allocated: not null })
            .Select(o => o.File!).DistinctBy(f => f.Identity);
        var savings = Engine.Store.MeasuredSince(DateTime.MinValue);
        var jobs = Engine.Store.Jobs();
        var volumes = new List<object>();
        foreach (var name in roots.Select(r => Path.GetPathRoot(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var drive = new DriveInfo(name); volumes.Add(new { Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"Local Disk ({name.TrimEnd('\\')})" : $"{drive.VolumeLabel} ({name.TrimEnd('\\')})", Total = drive.TotalSize, Free = drive.AvailableFreeSpace }); }
            catch (Exception ex) when (Scope.FileError(ex)) { }
        }
        return new
        {
            Folders = roots.Select(r => new
            {
                r.Id, r.Name, r.Path, r.Policy,
                Scan = scans.FirstOrDefault(s => s.RootId == r.Id),
                Skips = Skips(observations[r.Id]),
                Run = Run(r, jobs),
                Status = Plain.Message(Engine.FolderStatus(r.Id)),
                CanRestore = Engine.RestorableFiles(r, observations[r.Id]).Any()
            }),
            Jobs = jobs.Select(j => new { j.Id, j.RootId, j.Path, FileName = Path.GetFileName(j.Path), j.State, StateText = Plain.JobState(j.State).Text, Action = Plain.Action(j.Operation), Message = Plain.Message(j.Message), j.Delta, Utc = j.FinishedUtc ?? j.CreatedUtc, j.Pending }),
            Engine.Paused, Startup = StartupRegistration.Enabled, Status = Plain.Message(Engine.Status), Engine.CurrentFile,
            Savings = new { savings.Delta, savings.Measured },
            Allocated = measured.Sum(f => f.Allocated!.Value),
            Unknown = entries.Count(e => e.File == null || (e.File.Directory == false && e.File.Allocated == null)),
            Volumes = volumes
        };
    }

    /// <summary>Progress of the folder's current or latest batch of work, derived from its jobs since the batch began.</summary>
    private object? Run(ManagedFolder root, IReadOnlyList<WorkItem> jobs)
    {
        if (Engine.Store.RunStart(root.Id) is not { } start) return null;
        // A retried file has a newer job; count each file once, by its latest attempt.
        var mine = jobs.Where(j => j.RootId == root.Id && j.CreatedUtc >= start)
            .GroupBy(j => (j.Expected.Identity, j.Operation)).Select(g => g.MaxBy(j => j.CreatedUtc)!).ToArray();
        if (mine.Length == 0) return null;
        var pending = mine.Count(j => j.Pending);
        var running = mine.FirstOrDefault(j => j.State == "Running");
        var waiting = pending == 0 || running != null ? null : Engine.Paused ? "Paused" : Engine.WaitingReason(root.Id);
        return new
        {
            Operation = mine.MinBy(j => j.CreatedUtc)!.Operation,
            StartedUtc = start,
            Total = mine.Length,
            Pending = pending,
            Failed = mine.Count(j => j.State == "Failed"),
            Skipped = mine.Count(j => j.State == "Skipped"),
            SavedSoFar = mine.Where(j => j.State == "Succeeded").Sum(j => j.Delta ?? 0),
            FinishedUtc = pending == 0 ? mine.Max(j => j.FinishedUtc ?? j.CreatedUtc) : (DateTime?)null,
            Phase = pending == 0 ? "done" : running != null ? "working" : waiting != null ? "waiting" : "queued",
            Waiting = waiting != null ? Plain.Message(waiting) : null,
            CurrentFile = running == null ? null : Path.GetFileName(running.Path)
        };
    }

    /// <summary>Why files in a scan are not candidates, so the page can tell "already compact" from "nothing compressible".</summary>
    private static object Skips(IReadOnlyList<Observation> entries)
    {
        int compressed = 0, tooSmall = 0, lowBenefit = 0, excluded = 0, other = 0;
        long compressedLogical = 0, compressedAllocated = 0;
        foreach (var entry in entries.Where(e => e.File is { Directory: false }))
        {
            var reason = entry.Reason;
            if (reason == "Candidate") continue;
            if (reason == "Already compressed") { compressed++; compressedLogical += entry.File!.Logical; compressedAllocated += entry.File.Allocated ?? entry.File.Logical; }
            else if (reason == "Below minimum size") tooSmall++;
            else if (reason.StartsWith("Permanent: low estimated benefit", StringComparison.Ordinal)) lowBenefit++;
            else if (reason == "Excluded by policy") excluded++;
            else other++;
        }
        return new { AlreadyCompressed = new { Files = compressed, Logical = compressedLogical, Allocated = compressedAllocated }, TooSmall = tooSmall, LowBenefit = lowBenefit, Excluded = excluded, Other = other };
    }

    private static readonly string[] CloudFolderNames = ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloud", "Box", "pCloud", "MEGA", "Sync"];
    private static bool IsCloudSynced(string full)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root) && Scope.Within(full, Scope.Normalize(root))) return true;
        }
        return full.Split('\\', '/').Any(part => CloudFolderNames.Any(name => part.Equals(name, StringComparison.OrdinalIgnoreCase) || part.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) || part.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Well-known folders that usually compress well, on local NTFS drives and not already managed. Never synced, media or system folders.</summary>
    private List<(string Path, string Name, string Note)> Suggestions()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string Path, string Note)[] candidates =
        [
            (System.IO.Path.Combine(profile, "source"), "Source code and build output usually compress well."),
            (System.IO.Path.Combine(profile, "repos"), "Source code and build output usually compress well."),
            (System.IO.Path.Combine(profile, "code"), "Source code and build output usually compress well."),
            (System.IO.Path.Combine(profile, "projects"), "Source code and build output usually compress well."),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents, spreadsheets and text compress well."),
            (System.IO.Path.Combine(profile, "Downloads"), "Mixed contents: installers and archives save little.")
        ];
        var roots = Engine.Store.Roots();
        var suggestions = new List<(string Path, string Name, string Note)>();
        foreach (var (candidate, note) in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                var full = Scope.Normalize(candidate);
                // Folders synced by OneDrive and friends hold placeholder files, which ZipXP skips.
                if (IsCloudSynced(full)) continue;
                if (suggestions.Any(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
                if (roots.Any(r => Scope.Within(full, r.Path) || Scope.Within(r.Path, full))) continue;
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(full)!);
                if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                suggestions.Add((full, System.IO.Path.GetFileName(full.TrimEnd('\\', '/')), note));
            }
            catch (Exception ex) when (Scope.FileError(ex) || ex is ArgumentException) { }
        }
        return suggestions.Take(4).ToList();
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

    private string Queued(int count) => $"{count} items queued." + (Engine.Paused ? " Choose Resume all folders to start." : " See Activity for progress.");

    private async Task<string> QueueAsync(JsonElement args)
    {
        var operation = Text(args, "operation");
        if (operation is not ("Compress" or "Decompress" or "ClearDefault")) throw new ArgumentException("Unknown operation.");
        var roots = args.TryGetProperty("rootId", out var id) && id.ValueKind == JsonValueKind.String
            ? new[] { Root(args) }
            : Engine.Store.Roots().Where(r => operation != "Compress" || !r.Policy.Paused).ToArray();
        if (roots.Length == 0) return "Add a folder first.";
        var previews = new List<(ManagedFolder Root, JobPreview Preview)>();
        foreach (var root in roots)
        {
            var preview = await Engine.PreviewAsync(root, operation);
            if (preview.Files > 0) previews.Add((root, preview));
        }
        if (previews.Count == 0) return operation switch
        {
            "Decompress" => "ZipXP found no compressed files it can restore.",
            "ClearDefault" => "ZipXP found no folders with Windows compression to turn off.",
            _ => "ZipXP found no files it can compress right now."
        };
        var count = previews.Sum(p => p.Preview.Files);
        var headroom = previews.Max(p => p.Preview.RequiredHeadroom);
        var explanation = operation switch
        {
            "Decompress" => "Files will take their original space again. Automatic compression will be turned off for these folders. No files are deleted. Windows folder compression, if enabled, can still compress new files independently.",
            "ClearDefault" => "Windows will stop compressing new files in these folders by itself. Existing compressed files stay compressed.",
            _ => "ZipXP will compress these files once. Your auto-compression settings stay as they are. Use Restore files to undo."
        };
        var title = operation switch { "Decompress" => "Restore original size", "ClearDefault" => "Turn off Windows compression", _ => "Compress once" };
        var coverage = previews.All(p => p.Preview.Coverage == "Complete primary-stream scan") ? "The scan covered the files within your folder settings." : "Some items were skipped or could not be checked.";
        var scope = previews.Count == 1 ? previews[0].Root.Path : $"{previews.Count} managed folders";
        if (!Confirm($"{scope}\n\n{count} eligible items · {Display.Bytes(previews.Sum(p => p.Preview.LogicalBytes))} in total\nNeeds at least {Display.Bytes(headroom)} free on each affected drive while it works.\n{coverage}\n\n{explanation}\n\nGo ahead?", title)) return "No changes made.";
        var queued = 0;
        foreach (var (root, _) in previews) queued += await Engine.QueueScopeAsync(root.Id, operation);
        return Queued(queued);
    }
}
