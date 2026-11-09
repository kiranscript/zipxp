using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StorageExplorer.Native;

public sealed record FileSnapshot(string FullPath, long LogicalBytes, long? AllocatedBytes,
    DateTime LastWriteTimeUtc, FileAttributes Attributes, uint LinkCount)
{
    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
    public bool IsCompressed => Attributes.HasFlag(FileAttributes.Compressed);

    public bool MatchesSelection(FileSnapshot other) =>
        LogicalBytes == other.LogicalBytes && AllocatedBytes == other.AllocatedBytes &&
        LastWriteTimeUtc == other.LastWriteTimeUtc && Attributes == other.Attributes && LinkCount == other.LinkCount;

    public bool HasStableContent(FileSnapshot other) =>
        LogicalBytes == other.LogicalBytes && LastWriteTimeUtc == other.LastWriteTimeUtc;
}

public static class FileMetadata
{
    // BOOLEAN is one byte (not Win32 BOOL). Native structure size is 24 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileStandardInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, uint nameSize,
        out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);

    public static FileSnapshot Read(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Reparse point skipped; its target was not inspected.");
        // Zero desired access: metadata only. Backup semantics allows directory handles.
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError("Cannot open metadata", path);
        if (!GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()))
            throw NativeError("Cannot read allocation/link metadata", path);
        if (info.DeletePending != 0) throw new IOException("Entry is pending deletion.");
        var written = File.GetLastWriteTimeUtc(path);
        return new(path, info.EndOfFile, info.AllocationSize >= 0 ? info.AllocationSize : null,
            written, attributes, info.NumberOfLinks);
    }

    public static void RequireCompressionVolume(string root)
    {
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationW(root, null, 0, out _, out _, out var flags, fileSystem, 64))
            throw NativeError("Cannot inspect volume", root);
        if (!fileSystem.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase) || (flags & 0x10) == 0)
            throw new IOException("Choose a local NTFS volume with file compression support.");
    }

    private static Win32Exception NativeError(string action, string path) =>
        new(Marshal.GetLastWin32Error(), $"{action}: {path}. {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
}
