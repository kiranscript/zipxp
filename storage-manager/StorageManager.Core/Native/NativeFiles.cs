using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StorageManager.Core.Native;

public static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    public struct StandardInfo { public long Allocation, Length; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long Created, Accessed, Written, Changed; public FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AC, Battery, Percent, Reserved; public uint Remaining, Full; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Standard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Basic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Identity(SafeFileHandle handle, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref ushort input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationW(string root, StringBuilder? name, uint count, out uint serial, out uint max, out uint flags, StringBuilder fs, uint capacity);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private static long previousIdle, previousTotal;
    private static DateTime lastCpuSample;
    private static bool busy;

    public static bool SystemBusy()
    {
        if ((DateTime.UtcNow - lastCpuSample).TotalMilliseconds < 500) return busy;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return false;
        var total = kernel + user;
        if (previousTotal > 0 && total > previousTotal) busy = 1.0 - (double)(idle - previousIdle) / (total - previousTotal) > 0.85;
        previousIdle = idle; previousTotal = total; lastCpuSample = DateTime.UtcNow;
        return busy;
    }

    public static bool OnBattery => GetSystemPowerStatus(out var status) && status.AC == 0;

    public static SafeFileHandle Open(string path, bool mutation = false)
    {
        // Refuse writers and renames while mutating. Metadata reads allow ordinary sharing.
        var handle = CreateFileW(path, mutation ? 0xC0000000u : 0u,
            mutation ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, new Win32Exception(error).Message + ": " + path); }
        return handle;
    }

    public static FileRecord Read(string path)
    {
        using var handle = Open(path);
        if (!FinalPath(handle).Equals(path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Resolved metadata path differs; a parent may have changed.");
        return Read(handle, path);
    }

    public static FileRecord Read(SafeFileHandle handle, string path)
    {
        if (!Standard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) ||
            !Basic(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()) ||
            !Identity(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (standard.DeletePending != 0) throw new IOException("Entry is pending deletion.");
        return new(path, $"{id.Volume:X16}:{id.Low:X16}{id.High:X16}", standard.Length,
            standard.Allocation < 0 ? null : standard.Allocation, basic.Written, basic.Attributes, standard.Links);
    }

    public static string FinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = path.ToString();
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    public static void SetCompression(SafeFileHandle handle, bool compress)
    {
        ushort format = compress ? (ushort)1 : (ushort)0;
        if (!DeviceIoControl(handle, 0x9C040, ref format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void RequireVolume(string root)
    {
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationW(root, null, 0, out _, out _, out var flags, fs, 64)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (fs.ToString() != "NTFS" || (flags & 0x10) == 0) throw new IOException("NTFS with file compression support is required.");
    }
}
