using StorageExplorer.Models;
using StorageExplorer.Native;

namespace StorageExplorer.Services;

public sealed record FolderScan(IReadOnlyList<FileEntry> Entries, FileSnapshot Directory, string? Error);

public sealed class FolderScanner(IFileEnvironment? environment = null)
{
    private readonly IFileEnvironment environment = environment ?? new FolderScope();

    public FolderScan Scan(string root, string directory)
    {
        environment.ValidateDirectory(root, directory);
        var folder = environment.Inspect(directory);
        List<FileEntry> entries = [];
        string? scanError = null;
        // Do not ignore inaccessible entries or recurse. Enumeration errors preserve partial results.
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var isDirectory = false;
                try
                {
                    var attributes = File.GetAttributes(path);
                    isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var reason = Eligibility.AttributeReason(attributes);
                    if (reason != null)
                    {
                        entries.Add(new(path, isDirectory, null, $"Skipped: {reason}"));
                        continue;
                    }
                    entries.Add(new(path, isDirectory, environment.Inspect(path)));
                }
                catch (Exception ex) when (ServiceErrors.IsFileError(ex))
                {
                    entries.Add(new(path, isDirectory, null, $"Inspection failed: {ex.Message}. Refresh to retry."));
                }
            }
        }
        catch (Exception ex) when (ServiceErrors.IsFileError(ex))
        {
            scanError = $"Folder listing failed: {ex.Message}. Refresh to retry; listing may be incomplete.";
        }
        return new(entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), folder, scanError);
    }
}

public static class ServiceErrors
{
    public static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
