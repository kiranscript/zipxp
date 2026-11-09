using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using StorageManager.Core;
using StorageManager.Core.Native;
using StorageManager.Core.Persistence;
using StorageManager.Core.Services;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal sealed record Item(string RelativePath, FileRecord Metadata, string Sha256);
    internal sealed record Capture(DateTime Utc, string Folder, IReadOnlyList<Item> Files,
        IReadOnlyList<FileRecord> Directories, long UniqueLogical, long UniqueAllocated, long FreeBytes,
        bool OnBattery, string OS, string Runtime);

    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Write(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, Json)); }
    private static Capture Load(string path) => JsonSerializer.Deserialize<Capture>(File.ReadAllText(path))!;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "capture":
                {
                    var folder = Scope.Normalize(args[1]); Scope.Validate(folder, folder);
                    List<Item> files = []; List<FileRecord> directories = [NativeFiles.Read(folder)];
                    Stack<string> todo = new(); todo.Push(folder);
                    while (todo.TryPop(out var directory))
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected reparse point; baseline requires explicit review: " + path);
                            var metadata = NativeFiles.Read(path);
                            if (metadata.Directory) { directories.Add(metadata); todo.Push(path); }
                            else files.Add(new(Path.GetRelativePath(folder, path), metadata, Hash(path)));
                        }
                    var unique = files.DistinctBy(f => f.Metadata.Identity).ToArray();
                    var captured = new Capture(DateTime.UtcNow, folder, files, directories, unique.Sum(f => f.Metadata.Logical),
                        unique.Sum(f => f.Metadata.Allocated ?? 0), new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace,
                        NativeFiles.OnBattery, RuntimeInformation.OSDescription, Environment.Version.ToString());
                    Write(args[2], captured);
                    Console.WriteLine($"Captured {files.Count} files, {directories.Count - 1} subdirectories, {files.Count(f => f.Metadata.Compressed)} compressed; unique allocation {captured.UniqueAllocated} bytes.");
                    break;
                }
                case "seed":
                {
                    using var store = new Store(Path.Combine(args[2], "storage.sqlite3"));
                    await using var engine = new Engine(store);
                    var root = await engine.AddAsync(args[1]);
                    var scan = new Analyzer(store).Scan(root);
                    Write(args[3], new { Root = root, Summary = scan.Summary, Entries = scan.Entries });
                    Console.WriteLine($"Analysis only: {scan.Summary.Files} files; {scan.Entries.Count(e => e.Candidate)} candidates; {scan.Summary.Errors} errors; native defaults {scan.Summary.NativeDefaults}.");
                    break;
                }
                case "disable":
                {
                    using var store = new Store(Path.Combine(args[1], "storage.sqlite3"));
                    await using var engine = new Engine(store);
                    foreach (var root in store.Roots()) await engine.SavePolicyAsync(root, root.Policy with { Enabled = false });
                    engine.Pause(true);
                    Console.WriteLine("Disabled automatic policies in the isolated test profile; no file operation started.");
                    break;
                }
                case "read":
                {
                    using var reader = new Reader(Path.Combine(args[1], "storage.sqlite3"));
                    Write(args[2], new
                    {
                        Utc = DateTime.UtcNow,
                        Roots = reader.Data("SELECT data FROM roots").Select(s => JsonSerializer.Deserialize<ManagedFolder>(s)).ToArray(),
                        Jobs = reader.Data("SELECT data FROM jobs ORDER BY created").Select(s => JsonSerializer.Deserialize<WorkItem>(s)).ToArray(),
                        Scans = reader.Data("SELECT data FROM scans ORDER BY id").Select(s => JsonSerializer.Deserialize<ScanSummary>(s)).ToArray(),
                        LedgerNetDelta = long.Parse(reader.Data("SELECT COALESCE(SUM(delta),0) FROM ledger").Single())
                    });
                    break;
                }
                case "repro-allocation":
                {
                    var path = args[1];
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(new byte[65537]);
                    var initial = NativeFiles.Read(path); var originalHash = Hash(path);
                    FileRecord compressOpen, decompressOpen;
                    using (var handle = NativeFiles.Open(path, true)) { NativeFiles.SetCompression(handle, true); compressOpen = NativeFiles.Read(handle, path); }
                    var compressClosed = NativeFiles.Read(path);
                    using (var handle = NativeFiles.Open(path, true)) { NativeFiles.SetCompression(handle, false); decompressOpen = NativeFiles.Read(handle, path); }
                    var decompressClosed = NativeFiles.Read(path);
                    Write(args[2], new { Initial = initial, CompressionHandleOpen = compressOpen, CompressionHandleClosed = compressClosed,
                        DecompressionHandleOpen = decompressOpen, DecompressionHandleClosed = decompressClosed, HashUnchanged = Hash(path) == originalHash });
                    Console.WriteLine($"Decompression allocation: open handle {decompressOpen.Allocated}, after close {decompressClosed.Allocated}, initial {initial.Allocated}. Hash unchanged: {Hash(path) == originalHash}.");
                    break;
                }
                case "probe":
                {
                    var directory = args[1]; Directory.CreateDirectory(directory);
                    // Only the test-created directory: existing defaults elsewhere stay untouched.
                    using (var handle = NativeFiles.Open(directory, true)) NativeFiles.SetCompression(handle, false);
                    var path = Path.Combine(directory, args[2]);
                    if (File.Exists(path)) throw new IOException("Refusing to overwrite a probe.");
                    File.WriteAllText(path, new string('z', 1024 * 1024));
                    if (args.Length > 4 && args[4] == "aged") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-3));
                    Write(args[3], new { CreatedUtc = DateTime.UtcNow, File = NativeFiles.Read(path), Sha256 = Hash(path) });
                    break;
                }
                case "restore":
                {
                    var baseline = Load(args[1]);
                    List<string> restored = [], failures = [];
                    // Validate every original before changing any state.
                    foreach (var file in baseline.Files)
                    {
                        var now = NativeFiles.Read(file.Metadata.Path);
                        if (now.Identity != file.Metadata.Identity || Hash(now.Path) != file.Sha256)
                            throw new IOException("Original identity/content changed: " + file.RelativePath);
                    }
                    foreach (var original in baseline.Files.Select(f => f.Metadata).Concat(baseline.Directories))
                    {
                        try
                        {
                            var now = NativeFiles.Read(original.Path);
                            if (now.Identity != original.Identity) throw new IOException("Identity changed.");
                            if (now.Compressed != original.Compressed)
                            {
                                using var handle = NativeFiles.Open(original.Path, true);
                                NativeFiles.SetCompression(handle, original.Compressed);
                                restored.Add(original.Path);
                            }
                        }
                        catch (Exception ex) { failures.Add(original.Path + ": " + ex.Message); }
                    }
                    foreach (var directory in baseline.Directories)
                        Directory.SetLastWriteTimeUtc(directory.Path, DateTime.FromFileTimeUtc(directory.Written));
                    Write(args[2], new { Utc = DateTime.UtcNow, RestoredCompressionStates = restored, Failures = failures });
                    if (failures.Count > 0) throw new IOException(string.Join("\n", failures));
                    break;
                }
                default: throw new ArgumentException("Use capture, seed, read, probe, or restore; see folder test script.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // Read-only Windows SQLite connection. Opening Store while the app is running would
    // invoke crash recovery, so the evidence collector deliberately does not do that.
    private sealed class Reader : IDisposable
    {
        private IntPtr db;
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
        public Reader(string path) { if (sqlite3_open_v2(path, out db, 1 | 0x10000, IntPtr.Zero) != 0) throw new IOException("Cannot open read-only evidence database."); }
        public List<string> Data(string sql)
        {
            if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw new IOException("Cannot prepare evidence query.");
            try
            {
                List<string> rows = []; int result;
                while ((result = sqlite3_step(stmt)) == 100) rows.Add(Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0))!);
                if (result != 101) throw new IOException("Evidence query did not finish.");
                return rows;
            }
            finally { sqlite3_finalize(stmt); }
        }
        public void Dispose() { sqlite3_close_v2(db); }
    }
}
