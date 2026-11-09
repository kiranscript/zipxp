using System.ComponentModel;
using StorageExplorer.Native;
using StorageExplorer.Services;

namespace StorageExplorer.Models;

public sealed class FileEntry : INotifyPropertyChanged
{
    public string FullPath { get; }
    public string Name => Path.GetFileName(FullPath);
    public FileSnapshot? Snapshot { get; private set; }
    public string? InspectionError { get; }
    public bool IsDirectory { get; }
    public string Kind => IsDirectory ? "Folder" : "File";
    public string LogicalSize => IsDirectory ? "—" : FormatBytes(Snapshot?.LogicalBytes);
    public string AllocatedSize => IsDirectory ? "—" : FormatBytes(Snapshot?.AllocatedBytes);
    public string Compression => Snapshot == null ? "Unknown" : Snapshot.IsCompressed ? "Compressed" : "Uncompressed";
    public string EligibilityReason => InspectionError ?? (Snapshot == null ? "Metadata unavailable" :
        IsDirectory ? Eligibility.AttributeReason(Snapshot.Attributes) ?? "Double-click to enter" :
        Eligibility.ForFile(Snapshot, !Snapshot.IsCompressed) ?? "Eligible");
    private string? result;
    public string Result => result ?? EligibilityReason;
    public JobResult? LastJobResult { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry(string path, bool isDirectory, FileSnapshot? snapshot, string? error = null)
        => (FullPath, IsDirectory, Snapshot, InspectionError) = (path, isDirectory, snapshot, error);

    public void Apply(JobResult value)
    {
        LastJobResult = value;
        if (value.After != null) Snapshot = value.After;
        result = value.Display;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes == null) return "—";
        var value = (double)bytes.Value;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N2} {units[unit]}";
    }
}
