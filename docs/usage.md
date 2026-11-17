# Using ZipXP

## Getting started

1. Select **Managed folders → Add folder…**. Choose a local fixed NTFS folder, such as `C:\zipxp-test`. Adding a folder starts read-only analysis. A WSL/UNC folder is not supported.
2. Inspect the overview and storage insights. Folder rows show their policy, known logical/allocation totals, candidate bytes, status, scan time, and native directory-default count. Double-click a folder in Insights to drill down, or choose **Largest files**.
3. Select **Folder settings** and turn on automatic compression. Subfolder inclusion and battery behavior are on the first screen; file exclusions, size/benefit thresholds, quiet period, cooldown, schedule, and free-space reserve are under **Advanced options**.
4. Eligible existing files are queued. New, copied-in, replaced, and changed files are discovered through notifications and periodic scans. The recommended quiet period is 60 seconds; reconciliation runs every two minutes. Busy files retry with backoff. Poor-benefit files stay excluded until their content fingerprint changes or a failed job is explicitly retried.
5. Close the window to keep the tray worker running. Reopen it from the tray. **Start when I sign in** explicitly registers this user's current launch command. **Pause all** / **Pause folder** stops new operations after the current one. **Quit ZipXP** finishes the file in progress, then ends the worker and leaves pending work persisted.
6. Use Activity to search/filter outcomes, copy diagnostics, and retry selected failures. Retry does not re-enable disabled automatic policies.

Adding a folder for analysis does not compress it. **Compress now** is an explicit one-off job; it does not turn on future automation. Each recursive action shows a scope preview first. Ordinary automatic files have no per-file confirmation dialog.

### Decompression and stopping management

- **Restore original size…** (Overview for all folders, Managed folders toolbar per folder, or Storage insights for selected files) uncompresses files so they take their full space again; no files are deleted. It first disables automatic compression for that managed root and cancels pending compression. Re-enabling automation while decompression is pending is rejected.
- Decompression does not clear existing NTFS directory defaults. The preview reports those defaults, incomplete coverage, omitted entries, and the largest sequential operation's required headroom.
- **Turn off Windows' own folder compression** (More menu) is a separate recursive directory-only job within the current policy scope. Existing files stay compressed. Excluded/unreadable/unsupported directories may retain defaults; inspect the next scan.
- **Stop managing this folder** (More menu) removes the root, observations, and future jobs. Existing files and native defaults remain unchanged. Historical job measurements are retained.
- Resuming/pausing the selective worker does not change any native directory inheritance settings. Such defaults can compress future files even while the app is closed and can bypass the selective worker's exclusions and size limits.
