using System.Collections.Concurrent;
using StorageManager.Core.Native;
using StorageManager.Core.Persistence;

namespace StorageManager.Core.Services;

public sealed record EngineOptions(int ReconcileSeconds = 120, int DebounceMilliseconds = 2000, int ScanSpacingSeconds = 15, int FileDelayMilliseconds = 500);
public sealed record JobPreview(int Files, long LogicalBytes, long RequiredHeadroom, int NativeDefaults, string Coverage);

public sealed class Engine : IAsyncDisposable
{
    public Store Store { get; }
    private readonly Analyzer analyzer;
    private readonly Mutator mutator;
    private readonly EngineOptions options;
    private readonly Func<bool> onBattery;
    private readonly Func<bool> resourceBusy;
    private readonly Func<string, long> freeSpace;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, DateTime> dirty = new();
    private readonly Dictionary<string, FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, DateTime> scanned = [];
    private readonly ConcurrentDictionary<string, string> statuses = new();
    private Task? loop;
    private volatile bool paused;
    private volatile string? current;
    public bool Paused => paused;
    public string? CurrentFile => current;
    public string Status { get; private set; } = "Ready";
    public event Action? Changed;

    public Engine(Store store, EngineOptions? options = null, Mutator? mutator = null,
        Func<bool>? onBattery = null, Func<string, long>? freeSpace = null, Func<bool>? resourceBusy = null)
    {
        Store = store; analyzer = new(store); this.options = options ?? new();
        this.freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
        this.mutator = mutator ?? new(this.freeSpace);
        this.onBattery = onBattery ?? (() => NativeFiles.OnBattery);
        this.resourceBusy = resourceBusy ?? NativeFiles.SystemBusy;
        paused = store.Setting("paused") == "true";
    }

    public string FolderStatus(string id) => statuses.GetValueOrDefault(id, "Waiting for scan");
    public void RequestScan(string id) => dirty[id] = DateTime.MinValue;
    public void Pause(bool value)
    {
        paused = value; Store.Setting("paused", value ? "true" : "false");
        Status = value ? "Paused; the current operation will finish" : "Ready";
        Changed?.Invoke();
    }
    public Task<bool> SetPausedAsync(bool value)
    {
        paused = value;
        Status = value ? "Paused; the current operation will finish" : "Ready";
        return Exclusive(() => { Store.Setting("paused", value ? "true" : "false"); return true; });
    }

    private async Task<T> Exclusive<T>(Func<T> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(action).ConfigureAwait(false); }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    public Task<ManagedFolder> AddAsync(string path) => Exclusive(() =>
    {
        path = Scope.Normalize(path); Scope.Validate(path, path);
        if (Scope.Within(Store.DatabasePath, path)) throw new IOException("The app data directory cannot be inside a managed root.");
        if (Store.Roots().Any(root => Scope.Within(path, root.Path) || Scope.Within(root.Path, path)))
            throw new IOException("This folder overlaps an existing managed folder. Use one root with exclusions.");
        var root = new ManagedFolder(Guid.NewGuid().ToString("N"), path, new());
        Store.SaveRoot(root); RequestScan(root.Id); return root;
    });

    public Task<bool> SavePolicyAsync(ManagedFolder root, FolderPolicy policy) => Exclusive(() =>
    {
        policy.Validate();
        if (!Store.Roots().Any(r => r.Id == root.Id)) throw new IOException("This folder is no longer managed.");
        if (policy.Enabled && Store.ActiveJobs().Any(j => j.RootId == root.Id && j.Pending && j.Operation == "Decompress"))
            throw new IOException("Wait for pending decompression to finish before re-enabling automatic compression.");
        Store.SaveRoot(root with { Policy = policy });
        Store.Cancel(root.Id);
        if (Store.Setting("sweep:" + root.Id) == "Compress") Store.Setting("sweep:" + root.Id, "");
        Store.ForgetDecisions(root.Id);
        RequestScan(root.Id); return true;
    });

    public Task<bool> SetFolderPausedAsync(string id, bool paused) => Exclusive(() =>
    {
        var root = Store.Roots().First(r => r.Id == id);
        Store.SaveRoot(root with { Policy = root.Policy with { Paused = paused } });
        if (!paused) RequestScan(id);
        return true;
    });

    public Task<bool> RemoveAsync(string id) => Exclusive(() =>
    {
        Store.RemoveRoot(id); Store.Setting("sweep:" + id, "");
        if (watchers.Remove(id, out var watcher)) watcher.Dispose();
        dirty.TryRemove(id, out _); statuses.TryRemove(id, out _); return true;
    });

    public Task<JobPreview> PreviewAsync(ManagedFolder root, string operation) => Exclusive(() =>
    {
        var scan = analyzer.Scan(Store.Roots().First(r => r.Id == root.Id), stop.Token);
        var files = operation == "Compress" ? scan.Entries.Where(e => e.Candidate).Select(e => e.File!).ToArray() : Eligible(scan.Entries, root, operation).ToArray();
        return new JobPreview(files.Length + (operation == "ClearDefault" && NativeFiles.Read(root.Path).Compressed ? 1 : 0), files.Sum(f => f.Logical),
            root.Policy.ReserveBytes + (operation == "Decompress" ? files.Select(f => f.Logical).DefaultIfEmpty().Max() : 0),
            scan.Summary.NativeDefaults, scan.Summary.Coverage);
    });

    private static IEnumerable<FileRecord> Eligible(IEnumerable<Observation> entries, ManagedFolder root, string operation) =>
        entries.Where(o => o.File != null && !Rules.Excluded(Path.GetRelativePath(root.Path, o.Path), root.Policy))
            .Select(o => o.File!).Where(f => operation == "ClearDefault"
                ? f.Directory && f.Compressed && Rules.AttributeReason(f.Attributes) == null
                : !f.Directory && f.Compressed == (operation == "Decompress") &&
                    Rules.Eligibility(f, root.Policy, operation == "Compress", DateTime.UtcNow) == null);

    private static string DecisionKey(FileRecord file, string operation) => operation == "Compress" ? file.Fingerprint : file.Fingerprint + ":" + operation;

    public Task<int> QueueScopeAsync(string id, string operation) => Exclusive(() =>
    {
        if (operation is not ("Compress" or "Decompress" or "ClearDefault")) throw new ArgumentException("Unknown operation.");
        var root = Store.Roots().First(r => r.Id == id);
        if (Store.ActiveJobs().Any(j => j.RootId == id && j.Pending && j.Operation != operation && !(operation == "Decompress" && j.Operation == "Compress")))
            throw new IOException("Wait for the current folder operation to finish before starting a different operation.");
        if (operation == "Decompress")
        {
            root = root with { Policy = root.Policy with { Enabled = false } };
            Store.SaveRoot(root); Store.Cancel(id);
        }
        Store.Setting("sweep:" + id, operation);
        var scan = analyzer.Scan(root, stop.Token);
        scanned[id] = DateTime.UtcNow;
        var count = QueueScan(root, scan.Entries, operation);
        RequestScan(id); return count;
    });

    public Task<int> QueueSelectedAsync(string id, IEnumerable<Observation> selected) => Exclusive(() =>
    {
        var root = Store.Roots().First(r => r.Id == id);
        if (Store.ActiveJobs().Any(j => j.RootId == id && j.Pending && j.Operation == "ClearDefault"))
            throw new IOException("Wait for native-default clearing to finish first.");
        root = root with { Policy = root.Policy with { Enabled = false } };
        Store.SaveRoot(root); Store.Cancel(id); Store.Setting("sweep:" + id, "");
        return Eligible(selected.Where(o => o.RootId == id && Scope.Within(o.Path, root.Path)), root, "Decompress").Count(f => Store.Enqueue(root, f, "Decompress", false));
    });

    public Task<int> RetryAsync(IEnumerable<WorkItem> selection) => Exclusive(() =>
    {
        var count = 0;
        foreach (var item in selection.Where(j => j.State is "Failed" or "Skipped"))
        {
            var root = Store.Roots().FirstOrDefault(r => r.Id == item.RootId);
            if (root == null) continue;
            if (item.Automatic && !root.Policy.Enabled) continue;
            if (item.Operation == "Decompress")
            {
                root = root with { Policy = root.Policy with { Enabled = false } };
                Store.SaveRoot(root); Store.Cancel(root.Id);
                if (Store.Setting("sweep:" + root.Id) == "Compress") Store.Setting("sweep:" + root.Id, "");
            }
            Store.Forget(root.Id, item.Expected.Fingerprint);
            Store.Forget(root.Id, DecisionKey(item.Expected, item.Operation));
            FileRecord now;
            try { now = NativeFiles.Read(item.Path); }
            catch (Exception ex) when (Scope.FileError(ex)) { RequestScan(root.Id); continue; }
            if (!now.SameContent(item.Expected)) { RequestScan(root.Id); continue; }
            if (Store.Enqueue(root, now, item.Operation, item.Automatic)) count++;
        }
        return count;
    });

    private int QueueScan(ManagedFolder root, IReadOnlyList<Observation> entries, string operation)
    {
        IEnumerable<FileRecord> candidates = operation == "Compress"
            ? entries.Where(e => e.Candidate).Select(e => e.File!) : Eligible(entries, root, operation);
        if (operation == "ClearDefault")
        {
            var directory = NativeFiles.Read(root.Path);
            if (directory.Compressed) candidates = candidates.Append(directory);
        }
        int count = 0;
        foreach (var file in candidates)
        {
            var decision = Store.Decision(root.Id, DecisionKey(file, operation));
            if (decision.HasValue && decision.Value.Message.StartsWith("Permanent:")) continue;
            if (Store.Enqueue(root, file, operation, false)) count++;
        }
        return count;
    }

    public void Start()
    {
        if (loop != null) return;
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await CycleAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { Status = "Worker error: " + ex.Message; Changed?.Invoke(); }
                try { await Task.Delay(options.FileDelayMilliseconds, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public Task<bool> CycleAsync() => Exclusive(() =>
    {
        stop.Token.ThrowIfCancellationRequested();
        var roots = Store.Roots();
        RefreshWatchers(roots);
        var now = DateTime.UtcNow;
        foreach (var root in roots)
        {
            var last = scanned.GetValueOrDefault(root.Id, DateTime.MinValue);
            if (last.AddSeconds(options.ReconcileSeconds) <= now || (dirty.TryGetValue(root.Id, out var due) && due <= now && (due == DateTime.MinValue || last.AddSeconds(options.ScanSpacingSeconds) <= now)))
            {
                // Remove first so events arriving during a scan are retained for the next pass.
                dirty.TryRemove(root.Id, out _);
                statuses[root.Id] = "Scanning primary streams…"; current = root.Path; Changed?.Invoke();
                try
                {
                    var scan = analyzer.Scan(root, stop.Token);
                    scanned[root.Id] = now;
                    var quietUntil = scan.Entries.Where(e => e.File != null && e.Reason.Contains("quiet period"))
                        .Select(e => DateTime.FromFileTimeUtc(e.File!.Written).AddSeconds(root.Policy.QuietSeconds + 1)).DefaultIfEmpty(DateTime.MaxValue).Min();
                    if (quietUntil != DateTime.MaxValue) dirty.AddOrUpdate(root.Id, quietUntil, (_, existing) => existing < quietUntil ? existing : quietUntil);
                    var sweep = Store.Setting("sweep:" + root.Id);
                    if (!string.IsNullOrEmpty(sweep))
                    {
                        var queued = QueueScan(root, scan.Entries, sweep);
                        if (queued == 0 && !Store.ActiveJobs().Any(j => j.RootId == root.Id && j.Pending && j.Operation == sweep)) Store.Setting("sweep:" + root.Id, "");
                    }
                    else if (root.Policy.Enabled && !root.Policy.Paused)
                        foreach (var entry in scan.Entries.Where(e => e.Candidate)) Store.Enqueue(root, entry.File!, "Compress", true);
                    statuses[root.Id] = scan.Summary.Coverage;
                }
                catch (Exception ex) when (Scope.FileError(ex)) { statuses[root.Id] = "Scan failed: " + ex.Message; scanned[root.Id] = now; }
                finally { current = null; }
                return true;
            }
        }
        if (paused) { Status = "Paused; no new operations start. Native directory inheritance remains active."; return false; }
        foreach (var job in Store.ActiveJobs().Where(j => j.State is "Queued" or "Retry" && j.DueUtc <= now).OrderBy(j => j.CreatedUtc))
        {
            var root = roots.FirstOrDefault(r => r.Id == job.RootId);
            if (root == null) { Store.SaveJob(job with { State = "Skipped", Message = "Folder is no longer managed.", FinishedUtc = now }); continue; }
            if (job.Automatic && !root.Policy.Enabled) { Store.SaveJob(job with { State = "Skipped", Message = "Automation disabled.", FinishedUtc = now }); continue; }
            var waiting = root.Policy.Paused ? "Folder paused" : !root.Policy.InSchedule(DateTime.Now) ? "Outside schedule" : !root.Policy.RunOnBattery && onBattery() ? "On battery" : resourceBusy() ? "High system CPU load" : null;
            if (waiting == null)
            {
                try { if (freeSpace(root.Path) < root.Policy.ReserveBytes + (job.Operation == "Decompress" ? job.Expected.Logical : 0)) waiting = "Low free space"; }
                catch (Exception ex) when (Scope.FileError(ex)) { waiting = "Volume unavailable: " + ex.Message; }
            }
            if (waiting != null) { statuses[root.Id] = waiting; continue; }
            // A rename is reconciled by stable identity. A replacement at the same path is never substituted.
            var observation = Store.Observations(root.Id).FirstOrDefault(o => o.File?.Identity == job.Expected.Identity);
            var executing = observation?.File != null && observation.File.SameContent(job.Expected)
                ? job with { Expected = job.Expected with { Path = observation.Path } } : job;
            current = executing.Path; Status = job.Operation + ": " + current;
            Store.SaveJob(executing with { State = "Running", Attempts = job.Attempts + 1, Message = "In progress; stop waits for this file." }); Changed?.Invoke();
            var result = mutator.Execute(root, executing);
            var retry = result.Retryable && job.Attempts < 2;
            Store.SaveJob(executing with
            {
                State = retry ? "Retry" : result.State == "Retry" ? "Failed" : result.State,
                Attempts = job.Attempts + 1, Message = result.Message,
                DueUtc = now.AddSeconds(Math.Pow(2, job.Attempts) * 10),
                Delta = result.Delta, FinishedUtc = retry ? null : DateTime.UtcNow
            });
            if (!retry)
            {
                var poor = result.State == "Failed" || result.State == "Retry" || result.Delta is <= 0;
                Store.Remember(root.Id, DecisionKey(executing.Expected, job.Operation), poor ? "Permanent: " + result.Message : "Cooldown after operation");
            }
            current = null;
            if (!Store.ActiveJobs().Any(j => j.RootId == root.Id && j.Pending)) scanned[root.Id] = DateTime.MinValue;
            else dirty[root.Id] = now.AddSeconds(options.ScanSpacingSeconds);
            Status = result.Message; statuses[root.Id] = result.Message;
            return true;
        }
        Status = roots.Count == 0 ? "Add a folder for read-only analysis to begin." : "Watching managed folders; reconciliation is active.";
        return false;
    });

    private void RefreshWatchers(IReadOnlyList<ManagedFolder> roots)
    {
        foreach (var root in roots.Where(r => !watchers.ContainsKey(r.Id)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 16384 };
                FileSystemEventHandler changed = (_, _) => dirty[root.Id] = DateTime.UtcNow.AddMilliseconds(options.DebounceMilliseconds);
                watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
                watcher.Renamed += (_, _) => dirty[root.Id] = DateTime.UtcNow.AddMilliseconds(options.DebounceMilliseconds);
                watcher.Error += (_, _) => { dirty[root.Id] = DateTime.MinValue; statuses[root.Id] = "Watcher overflow/error; reconciliation requested."; };
                watcher.EnableRaisingEvents = true; watchers[root.Id] = watcher;
            }
            catch (Exception ex) when (Scope.FileError(ex)) { statuses[root.Id] = "Watcher unavailable; periodic scan remains active: " + ex.Message; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop != null) await loop.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { foreach (var watcher in watchers.Values) watcher.Dispose(); watchers.Clear(); }
        finally { gate.Release(); }
        stop.Dispose(); gate.Dispose();
    }
}
