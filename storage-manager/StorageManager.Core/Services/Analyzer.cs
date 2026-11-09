using StorageManager.Core.Native;
using StorageManager.Core.Persistence;

namespace StorageManager.Core.Services;

public sealed class Analyzer(Store store)
{
    public const int ObservationLimit = 100000;

    public (IReadOnlyList<Observation> Entries, ScanSummary Summary) Scan(ManagedFolder root, CancellationToken token = default)
    {
        root.Policy.Validate();
        Scope.Validate(root.Path, root.Path);
        List<Observation> entries = [];
        HashSet<string> counted = [];
        Stack<string> pending = new(); pending.Push(root.Path);
        long logical = 0, allocated = 0, candidates = 0, estimate = 0;
        int files = 0, unknown = 0, errors = 0, defaults = NativeFiles.Read(root.Path).Compressed ? 1 : 0;
        var complete = true;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Scope.Validate(root.Path, directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count >= ObservationLimit) { complete = false; pending.Clear(); break; }
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        var relative = Path.GetRelativePath(root.Path, path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { entries.Add(new(root.Id, null, path, "Reparse point; not followed")); unknown++; continue; }
                        var file = NativeFiles.Read(path);
                        var excluded = Rules.Excluded(relative, root.Policy);
                        var reason = Rules.AttributeReason(file.Attributes);
                        if (file.Directory)
                        {
                            if (file.Compressed) defaults++;
                            entries.Add(new(root.Id, file, path, excluded ? "Excluded by policy" : reason ?? "Folder"));
                            if (root.Policy.Recursive && !excluded && reason == null) pending.Push(path);
                            else if (reason != null) { unknown++; complete = false; }
                            continue;
                        }
                        files++;
                        if (counted.Add(file.Identity))
                        { logical += file.Logical; if (file.Allocated is long bytes) allocated += bytes; else unknown++; }
                        reason = excluded ? "Excluded by policy" : Rules.Eligibility(file, root.Policy, true, DateTime.UtcNow);
                        if (reason == null && file.Compressed) reason = "Already compressed";
                        var decision = store.Decision(root.Id, file.Fingerprint);
                        if (reason == null && decision.HasValue && (decision.Value.Message.StartsWith("Permanent:") || decision.Value.Time.AddSeconds(root.Policy.CooldownSeconds) > DateTime.UtcNow)) reason = decision.Value.Message;
                        long? saving = null;
                        if (reason == null)
                        {
                            saving = Rules.Estimate(file);
                            var fresh = NativeFiles.Read(path);
                            if (!file.SameContent(fresh)) reason = "Changed during sampling; rescan pending";
                            else if (saving < root.Policy.MinimumSavingBytes)
                            {
                                reason = "Permanent: low estimated benefit until content changes";
                                store.Remember(root.Id, file.Fingerprint, reason);
                            }
                            else { reason = "Candidate"; candidates += file.Logical; estimate += saving.Value; }
                        }
                        entries.Add(new(root.Id, file, path, reason!, saving));
                    }
                    catch (Exception ex) when (Scope.FileError(ex)) { entries.Add(new(root.Id, null, path, "Inspection failed: " + ex.Message)); errors++; unknown++; complete = false; }
                }
            }
            catch (Exception ex) when (Scope.FileError(ex)) { entries.Add(new(root.Id, null, directory, "Listing failed: " + ex.Message)); errors++; complete = false; }
        }
        var summary = new ScanSummary(root.Id, DateTime.UtcNow, logical, allocated, candidates, estimate, files, unknown, errors, complete, defaults,
            entries.Count >= ObservationLimit ? "100,000-entry scan bound reached; narrow the managed root. Coverage incomplete." : "Primary data streams; excludes directory metadata and named streams. Excluded subtrees are outside policy scope.");
        store.SaveScan(root.Id, entries, summary);
        return (entries, summary);
    }
}
