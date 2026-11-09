using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StorageManager.Core;
using StorageManager.Core.Native;
using StorageManager.Core.Persistence;
using StorageManager.Core.Services;

internal static class Program
{
    private static int passed;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string alias, string existing, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint length, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    private static void Junction(string alias, string target)
    {
        Directory.CreateDirectory(alias);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(data, 16); print.CopyTo(data, 16 + substitute.Length + 2);
        using var handle = NativeFiles.Open(alias, true);
        if (!DeviceIoControl(handle, 0x900A4, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message); passed++;
    }
    private static async Task WaitUntil(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { Check(true, message); return; }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Timed out: " + message);
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Text(string path, int bytes = 1024 * 1024) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, new string('a', bytes)); }
    private static WorkItem Job(ManagedFolder root, FileRecord file, string operation) => new(Guid.NewGuid().ToString("N"), root.Id, file, operation, false, "Queued", 0, DateTime.UtcNow, DateTime.UtcNow);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--seed-ui")
        {
            var data = Path.GetFullPath(args[1]);
            var demo = Path.Combine(data, "demo-fixture"); Directory.CreateDirectory(demo);
            Text(Path.Combine(demo, "analyze-only.txt"));
            using var seedStore = new Store(Path.Combine(data, "storage.sqlite3"));
            await using var seedEngine = new Engine(seedStore);
            var root = await seedEngine.AddAsync(demo);
            await seedEngine.SavePolicyAsync(root, root.Policy with { QuietSeconds = 0 });
            new Analyzer(seedStore).Scan(seedStore.Roots().Single());
            return 0;
        }
        var run = Path.Combine(Path.GetTempPath(), "ZipXP-Manager-checks-" + Guid.NewGuid().ToString("N"));
        var fixture = Path.Combine(run, "fixture");
        var state = Path.Combine(run, "state", "checks.sqlite3");
        Directory.CreateDirectory(fixture);
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}; runtime: {Environment.Version}");
        Console.WriteLine("Disposable run folder: " + run);
        try
        {
            await Run(fixture, state);
            Console.WriteLine($"{passed} checks passed. Fixtures retained: {run}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Run(string fixture, string state)
    {
        Check(Marshal.SizeOf<NativeFiles.StandardInfo>() == 24 && Marshal.OffsetOf<NativeFiles.StandardInfo>(nameof(NativeFiles.StandardInfo.Directory)).ToInt32() == 21, "correct native standard-info layout");
        var policy = new FolderPolicy { QuietSeconds = 0, CooldownSeconds = 0, RunOnBattery = true };
        policy.Validate();
        Check(!Rules.Excluded("hello.txt", policy) && Rules.Excluded("node_modules/package/a.txt", policy) && Rules.Excluded("nested/photo.jpg", policy), "policy exclusions match filenames and subtrees");
        Check((policy with { StartHour = 22, EndHour = 6 }).InSchedule(new DateTime(2026, 1, 1, 23, 0, 0)) &&
            !(policy with { StartHour = 22, EndHour = 6 }).InSchedule(new DateTime(2026, 1, 1, 12, 0, 0)), "overnight schedule");
        var invalid = false;
        try { (policy with { MaximumBytes = 512L * 1024 * 1024 }).Validate(); } catch (ArgumentException) { invalid = true; }
        Check(invalid && Display.Bytes(null) == "—", "tested size bound and unknown accounting");
        Text(Path.Combine(fixture, "text.txt"), 4 * 1024 * 1024);
        Text(Path.Combine(fixture, "spaces 日本語 café.txt"));
        Text(Path.Combine(fixture, "locked.txt"));
        Text(Path.Combine(fixture, "child", "nested.txt"));
        Directory.CreateDirectory(Path.Combine(fixture, "empty"));
        Text(Path.Combine(fixture, "node_modules", "excluded.txt"));
        File.WriteAllText(Path.Combine(fixture, "small.txt"), "small");
        File.WriteAllBytes(Path.Combine(fixture, "random.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        Text(Path.Combine(fixture, "readonly.txt")); File.SetAttributes(Path.Combine(fixture, "readonly.txt"), FileAttributes.ReadOnly);
        Text(Path.Combine(fixture, "hardlink.txt"));
        Check(CreateHardLinkW(Path.Combine(fixture, "hardlink-alias.txt"), Path.Combine(fixture, "hardlink.txt"), IntPtr.Zero), "create actual NTFS hard link fixture");
        var baseline = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);

        using var store = new Store(state);
        var battery = false; var busy = false; long available = long.MaxValue;
        var options = new EngineOptions(2, 100, 0, 50);
        await using var engine = new Engine(store, options, onBattery: () => battery, freeSpace: _ => available, resourceBusy: () => busy);
        var root = await engine.AddAsync(fixture);
        await engine.SavePolicyAsync(root, policy); root = store.Roots().Single();
        Check(!root.Policy.Enabled, "adding a root is analysis-only");
        var duplicate = false;
        try { await engine.AddAsync(Path.Combine(fixture, "child")); } catch (IOException) { duplicate = true; }
        Check(duplicate, "overlapping managed roots rejected");
        foreach (var unsupported in new[] { Path.GetPathRoot(fixture)!, @"\\server\share", Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var rejected = false;
            try { Scope.Validate(unsupported, unsupported); } catch (Exception ex) when (Scope.FileError(ex)) { rejected = true; }
            Check(rejected, "unsupported scope rejected: " + unsupported);
        }
        await engine.CycleAsync();
        Check(!NativeFiles.Read(Path.Combine(fixture, "text.txt")).Compressed && store.Jobs().Count == 0, "analysis performs no file mutation");
        var observations = store.Observations(root.Id);
        Check(observations.Any(o => o.Path.EndsWith("nested.txt")) && !observations.Any(o => o.Path.EndsWith("excluded.txt")), "recursive analysis respects excluded subtrees");
        Check(observations.Single(o => o.Path.EndsWith("small.txt")).Reason.Contains("minimum") && observations.Single(o => o.Path.EndsWith("readonly.txt")).Reason == "Read-only", "small and read-only reasons visible");
        Check(observations.Where(o => o.Path.Contains("hardlink")).All(o => o.Reason.Contains("hard links")), "both hard-link names excluded from mutation");
        var uniqueLogical = observations.Where(o => o.File is { Directory: false }).Select(o => o.File!).DistinctBy(f => f.Identity).Sum(f => f.Logical);
        Check(store.Scans(root.Id)[0].Logical == uniqueLogical, "scan totals deduplicate hard-linked data");
        Check(observations.Single(o => o.Path.EndsWith("random.bin")).Reason.Contains("low estimated benefit"), "poor-benefit sampling cached until content changes");
        await engine.SavePolicyAsync(root, root.Policy with { MinimumSavingBytes = 0 });
        var relaxedRoot = store.Roots().Single();
        Check(new Analyzer(store).Scan(relaxedRoot).Entries.Single(o => o.Path.EndsWith("random.bin")).Candidate,
            "editing the benefit policy re-evaluates cached low-benefit decisions");
        await engine.SavePolicyAsync(relaxedRoot, policy); root = store.Roots().Single();
        var source = NativeFiles.Read(Path.Combine(fixture, "text.txt"));
        var result = new Mutator().Execute(root, Job(root, source, "Compress"));
        Check(result.State == "Succeeded" && result.After!.Compressed && result.Delta > 0 && Hash(source.Path) == baseline[source.Path], "handle-based compression reduces allocation and preserves contents");
        Console.WriteLine("MEASURED text reduction: " + result.Delta + " bytes");
        var completed = Job(root, source, "Compress") with { State = "Succeeded", Delta = result.Delta, FinishedUtc = DateTime.UtcNow };
        store.SaveJob(completed); store.SaveJob(completed);
        Check(store.MeasuredSince(DateTime.MinValue).Delta == result.Delta && store.MeasuredSince(DateTime.MinValue).Measured == 1, "measurement ledger is exactly once per job");
        var unchanged = new Mutator().Execute(root, Job(root, result.After!, "Compress"));
        Check(unchanged.State == "Unchanged" && unchanged.Delta == null, "previously compressed files do not claim new savings");
        var low = new Mutator(_ => policy.ReserveBytes + source.Logical - 1).Execute(root, Job(root, result.After!, "Decompress"));
        Check(low.Retryable && low.Message.Contains("Low free space"), "decompression requires full logical size plus reserve");
        var decompressed = new Mutator().Execute(root, Job(root, result.After!, "Decompress"));
        Check(decompressed.State == "Succeeded" && !decompressed.After!.Compressed && decompressed.Delta < 0, "decompression reports signed allocation growth");
        store.SaveJob(Job(root, result.After!, "Decompress") with { State = "Succeeded", Delta = decompressed.Delta, FinishedUtc = DateTime.UtcNow });
        Check(store.MeasuredSince(DateTime.MinValue).Delta == 0, "compression followed by decompression has zero net measured reduction");

        var locked = NativeFiles.Read(Path.Combine(fixture, "locked.txt"));
        using (var exclusive = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = new Mutator().Execute(root, Job(root, locked, "Compress"));
            Check(failure.Retryable, "exclusive lock produces a retryable failure");
        }
        var unicode = NativeFiles.Read(Path.Combine(fixture, "spaces 日本語 café.txt"));
        Check(new Mutator().Execute(root, Job(root, unicode, "Compress")).State == "Succeeded", "spaces and Unicode path handled intact");
        var replacePath = Path.Combine(fixture, "replacement.txt"); Text(replacePath);
        var old = NativeFiles.Read(replacePath); File.Move(replacePath, replacePath + ".old"); Text(replacePath);
        var replaced = new Mutator().Execute(root, Job(root, old, "Compress"));
        Check(replaced.State == "Skipped" && !NativeFiles.Read(replacePath).Compressed, "replacement at the same path is not mutated");
        var changed = NativeFiles.Read(replacePath); File.AppendAllText(replacePath, "changed");
        Check(new Mutator().Execute(root, Job(root, changed, "Compress")).State == "Skipped", "changed contents invalidate queued snapshot");
        var outOfScope = new Mutator().Execute(root, Job(root, source with { Path = Path.Combine(fixture + "-sibling", "file.txt") }, "Compress"));
        Check(outOfScope.State != "Succeeded", "sibling-prefix path cannot escape managed root");

        var linkPath = Path.Combine(fixture, "reparse-child");
        Junction(linkPath, Path.Combine(fixture, "child"));
        {
            var scan = new Analyzer(store).Scan(root);
            Check(scan.Entries.Single(o => o.Path == linkPath).Reason.Contains("Reparse"), "reparse-point child not followed");
            var rejectsLink = false;
            try { Scope.Validate(linkPath, linkPath); } catch (IOException) { rejectsLink = true; }
            Check(rejectsLink, "reparse-point root rejected");
        }
        var quietFile = Path.Combine(fixture, "quiet-period.txt"); Text(quietFile);
        var quietRoot = root with { Policy = root.Policy with { QuietSeconds = 60 } };
        var quietResult = new Mutator().Execute(quietRoot, Job(quietRoot, NativeFiles.Read(quietFile), "Compress"));
        Check(quietResult.State == "Skipped" && quietResult.Message.Contains("quiet period"), "freshly written files wait for a quiet period");

        // Keep work queued while exercising pause/schedule/battery/load/space gates.
        await engine.QueueScopeAsync(root.Id, "Compress");
        Check(store.Jobs().Any(j => j.Pending), "recursive explicit job enqueues candidates");
        engine.Pause(true);
        for (var i = 0; i < 3; i++) await engine.CycleAsync();
        Check(!store.Jobs().Any(j => j.State == "Running") && !NativeFiles.Read(source.Path).Compressed, "global pause stops new mutations while analysis remains read-only");
        engine.Pause(false);
        await engine.SetFolderPausedAsync(root.Id, true);
        for (var i = 0; i < 2; i++) await engine.CycleAsync();
        Check(engine.FolderStatus(root.Id) == "Folder paused", "folder pause exposes waiting reason");
        await engine.SetFolderPausedAsync(root.Id, false);
        root = store.Roots().Single();
        var now = DateTime.Now.Hour;
        await engine.SavePolicyAsync(root, root.Policy with { Enabled = true, StartHour = (now + 1) % 24, EndHour = (now + 2) % 24 });
        for (var i = 0; i < 3; i++) await engine.CycleAsync();
        Check(engine.FolderStatus(root.Id) == "Outside schedule", "schedule prevents work and exposes reason");
        root = store.Roots().Single();
        await engine.SavePolicyAsync(root, root.Policy with { StartHour = 0, EndHour = 0, RunOnBattery = false });
        battery = true;
        for (var i = 0; i < 3; i++) await engine.CycleAsync();
        Check(engine.FolderStatus(root.Id) == "On battery", "battery policy prevents work");
        battery = false; busy = true;
        await engine.CycleAsync(); Check(engine.FolderStatus(root.Id) == "High system CPU load", "busy system prevents new file operations");
        busy = false; available = policy.ReserveBytes - 1;
        await engine.CycleAsync(); Check(engine.FolderStatus(root.Id) == "Low free space", "low-space policy pauses without filling disk");
        available = long.MaxValue;

        var beforeStop = store.Jobs().Count(j => j.State == "Succeeded");
        var stopped = false;
        void StopOnCurrent()
        {
            if (stopped || !store.Jobs().Any(j => j.State == "Running")) return;
            stopped = true; engine.Pause(true);
        }
        engine.Changed += StopOnCurrent;
        for (var i = 0; i < 5 && !stopped; i++) await engine.CycleAsync();
        engine.Changed -= StopOnCurrent;
        for (var i = 0; i < 2; i++) await engine.CycleAsync();
        Check(stopped && store.Jobs().Count(j => j.State == "Succeeded") == beforeStop + 1 && store.Jobs().Any(j => j.Pending), "stop-after-current completes exactly one file and leaves remaining jobs queued");
        engine.Pause(false);

        engine.Start();
        await WaitUntil(() => NativeFiles.Read(source.Path).Compressed, "automatic policy compresses existing files");
        var newPath = Path.Combine(fixture, "created-while-running.txt"); Text(newPath);
        await WaitUntil(() => NativeFiles.Read(newPath).Compressed, "watcher/reconciliation automatically compresses a newly created file");
        var copiedPath = Path.Combine(fixture, "copied-in.txt");
        var incoming = Path.Combine(Path.GetDirectoryName(fixture)!, "incoming.txt"); Text(incoming);
        File.Copy(incoming, copiedPath);
        await WaitUntil(() => NativeFiles.Read(copiedPath).Compressed, "copied-in file is automatically compressed");
        Text(incoming); var incomingHash = Hash(incoming);
        File.Move(incoming, copiedPath, true);
        await WaitUntil(() => NativeFiles.Read(copiedPath).Compressed, "atomic replacement is rediscovered as a new identity and compressed");
        Check(Hash(copiedPath) == incomingHash, "atomic replacement contents are preserved");
        await WaitUntil(() => !store.Jobs().Any(j => j.Pending), "bounded worker finishes its queue without loops");
        Check(baseline.All(pair => Hash(pair.Key) == pair.Value), "all original SHA-256 hashes preserved through automatic cycle");
        var directory = NativeFiles.Read(fixture);
        using (var handle = NativeFiles.Open(fixture, true)) NativeFiles.SetCompression(handle, true);
        var inherited = Path.Combine(fixture, "native-inherited.txt"); Text(inherited);
        Check(NativeFiles.Read(inherited).Compressed, "native directory inheritance remains an independent OS mechanism");
        await engine.QueueScopeAsync(root.Id, "ClearDefault");
        await WaitUntil(() => !NativeFiles.Read(fixture).Compressed, "explicit directory-default clearing uses a separate job");
        Check(NativeFiles.Read(inherited).Compressed, "clearing a default does not decompress existing files");
        store.Remember(root.Id, NativeFiles.Read(source.Path).Fingerprint, "Permanent: poor compression result until content changes");
        await engine.QueueScopeAsync(root.Id, "Decompress");
        Check(!store.Roots().Single().Policy.Enabled, "recursive decompression disables automation before queuing work");
        await WaitUntil(() => !store.Jobs().Any(j => j.Pending), "recursive decompression completes");
        Check(!NativeFiles.Read(newPath).Compressed && Hash(source.Path) == baseline[source.Path], "decompressed files stay uncompressed with original contents");
        Check(!NativeFiles.Read(source.Path).Compressed, "a cached compression decision never prevents explicit decompression");
        await engine.RemoveAsync(root.Id);
        Check(store.Roots().Count == 0 && File.Exists(source.Path), "stop-managing preserves files and removes active scope");

        // Separate worker lifetime: no UI and no watcher exists while the offline file is created.
        await RestartChecks(Path.Combine(Path.GetDirectoryName(fixture)!, "restart"));
        QueueBounds(Path.Combine(Path.GetDirectoryName(fixture)!, "queue-bounds.sqlite3"));
    }

    private static void QueueBounds(string path)
    {
        using var store = new Store(path);
        var root = new ManagedFolder("bounded", @"C:\disposable-queue-model", new());
        store.SaveRoot(root);
        var sample = new FileRecord(@"C:\disposable-queue-model\file.txt", "fake:0", 65536, 65536, DateTime.UtcNow.ToFileTimeUtc(), FileAttributes.Normal, 1);
        Check(store.Enqueue(root, sample, "Compress", false) && !store.Enqueue(root, sample, "Compress", false), "persistent queue deduplicates the same file identity");
        for (var i = 1; i <= Store.QueueLimit; i++) store.Enqueue(root, sample with { Identity = "fake:" + i, Path = root.Path + "\\" + i }, "Compress", false);
        Check(store.Jobs().Count(j => j.Pending) == Store.QueueLimit, "persistent queue enforces its 2,000-job bound without touching fake file paths");
    }

    private static async Task RestartChecks(string run)
    {
        var folder = Path.Combine(run, "fixture"); Directory.CreateDirectory(folder);
        var database = Path.Combine(run, "state", "jobs.sqlite3");
        string rootId;
        long? measured;
        using (var store = new Store(database))
        {
            await using var worker = new Engine(store, new EngineOptions(1, 50, 0, 50), onBattery: () => false, resourceBusy: () => false);
            var root = await worker.AddAsync(folder); rootId = root.Id;
            await worker.SavePolicyAsync(root, root.Policy with { Enabled = true, QuietSeconds = 0, CooldownSeconds = 0, RunOnBattery = true });
            Text(Path.Combine(folder, "before-restart.txt")); worker.Start();
            await WaitUntil(() => NativeFiles.Read(Path.Combine(folder, "before-restart.txt")).Compressed, "first worker lifetime processes a file");
            await WaitUntil(() => !store.Jobs().Any(j => j.Pending), "first worker has persisted its completed state");
            var originalName = Path.Combine(folder, "rename-before-queue.txt"); Text(originalName);
            worker.Pause(true);
            var beforeRename = NativeFiles.Read(originalName);
            store.Enqueue(store.Roots().Single(), beforeRename, "Compress", false);
            var renamedPath = Path.Combine(folder, "renamed-after-queue.txt"); File.Move(originalName, renamedPath);
            worker.RequestScan(root.Id);
            worker.Pause(false);
            await WaitUntil(() => NativeFiles.Read(renamedPath).Compressed, "pending work reconciles a renamed file by stable identity");
            await WaitUntil(() => !store.Jobs().Any(j => j.Pending), "renamed-file job is persisted before restart");
            measured = store.MeasuredSince(DateTime.MinValue).Delta;
        }
        Text(Path.Combine(folder, "created-while-offline.txt"));
        using (var store = new Store(database))
        {
            Check(store.Roots().Single().Id == rootId && store.MeasuredSince(DateTime.MinValue).Delta == measured, "root settings and measured ledger survive restart without double counting");
            var root = store.Roots().Single();
            var existing = NativeFiles.Read(Path.Combine(folder, "before-restart.txt"));
            store.SaveJob(Job(root, existing, "Compress") with { State = "Running" });
        }
        using (var store = new Store(database))
        {
            Check(store.Jobs().Any(j => j.State == "Retry" && j.Message.Contains("Interrupted")), "interrupted running job becomes a reconcile-before-retry record");
            await using var worker = new Engine(store, new EngineOptions(1, 50, 0, 50), onBattery: () => false, resourceBusy: () => false);
            worker.Start();
            await WaitUntil(() => NativeFiles.Read(Path.Combine(folder, "created-while-offline.txt")).Compressed, "restart reconciliation catches files created while worker was stopped");
            await WaitUntil(() => !store.Jobs().Any(j => j.Pending), "restarted queue drains");
            Check(store.Jobs().Any(j => j.State == "Unchanged") && store.MeasuredSince(DateTime.MinValue).Measured == 3,
                "already-compressed interrupted job claims no recovered savings");
            worker.Pause(true);
            var lockedPath = Path.Combine(folder, "worker-locked.txt"); var nextPath = Path.Combine(folder, "worker-after-lock.txt");
            Text(lockedPath); Text(nextPath);
            var root = store.Roots().Single();
            var lockedRecord = NativeFiles.Read(lockedPath); var nextRecord = NativeFiles.Read(nextPath);
            using (var exclusive = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                store.Enqueue(root, lockedRecord, "Compress", false); store.Enqueue(root, nextRecord, "Compress", false);
                worker.Pause(false);
                await WaitUntil(() => NativeFiles.Read(nextPath).Compressed, "automatic worker continues to another file after a sharing failure");
                Check(store.Jobs().Any(j => j.Path == lockedPath && j.State == "Retry"), "locked worker job retains retry state and diagnostics");
            }
            var retry = store.Jobs().Single(j => j.Path == lockedPath && j.Pending);
            store.SaveJob(retry with { DueUtc = DateTime.UtcNow });
            await WaitUntil(() => NativeFiles.Read(lockedPath).Compressed, "released lock can complete through persisted retry");
        }
    }
}
