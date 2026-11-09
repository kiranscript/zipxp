# Windows storage manager: development plan

Updated: 2026-11-09

## Product decision

Build a Windows 11 storage manager whose main job is to keep selected folders using less disk space automatically. The user chooses folders once, enables a policy, and can see what the app is doing and how much allocation actually changed. Files keep their original names, paths, and normal application access.

Automatic compression is a core product feature. The folder browser already implemented in the alpha becomes a way to inspect files and troubleshoot results. The main interface becomes a storage dashboard, managed folders, and background activity. Copy/move/delete tools, tabs, previews, and replacing Explorer are outside this roadmap.

Use ordinary NTFS compression first. The operating system handles compressed reads, including while the app is closed or uninstalled. Selective automation needs the app's worker running; native directory inheritance is a separate setting with different behavior.

## Current state

[The alpha specification](alpha.md) is implemented under [gui/](gui/README.md): shallow browsing, selected-file compression/decompression, an explicit native directory default, original-path opening, cancellation between files, per-file errors, and primary-stream allocation measurements.

The Windows build, actual window startup/shutdown, and 54 policy/service/native checks passed. Hash verification, real compression/decompression, locked-file continuation, Unicode paths, and inheritance were exercised. The user has also reported that the app works. The README still tracks the remaining detailed interactive acceptance checks; this is not production validation.

The repository is in WSL. Development can use the WSL launcher, but runtime and storage tests use Windows and local NTFS folders. WSL filesystem paths are not managed-folder targets.

The alpha does not yet provide recursive managed folders, selective monitoring, persistent jobs, a tray worker, startup integration, schedules, or a storage dashboard. The milestones below add those features.

A separate [storage-manager implementation](storage-manager/README.md) now provides those product features without replacing the alpha. Its [validation record](storage-manager/VALIDATION.md) covers 68 Windows core checks, native dashboard navigation, the WSL launcher, and a self-contained install/uninstall cycle. Signing, the broader compatibility/performance matrix, sign-in lifecycle validation, and private-beta user acceptance remain release gates.

## Intended everyday experience

1. Open the dashboard and choose one or more folders to manage.
2. See an initial scan: space occupied, candidate files, exclusions, estimated benefit, and incomplete/unreadable areas.
3. Enable automatic compression with a visible folder policy. This authorizes processing existing suitable files and monitoring future changes within that scope.
4. Close the main window and let the tray worker continue. With start-at-login enabled, it resumes after the next sign-in.
5. Open the dashboard whenever needed to inspect disk usage, pending work, measured results, and failures. Pause or change a policy at any time.
6. Stop managing a folder or decompress its contents through separate, clearly explained actions.

There are no per-file approval dialogs during an enabled automatic policy. Adding a folder for analysis alone does not authorize compression. Files open through Explorer and normal applications at their original paths.

## Required storage-management features

### Automatic compression

- Process suitable existing files recursively in explicitly managed folders.
- Detect newly created, copied-in, replaced, and modified files. Wait for a quiet period, revalidate them, and compress eligible files without repeated user actions.
- Use filesystem notifications as hints, backed by periodic reconciliation and a rescan after missed events or watcher overflow. A stopped worker catches up on its next start.
- Persist pending work, policy decisions, retry state, and job results. Reconcile actual filesystem state after restart instead of trusting a remembered compression flag.
- Start with one bounded worker. Revalidate file identity, exclusions, freshness, and free space immediately before each operation.
- Retry temporarily busy files with bounded backoff. Do not repeatedly retry permanent exclusions or access-denied failures without a relevant change or user retry.
- Avoid recompression loops: debounce writes, detect the app's own changes, apply cooldowns, and remember poor compression results until the file changes.

### Folder policies

Each managed folder has visible settings for:

- Including subfolders, with explicit excluded child folders and filename/type patterns.
- Minimum file size, a tested maximum file size, minimum estimated benefit, time since last modification, and recompression cooldown.
- Continuous background work or scheduled windows; whether to run on battery; and a minimum free-space reserve.
- Whether automation is enabled, paused, or limited to analysis.

Ship a conservative recommended policy with editable settings and a readable explanation of each decision. Sample several regions of a file to estimate benefit; label that estimate and validate its usefulness against actual outcomes. Extension rules are hints, not proof that content can or cannot compress.

Initially retain the alpha's 64 KiB minimum, 256 MiB maximum, and at least 256 MiB compression headroom. Make the policy model configurable, but enable wider size ranges only after runtime testing. Decompression requires at least full logical file size plus the reserve, and still handles actual OS failures.

Overlapping roots must not create competing policies or duplicate jobs. Initially reject overlapping managed roots and use exclusions within one root. Show a clear error when a requested scope conflicts with an existing one.

### Background operation and controls

- A single per-user worker continues in the system tray when the main window closes. Keep the process alive; a Windows service is not required for the first release.
- Offer explicit start-at-login setup for the current user. It operates after sign-in, not before login. Default to no elevation.
- Provide Pause all, Resume, Stop after current file, Pause this folder, and a distinct Exit action. Exit requests cancellation and waits for the current native operation.
- Closing the main window explains the tray behavior the first time. Pausing stops new compression jobs; it does not decompress anything or disable native directory defaults.
- Honor schedules, battery settings, resource limits, and free-space reserves. Display the reason work is waiting, such as “outside schedule,” “file still changing,” or “low free space.”
- Bound scan concurrency and queue size, and favor a responsive foreground computer over throughput. Define and test the resource policy before claiming a particular CPU/disk cap.

### Storage dashboard and insights

The dashboard answers: “Where is the space going?”, “What can this app reduce?”, and “What is happening now?”

- Show current volume free space and usage, with a timestamp and a clear distinction from managed-folder totals.
- Show each managed folder's logical bytes, allocated primary-stream bytes, candidate bytes, compression state summary, policy, last scan, and automation status.
- Identify largest files and folders within managed scopes. Drill into a folder to see its immediate breakdown, eligibility reasons, and actions.
- Show recent successful, unchanged, skipped, failed, interrupted, and unmeasured operations; pending work; and the current file.
- Keep estimated benefit separate from measured allocation changes. Show signed net measured changes for a selected period, including decompression growth; never count the same completed job again after restart.
- Offer a small history chart based on timestamped scans and job records. Mark gaps, stale scans, and incomplete coverage. Do not present deletion or unrelated volume changes as compression savings.
- Make failed jobs searchable and retryable, with actionable reasons and copyable diagnostics.

Storage analysis is read-only until a compression policy or explicit operation is enabled. Charts and percentages must include their scope and measurement time.

### Reversible management

- Compress now, Decompress selected, and Decompress managed folder are explicit jobs, with scope and progress visible before recursive work begins.
- Stop managing removes future automatic work and queued compression for that scope. Existing compressed files remain compressed and open normally.
- Decompressing a managed scope disables its automatic compression policy before queuing decompression, preventing the worker from immediately reversing the request. Re-enabling the policy is explicit.
- Native directory defaults are inspected and reported separately. Turning off a default does not decompress existing files or clear defaults in existing child directories.
- A decompression preview reports compressed files, required headroom, native defaults still active, unsupported entries, and any incomplete scan. Never promise a complete restore when files were skipped or failed.
- No destructive cleanup, automatic deletion, or automatic decompression merely because free space is low.

## Native inheritance versus selective automation

The existing “compress newly created files” checkbox changes an NTFS directory default. It applies broadly to future children, can affect new subfolders, and remains active without the app. It does not enforce exclusions, quiet periods, schedules, or the manual size policy.

The new automatic policy is controlled by the worker and applies only after eligibility checks. It can process existing and changed files as well as newly created ones. It cannot run while the worker is stopped.

Use selective automation as the primary managed-folder experience. Keep native inheritance as an advanced, separately labeled control. During onboarding, detect active defaults within the managed scope and explain that they can compress files outside the selective policy. Do not silently change existing defaults or promise that Pause all disables them. Offer an explicit, separately scoped job to clear directory defaults when the user wants the worker alone to control future compression.

## Technical direction and filesystem boundaries

Continue with C#, WPF, and `net10.0-windows`. Keep scanning, policy evaluation, scheduling, compression, and accounting independently callable. Reuse the alpha's metadata wrapper and service checks while replacing assumptions that only hold for a shallow manual queue.

- Extract reusable services into a core library as the worker and UI become separate consumers. Avoid coupling scanning or job lifetimes to a window.
- Use a single-instance per-user coordinator so multiple windows or startup invocations cannot create competing workers. The main window and tray initially share one process.
- Store managed roots, policies, file identity/observations, jobs, outcomes, and scan summaries in SQLite with schema migration. Keep file contents on the filesystem. Bound history retention and diagnostic storage.
- Use volume identity plus stable file IDs to reconcile renames/replacements and deduplicate observations. Strengthen mutations with handle-based native operations and identity checks; do not rely solely on path-based `compact.exe` for unattended jobs.
- Recursively enumerate without following reparse points or crossing into unsupported volumes. Revalidate selected roots and their ancestors. Reject protected Windows/Program Files locations, drive roots, UNC/device paths, and unsupported filesystems.
- Keep hidden/system, encrypted, sparse, offline/recall, read-only, and multiple-hard-link files excluded from automatic mutation until explicitly supported and tested. Use the same eligibility rules for scans, previews, and execution.
- For storage analysis, avoid counting hard-linked data repeatedly within a scope. Label overlapping totals and unknown allocations. Multiple dashboard scopes must not be summed as though they were always disjoint.
- Metrics initially cover the primary data stream, excluding named streams and directory metadata. Unknown sizes stay unknown. File changes or incomplete scans invalidate affected estimates and measurements.
- Continue ordinary NTFS compression. Defer WOF until the core automatic workflow has evidence, separate free-space rules, and compatibility tests. ZIP export and a custom filesystem are outside this roadmap.

## Delivery milestones

### 1. Finish and preserve the working alpha

Complete the remaining interactive acceptance checklist in [gui/README.md](gui/README.md), including opening through other applications, stop/close during work, and observed folder-default behavior. Keep this release available as a regression baseline.

Exit gate: repeatable Windows build and documented interactive acceptance; no unresolved content-integrity failures.

### 2. Reliable recursive jobs and managed folders

Build the foundation for unattended work:

1. Managed-root records, overlap checks, recursive scans, explicit exclusions, stable identities, and partial-scan reporting.
2. Persistent sequential queue with restart reconciliation, deduplication, cancellation, bounded retry, and searchable results.
3. Stronger native mutation/identity handling, space checks, and tested metadata/content preservation.
4. Basic managed-folder list showing current allocation, policy status, last scan, and a manual Compress now action.
5. Folder-wide decompression with a preview and protection against automatic recompression.

Exit gate: interrupted scans/jobs resume through reconciliation without duplicate mutations or double-counted results; rename/replacement, hard-link, and partial-failure tests pass.

### 3. Automatic compression as the next product release

1. Enable an explicit per-folder automatic policy that processes existing candidates and future eligible changes.
2. Watchers plus periodic reconciliation; quiet periods, cooldowns, poor-result caching, and bounded retry.
3. Persistent worker lifecycle, tray controls, single-instance behavior, and opt-in start at login.
4. Pause/resume, schedules, battery/resource behavior, free-space reserves, and visible waiting reasons.
5. Onboarding that explains any native directory inheritance already active in the scope.

Exit gate: choose a folder once, enable automation, close the window, create/copy/edit files, and observe eligible files compress without further clicks. After sign-out/restart and sign-in, an opted-in worker starts and catches up. Pause/exit stops new jobs; disabled policies remain disabled; continuous changes do not cause loops.

This is the priority delivery after the reliability foundation. Dashboard polish and additional compression backends must not delay a usable automatic workflow.

### 4. Storage dashboard, insights, and history

1. Volume usage and managed-folder cards with scan freshness and coverage.
2. Largest-file/folder views, drill-down, candidate explanations, and estimated benefit previews.
3. Signed measured job totals, scan history charts, and clear accounting boundaries.
4. Search/filter activity, retry failures, policy editing, and understandable empty/error/loading states.

Exit gate: users can identify large consumers, understand what automation will do, distinguish estimates from measured results, and explain why a file was skipped. Decompression, deletion, rescans, and restart must not inflate reported savings.

### 5. Private beta and distribution

- Package and sign the app; test installation, updates, startup registration, recovery, and uninstall behavior. Uninstall removes the worker/startup registration; files remain accessible in their existing native compression state.
- Validate supported Windows builds, disk cluster sizes, permissions, antivirus interactions, backup/restore, and realistic application save/update workflows.
- Provide accessible keyboard navigation, responsive large-tree analysis, and bounded disk/memory/database use.
- Keep cloud-sync, network storage, WOF, deduplication, and cleanup/deletion features deferred until separately designed and tested.
- Run a small user pilot focused on automatic storage reduction and understandable controls. Diagnostics are local by default; any uploaded diagnostics are opt-in and exclude paths/content by default.

Exit gate: no unresolved data-integrity failures, an explicit supported-environment list, demonstrated useful savings on representative workloads, and repeatable installation/recovery/uninstall procedures.

The earlier alpha timebox does not estimate this larger product. Re-estimate milestone effort after the recursive queue and native identity approach are proven; make each milestone usable and independently verifiable.

## Validation and product gates

Use disposable copies on local NTFS. Record Windows/SDK/app versions, filesystem settings, workload, and hardware. The existing alpha fixtures and hash checks remain the starting point, not the entire suite.

Required scenarios:

- New files, slow copies, continuous writes, ordinary save, save-as, atomic replacement, rename, delete/recreate, same-volume moves, and application updates.
- Worker stopped during changes, login startup, crash during a job, watcher overflow, missed events, and reconciliation after a long pause.
- Policy changes, exclusions, rejected overlapping roots, schedule boundaries, battery/resource pauses, disabled automation, and active native inheritance.
- Stop/exit after the current operation, access denied, exclusive locks, disappearing folders, insufficient headroom, and unsupported/reparse-point children.
- Recursive decompression without immediate recompression, stop-managing without decompression, and directory defaults that remain active until explicitly changed.
- Stable hashes, timestamps/attributes and relevant metadata, hard links, named-stream accounting boundaries, and honest results when another writer changes content.
- Large trees, bounded queues, database growth, repeat scans, no duplicate savings after restart, and incomplete/unknown scan accounting.

Measure actual allocation before/after, elapsed work, CPU/memory/I/O impact, temporary headroom, and opening/saving latency. Repeat on compressible text/log/source datasets, mixed documents, random data, and already-compressed media. Automation should learn to avoid repeatedly spending work on poor candidates.

Proposed pilot targets remain at least 10% net allocation reduction in opted-in target workloads and no more than 10% median opening/saving latency regression. Establish a baseline first and inspect absolute delay and slow-tail behavior; these are product targets, not promised outcomes or research findings. Revise the target audience or policy if resource cost outweighs useful savings.

## Main risks and responses

- **Unattended changes:** explicit managed scope, conservative defaults, stable identities, fresh validation, visible history, and interruption tests.
- **Repeated work:** debouncing, cooldowns, bounded retries, poor-result caching, and reconciliation that deduplicates work.
- **Space pressure:** reserves, conservative decompression preflight, pausing new work, and handling actual OS failures. Never decompress automatically to solve low space.
- **Misleading numbers:** separate volume free space, folder allocation, candidate estimates, and signed measured job changes; expose freshness and coverage.
- **Confusing automation controls:** distinguish analysis, selective policy, native inheritance, pause, exit, stop-managing, and decompression in the UI.
- **Resource disruption:** one worker first, bounded scans/queues, schedules and pause reasons, and workload benchmarks before increasing concurrency.
- **Scope growth:** prioritize automatic compression and storage insight. General file-manager features and destructive cleanup remain outside this plan.

## Implementation references

- [Alpha specification](alpha.md) and [current implementation/verification notes](gui/README.md).
- [Microsoft: NTFS compression versus compression libraries](https://learn.microsoft.com/en-us/windows/win32/fileio/file-compression-and-decompression).
- [Microsoft: compact command and directory defaults](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact).
- [Microsoft: compression state and synchronous operations](https://learn.microsoft.com/en-us/windows/win32/fileio/compression-state).
- [Microsoft: WOF read/write behavior](https://devblogs.microsoft.com/oldnewthing/20190618-00/?p=102597).
- [Microsoft: WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).
- [Microsoft: last-access update behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior).

The roadmap and targets are product decisions. Verify platform/API details against authoritative documentation when implementing each milestone.
