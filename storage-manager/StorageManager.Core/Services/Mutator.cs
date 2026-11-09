using System.ComponentModel;
using StorageManager.Core.Native;

namespace StorageManager.Core.Services;

public sealed class Mutator(Func<string, long>? freeSpace = null)
{
    private readonly Func<string, long> freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);

    public MutationResult Execute(ManagedFolder root, WorkItem job)
    {
        try
        {
            Scope.Validate(root.Path, job.Path);
            var relative = Path.GetRelativePath(root.Path, job.Path);
            if (Rules.Excluded(relative, root.Policy) || (!root.Policy.Recursive && !string.Equals(Path.GetDirectoryName(job.Path), root.Path, StringComparison.OrdinalIgnoreCase) && job.Path != root.Path))
                return new("Skipped", "Outside the current policy scope.");
            using var handle = NativeFiles.Open(job.Path, true);
            if (!NativeFiles.FinalPath(handle).Equals(job.Path, StringComparison.OrdinalIgnoreCase))
                return new("Skipped", "Resolved path changed; no mutation performed.");
            var before = NativeFiles.Read(handle, job.Path);
            if (before.Identity != job.Expected.Identity || (!before.Directory && !before.SameContent(job.Expected))) return new("Skipped", "File identity, length or last-write time changed; rescan required.", before);
            var compress = job.Operation == "Compress";
            if (job.Operation == "ClearDefault")
            {
                if (!before.Directory || Rules.AttributeReason(before.Attributes) != null) return new("Skipped", "Unsupported directory default.", before);
            }
            else
            {
                var reason = Rules.Eligibility(before, root.Policy, compress, DateTime.UtcNow);
                if (reason != null) return new("Skipped", reason, before);
            }
            if (before.Compressed == compress) return new("Unchanged", "Actual compression state already matches; no new measurement.", before);
            var required = root.Policy.ReserveBytes + (!compress && !before.Directory ? before.Logical : 0);
            if (freeSpace(job.Path) < required) return new("Retry", $"Low free space; requires {Display.Bytes(required)} headroom.", before, Retryable: true);
            NativeFiles.SetCompression(handle, compress);
            var after = NativeFiles.Read(handle, job.Path);
            if (after.Compressed != compress) return new("Failed", "Expected compression state was not observed.", after);
            long? delta = !before.Directory && before.SameContent(after) && before.Allocated.HasValue && after.Allocated.HasValue
                ? before.Allocated.Value - after.Allocated.Value : null;
            return new("Succeeded", before.Directory ? "Native default cleared; existing file contents unchanged."
                : delta.HasValue ? $"Verified. Signed allocation reduction: {Display.Bytes(delta)}." : "State verified; allocation measurement unavailable.", after, delta);
        }
        catch (Win32Exception ex)
        {
            var retry = ex.NativeErrorCode is 32 or 33 or 112;
            return new(retry ? "Retry" : "Failed", ex.Message, Retryable: retry);
        }
        catch (Exception ex) when (Scope.FileError(ex)) { return new("Failed", ex.Message); }
    }
}
