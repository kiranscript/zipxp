using System.Diagnostics;
using StorageExplorer.Models;

namespace StorageExplorer.Services;

public sealed class FileLauncher(IFileEnvironment? environment = null)
{
    private readonly IFileEnvironment environment = environment ?? new FolderScope();

    public void Open(string root, string directory, FileEntry entry)
    {
        environment.ValidateFilePath(root, directory, entry.FullPath);
        var snapshot = environment.Inspect(entry.FullPath);
        var reason = Eligibility.AttributeReason(snapshot.Attributes);
        if (reason != null || snapshot.IsDirectory) throw new IOException(reason ?? "Choose a file to open.");
        // Original path only. Windows transparently reads NTFS compressed data.
        Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true })?.Dispose();
    }
}
