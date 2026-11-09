using System.ComponentModel;
using StorageManager.Core.Native;

namespace StorageManager.Core.Services;

public static class Scope
{
    public static string Normalize(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path[2..].IndexOfAny([':', '*', '?']) >= 0)
            throw new IOException("Choose an absolute local Windows folder, not a WSL/UNC/device path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + "\\", StringComparison.OrdinalIgnoreCase);

    public static void Validate(string root, string path)
    {
        root = Normalize(root); path = Normalize(path);
        if (!Within(path, root)) throw new IOException("Path is outside the managed folder.");
        var driveRoot = Path.GetPathRoot(root)!;
        if (root.Equals(driveRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Drive roots cannot be managed.");
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") throw new IOException("Choose a ready local fixed NTFS drive.");
        NativeFiles.RequireVolume(driveRoot);
        foreach (var protectedPath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") ?? "" })
            if (protectedPath.Length > 0 && Within(path, protectedPath)) throw new IOException("Windows and Program Files locations are excluded.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? root); directory != null; directory = directory.Parent)
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("A parent directory is a reparse point.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not followed.");
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.Directory) == 0 || Rules.AttributeReason(rootAttributes) != null) throw new IOException("The root folder is unsupported or unavailable.");
    }

    public static bool FileError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
