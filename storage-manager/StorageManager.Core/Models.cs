using System.Text.Json.Serialization;

namespace StorageManager.Core;

public sealed record FolderPolicy
{
    public bool Enabled { get; init; }
    public bool Paused { get; init; }
    public bool Recursive { get; init; } = true;
    public long MinimumBytes { get; init; } = 65536;
    public long MaximumBytes { get; init; } = 256L * 1024 * 1024;
    public long MinimumSavingBytes { get; init; } = 16384;
    public int QuietSeconds { get; init; } = 60;
    public int CooldownSeconds { get; init; } = 3600;
    public long ReserveBytes { get; init; } = 256L * 1024 * 1024;
    public bool RunOnBattery { get; init; }
    public int StartHour { get; init; }
    public int EndHour { get; init; }
    public string Exclusions { get; init; } = "*.zip;*.7z;*.mp4;*.jpg;*.png;node_modules/*;.git/*";

    public void Validate()
    {
        if (MinimumBytes < 65536 || MaximumBytes > 256L * 1024 * 1024 || MinimumBytes > MaximumBytes ||
            MinimumSavingBytes < 0 || ReserveBytes < 256L * 1024 * 1024 || QuietSeconds < 0 ||
            CooldownSeconds < 0 || StartHour is < 0 or > 23 || EndHour is < 0 or > 23)
            throw new ArgumentException("Use sizes from 64 KB to 256 MB, at least 256 MB reserve, nonnegative delays, and hours 0–23.");
    }

    public bool InSchedule(DateTime local) => StartHour == EndHour || (StartHour < EndHour
        ? local.Hour >= StartHour && local.Hour < EndHour : local.Hour >= StartHour || local.Hour < EndHour);
}

public sealed record ManagedFolder(string Id, string Path, FolderPolicy Policy)
{
    /// <summary>Last part of the path, for display ("Invoices" for C:\\Work\\Invoices).</summary>
    [JsonIgnore] public string Name { get { var name = System.IO.Path.GetFileName(Path.TrimEnd('\\')); return name.Length > 0 ? name : Path; } }
    [JsonIgnore] public string Parent => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? "";
}
public sealed record FileRecord(string Path, string Identity, long Logical, long? Allocated, long Written,
    FileAttributes Attributes, uint Links)
{
    [JsonIgnore] public bool Directory => (Attributes & FileAttributes.Directory) != 0;
    [JsonIgnore] public bool Compressed => (Attributes & FileAttributes.Compressed) != 0;
    [JsonIgnore] public string Fingerprint => $"{Identity}:{Logical}:{Written}";
    public bool SameContent(FileRecord other) => Identity == other.Identity && Logical == other.Logical && Written == other.Written;
}

public sealed record Observation(string RootId, FileRecord? File, string Path, string Reason, long? EstimatedSaving = null)
{
    [JsonIgnore] public bool Candidate => Reason == "Candidate";
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Kind => File?.Directory == true ? "Folder" : "File";
    [JsonIgnore] public string LogicalDisplay => File?.Directory == true ? "—" : Display.Bytes(File?.Logical);
    [JsonIgnore] public string AllocatedDisplay => File?.Directory == true ? "—" : Display.Bytes(File?.Allocated);
    [JsonIgnore] public string EstimateDisplay => Display.Bytes(EstimatedSaving);
    [JsonIgnore] public string Compression => File == null ? "Unknown" : File.Compressed ? "Compressed" : "Uncompressed";
}

public sealed record ScanSummary(string RootId, DateTime Utc, long Logical, long Allocated, long Candidates,
    long EstimatedSaving, int Files, int Unknown, int Errors, bool Complete, int NativeDefaults, string Message)
{
    [JsonIgnore] public string TimeDisplay => Utc.ToLocalTime().ToString("g");
    [JsonIgnore] public string AllocationDisplay => Display.Bytes(Allocated);
    [JsonIgnore] public string Coverage => Complete && Unknown == 0 ? "Complete primary-stream scan" : $"Partial · {Unknown} unknown · {Errors} errors";
}

public sealed record WorkItem(string Id, string RootId, FileRecord Expected, string Operation,
    bool Automatic, string State, int Attempts, DateTime DueUtc, DateTime CreatedUtc,
    string Message = "", long? Delta = null, DateTime? FinishedUtc = null)
{
    [JsonIgnore] public string Path => Expected.Path;
    [JsonIgnore] public string DeltaDisplay => Display.Bytes(Delta);
    [JsonIgnore] public string TimeDisplay => (FinishedUtc ?? CreatedUtc).ToLocalTime().ToString("g");
    [JsonIgnore] public bool Pending => State is "Queued" or "Retry" or "Running";
}

public sealed record MutationResult(string State, string Message, FileRecord? After = null, long? Delta = null, bool Retryable = false);
public sealed record VolumeUsage(string Name, long Total, long Free, DateTime Utc)
{
    public string Summary => $"{Name}  {Display.Bytes(Free)} free / {Display.Bytes(Total)} total · {Utc.ToLocalTime():t}";
}

public static class Display
{
    public static string Bytes(long? bytes)
    {
        if (bytes == null) return "—";
        double size = bytes.Value;
        // Same convention as Windows Explorer: 1 KB = 1,024 bytes.
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (Math.Abs(size) >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        // 1,013.9 KB reads better as 1.0 MB, as in Explorer.
        if (unit > 0 && unit < units.Length - 1 && Math.Round(Math.Abs(size), 1) >= 1000) { size /= 1024; unit++; }
        if (unit == 0) return $"{size:N0} bytes";
        // About three significant figures, like Explorer: "960 KB", "1 MB", "51.8 GB".
        var text = Math.Abs(size) >= 100 ? size.ToString("N0") : size.ToString("N1");
        if (text.EndsWith(".0", StringComparison.Ordinal)) text = text[..^2];
        return $"{text} {units[unit]}";
    }
}
