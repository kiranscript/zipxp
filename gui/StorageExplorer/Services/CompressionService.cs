using StorageExplorer.Native;
using StorageExplorer.Models;

namespace StorageExplorer.Services;

public enum JobOutcome { Succeeded, Skipped, Failed }

public sealed record JobResult(string FullPath, JobOutcome Outcome, string Message,
    FileSnapshot? After = null, long? AllocationDelta = null, string? Diagnostics = null, bool StopQueue = false)
{
    public string Display => $"{Outcome}: {Message}" + (string.IsNullOrWhiteSpace(Diagnostics) ? "" : $"\n{Diagnostics}");
}

public sealed record JobProgress(int Completed, int Queued, int Succeeded, int Skipped, int Failed,
    long AllocationDelta, int MeasuredFiles, string? CurrentFile, JobResult? Result = null);

public sealed class CompressionService(IFileEnvironment? environment = null, ICompactRunner? runner = null)
{
    private readonly IFileEnvironment environment = environment ?? new FolderScope();
    private readonly ICompactRunner runner = runner ?? new CompactRunner();

    public async Task<JobProgress> RunAsync(string root, string directory, IReadOnlyList<FileEntry> selection,
        bool compress, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        int completed = 0, succeeded = 0, skipped = 0, failed = 0, measured = 0;
        long delta = 0;
        foreach (var entry in selection)
        {
            if (cancellationToken.IsCancellationRequested) break;
            progress?.Report(new(completed, selection.Count, succeeded, skipped, failed, delta, measured, entry.FullPath));
            var result = await OperateFileAsync(root, directory, entry, compress).ConfigureAwait(false);
            completed++;
            switch (result.Outcome)
            {
                case JobOutcome.Succeeded: succeeded++; break;
                case JobOutcome.Skipped: skipped++; break;
                case JobOutcome.Failed: failed++; break;
            }
            if (result.Outcome == JobOutcome.Succeeded && result.AllocationDelta is long measuredDelta)
            { delta += measuredDelta; measured++; }
            progress?.Report(new(completed, selection.Count, succeeded, skipped, failed, delta, measured, entry.FullPath, result));
            if (result.StopQueue) break;
        }
        return new(completed, selection.Count, succeeded, skipped, failed, delta, measured, null);
    }

    public async Task<JobResult> OperateFileAsync(string root, string directory, FileEntry entry, bool compress)
    {
        FileSnapshot? after = null;
        CompactResult? command = null;
        try
        {
            environment.ValidateFilePath(root, directory, entry.FullPath);
            var before = environment.Inspect(entry.FullPath);
            after = before;
            var reason = Eligibility.ForFile(before, compress);
            if (reason != null) return new(entry.FullPath, JobOutcome.Skipped, reason, before);
            if (entry.Snapshot == null || !entry.Snapshot.MatchesSelection(before))
                return new(entry.FullPath, JobOutcome.Skipped, "Changed or unreadable since selection. Refresh, then select again.", before);
            if (before.IsCompressed == compress)
                return new(entry.FullPath, JobOutcome.Skipped, compress ? "Already compressed; no newly reclaimed bytes." : "Already uncompressed.", before);
            var requiredSpace = Eligibility.HeadroomBytes + (compress ? 0 : before.LogicalBytes);
            if (environment.AvailableFreeSpace(entry.FullPath) < requiredSpace)
                return new(entry.FullPath, JobOutcome.Failed, $"Insufficient free space: requires {FileEntry.FormatBytes(requiredSpace)} headroom.{(compress ? " Remaining compression queue stopped." : "")}", before, StopQueue: compress);
            command = await runner.RunAsync(entry.FullPath, compress).ConfigureAwait(false);
            after = environment.Inspect(entry.FullPath);
            if (command.ExitCode != 0 || after.IsCompressed != compress)
                return new(entry.FullPath, JobOutcome.Failed, "compact failed or the expected filesystem compression attribute was not observed. Refresh and retry.", after, Diagnostics: command.Diagnostics);
            var stable = before.HasStableContent(after);
            long? delta = stable && before.AllocatedBytes.HasValue && after.AllocatedBytes.HasValue
                ? before.AllocatedBytes.Value - after.AllocatedBytes.Value : null;
            var message = delta.HasValue
                ? $"{(compress ? "Compressed" : "Decompressed")}; allocation reduction {FileEntry.FormatBytes(delta)} (signed)."
                : $"State verified; allocation measurement unavailable{(!stable ? " because content changed during the job" : "")}. Refresh to inspect.";
            return new(entry.FullPath, JobOutcome.Succeeded, message, after, delta, command.Diagnostics);
        }
        catch (Exception ex) when (ServiceErrors.IsFileError(ex) || ex is InvalidOperationException)
        {
            return new(entry.FullPath, JobOutcome.Failed, $"{ex.Message} Refresh to retry.", after, Diagnostics: command?.Diagnostics);
        }
    }

    public async Task<JobResult> SetDirectoryDefaultAsync(string root, string directory, bool compress)
    {
        CompactResult? command = null;
        try
        {
            environment.ValidateDirectory(root, directory);
            var before = environment.Inspect(directory);
            if (!before.IsDirectory) throw new IOException("Current path is no longer a directory.");
            var reason = Eligibility.AttributeReason(before.Attributes);
            if (reason != null) throw new IOException(reason);
            if (before.IsCompressed == compress)
                return new(directory, JobOutcome.Skipped, "Directory default already matches.", before);
            if (compress && environment.AvailableFreeSpace(directory) < Eligibility.HeadroomBytes)
                throw new IOException("Insufficient free space: at least 256 MiB headroom is required.");
            command = await runner.RunAsync(directory, compress).ConfigureAwait(false);
            var after = environment.Inspect(directory);
            return new(directory, command.ExitCode == 0 && after.IsCompressed == compress ? JobOutcome.Succeeded : JobOutcome.Failed,
                command.ExitCode == 0 && after.IsCompressed == compress
                    ? "Directory default verified. Existing files and existing subfolders are unchanged."
                    : "Directory default could not be verified. Refresh and retry.", after, Diagnostics: command.Diagnostics);
        }
        catch (Exception ex) when (ServiceErrors.IsFileError(ex) || ex is InvalidOperationException)
        {
            return new(directory, JobOutcome.Failed, ex.Message, Diagnostics: command?.Diagnostics);
        }
    }
}
