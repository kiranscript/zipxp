using StorageExplorer.Native;

namespace StorageExplorer.Services;

public interface IFileEnvironment
{
    void ValidateDirectory(string root, string directory);
    void ValidateFilePath(string root, string directory, string path);
    FileSnapshot Inspect(string path);
    long AvailableFreeSpace(string path);
}

public sealed class FolderScope : IFileEnvironment
{
    public static string Normalize(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 11 is required.");
        // Exclude UNC, device namespaces, relative paths, alternate streams and command wildcards.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path[2..].IndexOfAny([':', '*', '?']) >= 0)
            throw new IOException("Choose an ordinary absolute local drive path; UNC and device paths are unsupported.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void ValidateDirectory(string root, string directory)
    {
        root = Normalize(root);
        directory = Normalize(directory);
        if (!IsWithin(directory, root)) throw new IOException("Navigation is limited to the chosen folder.");
        var driveRoot = Path.GetPathRoot(directory)!;
        if (root.Equals(Path.TrimEndingDirectorySeparator(driveRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a test folder, not a drive root.");
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A ready, local fixed NTFS drive is required.");
        FileMetadata.RequireCompressionVolume(driveRoot);
        foreach (var protectedPath in ProtectedLocations())
            if (IsWithin(directory, protectedPath) || IsWithin(root, protectedPath))
                throw new IOException("Windows and Program Files locations are excluded. Choose a disposable test folder.");

        // Includes the selected root and every ancestor, not just the current directory.
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point ancestor is unsupported: {current.FullName}");
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Directory no longer exists.");
        }
        var exclusion = Eligibility.AttributeReason(File.GetAttributes(directory));
        if (exclusion != null) throw new IOException($"Folder is unsupported: {exclusion}");
    }

    private static IEnumerable<string> ProtectedLocations() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? ""
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath);

    public void ValidateFilePath(string root, string directory, string path)
    {
        ValidateDirectory(root, directory);
        path = Normalize(path);
        if (!string.Equals(Path.GetDirectoryName(path), Normalize(directory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only immediate files in the current directory can be operated on.");
    }

    public FileSnapshot Inspect(string path) => FileMetadata.Read(path);
    public long AvailableFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
}

public static class Eligibility
{
    public const long MinimumCompressionBytes = 64 * 1024;
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const long HeadroomBytes = 256L * 1024 * 1024;

    public static string? AttributeReason(FileAttributes attributes)
    {
        (FileAttributes Flag, string Reason)[] exclusions =
        [
            (FileAttributes.ReparsePoint, "Reparse point (including WOF/cloud links)"),
            (FileAttributes.Hidden, "Hidden entry"), (FileAttributes.System, "System entry"),
            (FileAttributes.Encrypted, "Encrypted entry"), (FileAttributes.SparseFile, "Sparse entry"),
            (FileAttributes.Offline, "Offline entry"), ((FileAttributes)0x00040000, "Recall-on-open entry"),
            ((FileAttributes)0x00400000, "Recall-on-access entry"), (FileAttributes.ReadOnly, "Read-only entry")
        ];
        foreach (var (flag, reason) in exclusions)
            if ((attributes & flag) != 0) return reason;
        return null;
    }

    public static string? ForFile(FileSnapshot snapshot, bool compress)
    {
        var reason = AttributeReason(snapshot.Attributes);
        if (reason != null) return reason;
        if (snapshot.IsDirectory) return "Folder: use the current-folder setting; no recursive jobs";
        if (snapshot.LinkCount != 1) return "File has multiple or unknown hard links";
        if (snapshot.LogicalBytes > MaximumBytes) return "Larger than the alpha limit of 256 MiB";
        if (compress && snapshot.LogicalBytes < MinimumCompressionBytes) return "Smaller than the compression minimum of 64 KiB";
        return null;
    }
}
