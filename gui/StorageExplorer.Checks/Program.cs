using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StorageExplorer.Models;
using StorageExplorer.Native;
using StorageExplorer.Services;

internal static class Program
{
    private static int passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        passed++;
        Console.WriteLine($"PASS: {name}");
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription}; runtime: {Environment.Version}");
            await PolicyChecks();
            if (args.Length == 2 && args[0] == "--fixture") await WindowsChecks(Path.GetFullPath(args[1]));
            else if (args.Length != 0) throw new ArgumentException("Usage: [--fixture <disposable-folder>]");
            Console.WriteLine($"{passed} checks passed. UI/open-in-other-app acceptance remains manual.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static FileSnapshot Sample(string path = "file", long size = 8 * 1024 * 1024,
        long? allocation = 8 * 1024 * 1024, FileAttributes attributes = FileAttributes.Normal) =>
        new(path, size, allocation, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), attributes, 1);

    private static FileEntry Entry(FileSnapshot snapshot) => new(snapshot.FullPath, snapshot.IsDirectory, snapshot);

    private static async Task PolicyChecks()
    {
        Check(Marshal.SizeOf<FileMetadata.FileStandardInfo>() == 24 &&
            Marshal.OffsetOf<FileMetadata.FileStandardInfo>(nameof(FileMetadata.FileStandardInfo.NumberOfLinks)).ToInt32() == 16 &&
            Marshal.OffsetOf<FileMetadata.FileStandardInfo>(nameof(FileMetadata.FileStandardInfo.Directory)).ToInt32() == 21,
            "native FILE_STANDARD_INFO layout");
        var argumentPath = @"C:\test folder\space 日本語.txt";
        var start = CompactRunner.CreateStartInfo(argumentPath, true);
        Check(start.ArgumentList.SequenceEqual(new[] { "/C", "/A", argumentPath }) &&
            !start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError,
            "literal compact arguments and redirected no-shell process");
        Check(CompactRunner.CreateStartInfo(argumentPath, false).ArgumentList[0] == "/U", "decompression argument");
        Check(Eligibility.ForFile(Sample(size: 65535), true) != null && Eligibility.ForFile(Sample(size: 65536), true) == null,
            "64 KiB compression minimum");
        Check(Eligibility.ForFile(Sample(size: 0), false) == null &&
            Eligibility.ForFile(Sample(size: Eligibility.MaximumBytes), true) == null &&
            Eligibility.ForFile(Sample(size: Eligibility.MaximumBytes + 1), false) != null, "decompression and 256 MiB boundaries");
        foreach (var attr in new[] { FileAttributes.ReparsePoint, FileAttributes.Hidden, FileAttributes.System,
            FileAttributes.Encrypted, FileAttributes.SparseFile, FileAttributes.Offline, FileAttributes.ReadOnly,
            (FileAttributes)0x40000, (FileAttributes)0x400000 })
            Check(Eligibility.ForFile(Sample(attributes: attr), true) != null, $"skip {attr}");
        Check(Eligibility.ForFile(Sample() with { LinkCount = 2 }, true) != null, "hard-link exclusion");
        Check(FileEntry.FormatBytes(null) == "—", "unknown allocation is never zero");

        var fs = new FakeEnvironment();
        var runner = new FakeRunner(fs);
        var service = new CompressionService(fs, runner);
        fs.Files["file"] = Sample();
        var selected = Entry(fs.Files["file"]);
        fs.Files["file"] = fs.Files["file"] with { LastWriteTimeUtc = DateTime.UtcNow };
        var result = await service.OperateFileAsync("root", "dir", selected, true);
        Check(result.Outcome == JobOutcome.Skipped && runner.Calls == 0, "changed selection is skipped before execution");
        fs.Files["file"] = Sample();
        fs.FreeBytes = Eligibility.HeadroomBytes - 1;
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Failed && runner.Calls == 0, "compression headroom rejects low space");
        fs.Files["second"] = Sample("second");
        var lowSpaceQueue = await service.RunAsync("root", "dir", [Entry(Sample()), Entry(fs.Files["second"])], true);
        Check(lowSpaceQueue.Completed == 1 && lowSpaceQueue.Failed == 1 && runner.Calls == 0,
            "low space stops remaining compression queue without launching compact");
        fs.FreeBytes = Eligibility.HeadroomBytes;
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Succeeded && result.AllocationDelta > 0, "compression at exact headroom succeeds");
        var calls = runner.Calls;
        result = await service.OperateFileAsync("root", "dir", Entry(fs.Files["file"]), true);
        Check(result.Outcome == JobOutcome.Skipped && result.AllocationDelta == null && runner.Calls == calls, "already-compressed files do not count savings");
        fs.FreeBytes = Eligibility.HeadroomBytes + fs.Files["file"].LogicalBytes - 1;
        result = await service.OperateFileAsync("root", "dir", Entry(fs.Files["file"]), false);
        Check(result.Outcome == JobOutcome.Failed && runner.Calls == calls, "decompression requires logical size plus headroom");
        fs.FreeBytes++;
        result = await service.OperateFileAsync("root", "dir", Entry(fs.Files["file"]), false);
        Check(result.Outcome == JobOutcome.Succeeded && result.AllocationDelta < 0, "decompression reports signed allocation growth");

        fs.FreeBytes = long.MaxValue;
        fs.Files["file"] = Sample();
        runner.AfterOperation = s => s with { LastWriteTimeUtc = s.LastWriteTimeUtc.AddSeconds(1) };
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Succeeded && result.AllocationDelta == null, "concurrent change excludes allocation measurement");
        runner.AfterOperation = s => s with { AllocatedBytes = Sample().AllocatedBytes };
        fs.Files["file"] = Sample();
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Succeeded && result.AllocationDelta == 0, "zero savings is valid");
        runner.AfterOperation = s => s with { Attributes = FileAttributes.Normal };
        fs.Files["file"] = Sample();
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Failed, "exit zero without expected attribute fails");
        runner.AfterOperation = s => s;
        runner.ExitCode = 1;
        fs.Files["file"] = Sample();
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Failed && result.Diagnostics!.Contains("exit 1"), "nonzero exit and diagnostics are retained");
        runner.ExitCode = 0;

        fs.Files["bad"] = Sample("bad");
        fs.Files["good"] = Sample("good");
        runner.FailPath = "bad";
        var summary = await service.RunAsync("root", "dir", [Entry(fs.Files["bad"]), Entry(fs.Files["good"])], true);
        Check(summary.Completed == 2 && summary.Failed == 1 && summary.Succeeded == 1, "per-file failure does not abort the queue");
        fs.Files["first"] = Sample("first");
        fs.Files["second"] = Sample("second");
        using var cancellation = new CancellationTokenSource();
        runner.AfterOperation = s => { cancellation.Cancel(); return s; };
        summary = await service.RunAsync("root", "dir", [Entry(fs.Files["first"]), Entry(fs.Files["second"])], true, cancellationToken: cancellation.Token);
        Check(summary.Completed == 1 && summary.Queued == 2 && !fs.Files["second"].IsCompressed, "cancellation finishes current file and leaves next untouched");
        runner.AfterOperation = s => s;
        fs.RejectPaths = true;
        calls = runner.Calls;
        result = await service.OperateFileAsync("root", "dir", Entry(Sample()), true);
        Check(result.Outcome == JobOutcome.Failed && runner.Calls == calls, "boundary failure prevents process launch");
    }

    private static async Task WindowsChecks(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native fixture checks require Windows.");
        var fs = new FolderScope();
        fs.ValidateDirectory(root, root);
        Check(Path.IsPathFullyQualified(CompactRunner.CreateStartInfo("file", true).FileName), "compact resolved from Windows system directory");
        foreach (var rejected in new[] { Path.GetPathRoot(root)!, @"\\server\share", Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var didReject = false;
            try { fs.ValidateDirectory(rejected, rejected); } catch (Exception ex) when (ServiceErrors.IsFileError(ex)) { didReject = true; }
            Check(didReject, $"reject unsupported root {rejected}");
        }
        var outsideRejected = false;
        try { fs.ValidateFilePath(root, root, Path.Combine(root + "-sibling", "escape.txt")); }
        catch (IOException) { outsideRejected = true; }
        Check(outsideRejected, "sibling-prefix path cannot escape chosen root");
        var scanner = new FolderScanner();
        var initial = scanner.Scan(root, root);
        Check(initial.Error == null && initial.Entries.Any(e => e.Name == "child" && e.IsDirectory), "shallow folder scan");
        Check(!initial.Entries.Any(e => e.Name == "nested.txt") && scanner.Scan(root, Path.Combine(root, "child")).Entries.Any(e => e.Name == "nested.txt"),
            "child navigation performs a separate shallow scan");
        var baselines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
        var service = new CompressionService();
        FileEntry Fresh(string name) => Entry(fs.Inspect(Path.Combine(root, name)));
        var text = Fresh("repetitive.txt");
        var result = await service.OperateFileAsync(root, root, text, true);
        Check(result.Outcome == JobOutcome.Succeeded && result.After!.IsCompressed && result.AllocationDelta > 0, "real repetitive text compresses and allocation decreases");
        Console.WriteLine($"MEASURED: repetitive primary-stream allocation reduction {result.AllocationDelta} bytes");
        Check(Hash(text.FullPath) == baselines[text.FullPath], "compressed file reads original content without extraction");
        result = await service.OperateFileAsync(root, root, Fresh("random.bin"), true);
        Check(result.Outcome == JobOutcome.Succeeded && result.AllocationDelta.HasValue, "random data reports a measurement without a savings promise");
        Console.WriteLine($"MEASURED: random primary-stream allocation reduction {result.AllocationDelta} bytes");
        var unicode = initial.Entries.Single(e => e.Name.StartsWith("spaces ", StringComparison.Ordinal));
        result = await service.OperateFileAsync(root, root, unicode, true);
        Check(result.Outcome == JobOutcome.Succeeded && result.After!.IsCompressed, "real spaces and Unicode path operates intact");
        result = await service.OperateFileAsync(root, root, Fresh("small.txt"), true);
        Check(result.Outcome == JobOutcome.Skipped && result.Message.Contains("64 KiB"), "real small file skipped with reason");

        var locked = Fresh("locked.txt");
        var other = Fresh("after-lock.txt");
        using (var exclusive = new FileStream(locked.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await service.RunAsync(root, root, [locked, other], true);
            Check(report.Failed == 1 && report.Succeeded == 1, "real exclusive lock fails visibly while next file completes");
        }

        var defaultBefore = fs.Inspect(root).IsCompressed;
        var childBefore = fs.Inspect(Path.Combine(root, "child")).IsCompressed;
        Check(!defaultBefore && !childBefore, "fixtures start with directory defaults disabled");
        result = await service.SetDirectoryDefaultAsync(root, root, true);
        Check(result.Outcome == JobOutcome.Succeeded && result.After!.IsCompressed, "directory default enabled via app service");
        var inherited = Path.Combine(root, "created-while-enabled.txt");
        File.WriteAllText(inherited, new string('x', 1024 * 1024));
        Check(fs.Inspect(inherited).IsCompressed, "new file inherits native compression without any watcher");
        var newChild = Path.Combine(root, "created-while-enabled");
        Directory.CreateDirectory(newChild);
        Check(fs.Inspect(newChild).IsCompressed, "new subfolder inherits compression");
        result = await service.SetDirectoryDefaultAsync(root, root, false);
        Check(result.Outcome == JobOutcome.Succeeded && !result.After!.IsCompressed, "directory default disabled via app service");
        var notInherited = Path.Combine(root, "created-while-disabled.txt");
        File.WriteAllText(notInherited, new string('x', 1024 * 1024));
        Check(!fs.Inspect(notInherited).IsCompressed && fs.Inspect(inherited).IsCompressed && fs.Inspect(text.FullPath).IsCompressed,
            "disabling default leaves existing compressed files unchanged");
        Check(fs.Inspect(Path.Combine(root, "child")).IsCompressed == childBefore && fs.Inspect(newChild).IsCompressed,
            "both toggles leave existing child directory defaults unchanged");
        result = await service.OperateFileAsync(root, root, Fresh("repetitive.txt"), false);
        Check(result.Outcome == JobOutcome.Succeeded && !result.After!.IsCompressed && Hash(text.FullPath) == baselines[text.FullPath],
            "real decompression clears attribute and preserves SHA-256");
        Check(baselines.All(pair => Hash(pair.Key) == pair.Value), "all original fixture SHA-256 hashes preserved after complete cycle");

        var readOnly = Path.Combine(root, "readonly.txt");
        File.WriteAllText(readOnly, new string('a', 65536));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try { Check(scanner.Scan(root, root).Entries.Single(e => e.FullPath == readOnly).Result.Contains("Read-only"), "unsupported real entry has visible reason"); }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class FakeEnvironment : IFileEnvironment
    {
        public Dictionary<string, FileSnapshot> Files { get; } = [];
        public long FreeBytes { get; set; } = long.MaxValue;
        public bool RejectPaths { get; set; }
        public void ValidateDirectory(string root, string directory) { if (RejectPaths) throw new IOException("Rejected boundary"); }
        public void ValidateFilePath(string root, string directory, string path) => ValidateDirectory(root, directory);
        public FileSnapshot Inspect(string path) => Files.TryGetValue(path, out var value) ? value : throw new FileNotFoundException(path);
        public long AvailableFreeSpace(string path) => FreeBytes;
    }

    private sealed class FakeRunner(FakeEnvironment environment) : ICompactRunner
    {
        public int Calls { get; private set; }
        public string? FailPath { get; set; }
        public int ExitCode { get; set; }
        public Func<FileSnapshot, FileSnapshot> AfterOperation { get; set; } = s => s;
        public Task<CompactResult> RunAsync(string path, bool compress)
        {
            Calls++;
            if (path == FailPath) throw new IOException("Simulated exclusive lock");
            var before = environment.Files[path];
            environment.Files[path] = AfterOperation(before with
            {
                Attributes = compress ? before.Attributes | FileAttributes.Compressed : before.Attributes & ~FileAttributes.Compressed,
                AllocatedBytes = compress ? before.LogicalBytes / 2 : before.LogicalBytes
            });
            return Task.FromResult(new CompactResult(ExitCode, "localized diagnostic output", ""));
        }
    }
}
