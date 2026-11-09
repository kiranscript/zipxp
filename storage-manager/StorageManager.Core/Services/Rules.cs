using System.IO.Compression;
using System.IO.Enumeration;

namespace StorageManager.Core.Services;

public static class Rules
{
    public static string? AttributeReason(FileAttributes attributes)
    {
        (FileAttributes Flag, string Reason)[] exclusions =
        [ (FileAttributes.ReparsePoint, "Reparse point"), (FileAttributes.Hidden, "Hidden"), (FileAttributes.System, "System"),
          (FileAttributes.Encrypted, "Encrypted"), (FileAttributes.SparseFile, "Sparse"), (FileAttributes.Offline, "Offline"),
          ((FileAttributes)0x40000, "Recall-on-open"), ((FileAttributes)0x400000, "Recall-on-access"), (FileAttributes.ReadOnly, "Read-only") ];
        foreach (var (flag, reason) in exclusions) if ((attributes & flag) != 0) return reason;
        return null;
    }

    public static bool Excluded(string relative, FolderPolicy policy)
    {
        relative = relative.Replace('\\', '/');
        foreach (var pattern in policy.Exclusions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = pattern.Replace('\\', '/').TrimStart('/');
            if (FileSystemName.MatchesSimpleExpression(normalized, relative, true) ||
                (!normalized.Contains('/') && relative.Split('/').Any(part => FileSystemName.MatchesSimpleExpression(normalized, part, true))) ||
                FileSystemName.MatchesSimpleExpression(normalized, relative + "/", true)) return true;
        }
        return false;
    }

    public static string? Eligibility(FileRecord file, FolderPolicy policy, bool compress, DateTime now, bool quiet = true)
    {
        var reason = AttributeReason(file.Attributes);
        if (reason != null) return reason;
        if (file.Directory) return "Directory default only";
        if (file.Links != 1) return "Multiple/unknown hard links";
        if (file.Logical > policy.MaximumBytes) return "Above tested maximum size";
        if (compress && file.Logical < policy.MinimumBytes) return "Below minimum size";
        if (quiet && compress && DateTime.FromFileTimeUtc(file.Written).AddSeconds(policy.QuietSeconds) > now) return "File still changing / quiet period";
        return null;
    }

    public static long Estimate(FileRecord file)
    {
        // Three independent 32 KiB samples: a candidate heuristic, not an NTFS prediction.
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long sampled = 0, packed = 0;
        foreach (var position in new[] { 0L, Math.Max(0, file.Logical / 2 - 16384), Math.Max(0, file.Logical - 32768) }.Distinct())
        {
            stream.Position = position;
            var buffer = new byte[(int)Math.Min(32768, Math.Max(0, file.Logical - position))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, false);
            using var output = new MemoryStream();
            using (var compressor = new DeflateStream(output, CompressionLevel.Fastest, true)) compressor.Write(buffer, 0, read);
            sampled += read; packed += output.Length;
        }
        return sampled == 0 ? 0 : Math.Max(0, (long)(file.Logical * (1.0 - Math.Min(1.0, (double)packed / sampled))));
    }
}
