# Storage-aware file explorer: development plan

Date: 2026-11-09

## Product decision

Build a Windows 11 desktop file explorer that reduces the storage used by suitable files while keeping their names, paths, and normal application access. Use native NTFS compression initially. ZIP is a future export/archive feature, not the live storage format.

The first release is deliberately a small folder browser. The product hypothesis is that users value understandable, reversible storage savings enough to adopt it. Neither a compression engine nor a complete Explorer replacement needs to be built to test that hypothesis.

Assumptions: one developer familiar with C#; a Windows 11 machine with a local NTFS test folder; .NET SDK installed. Estimates are planning estimates, not commitments. This workspace is currently empty and hosted on Linux; runtime acceptance needs a Windows machine. No app has been implemented or tested yet.

## Outcomes and boundaries

- Files open through this app, Windows Explorer, and another application's Open dialog using their original paths.
- Show logical size separately from allocated storage and measured changes caused by operations.
- Support an explicit folder setting for compression of newly created files.
- Later, offer selective policies for existing files based on savings and resource costs.
- Preserve access when the app is closed or uninstalled; the OS owns the compressed representation.
- Do not promise a particular percentage saved or universal performance improvements.

The initial audience is technically comfortable Windows users with space pressure and compressible local datasets. Validate with users who keep text datasets, logs, source trees, or relatively static application resources. Media-heavy collections may provide little value.

## Technical direction

Use C# with WPF on a supported .NET SDK, initially targeting `net10.0-windows`. WPF provides the desktop controls needed without introducing a web frontend or multiple application runtimes. Keep UI, file inspection, and compression operations separate, but avoid dependency injection frameworks and elaborate layering in the alpha.

Use ordinary NTFS compression first. Existing files are explicitly compressed through `compact.exe`; marking a directory sets the default for new children. Reads are transparently decompressed by Windows. There is no temporary extraction folder and no application-specific content store.

Add a WOF backend only after workload testing. WOF is attractive for stable, frequently read files, but writing can materialize an uncompressed file; free-space requirements and recompression behavior must be evaluated separately.

Do not create a custom filesystem in the first product cycle. Consider it only if a validated requirement cannot be met by native backends.

## Milestone 1: functional alpha, approximately 4–6 focused hours

Implement [alpha.md](alpha.md). Deliver a runnable Windows build with a shallow folder browser, selected-file compress/decompress, native folder inheritance controls, and a reproducible demo using disposable files.

Exit gate: the documented acceptance checks pass on Windows, including content hashes, opening outside the app, partial failure, cancellation between files, and compression inheritance while the app is closed.

This validates the complete interaction loop. It does not establish production readiness, robust concurrent mutation handling, or market demand.

## Milestone 2: reliable selected-folder optimizer, approximately 3–5 engineering days

1. Replace path-based command execution with handle-based native operations where needed to strengthen identity checks and avoid path replacement races.
2. Add recursive scanning with explicit reparse-point boundaries, stable file identities, hard-link accounting, and per-directory failures.
3. Add a queue with persistent job records, resumable scans, and reconciliation after an interrupted operation. Store metadata in SQLite; never make it the sole source of truth for file contents or compression state.
4. Model job results explicitly: succeeded, unchanged, skipped, failed, interrupted, and stale measurement.
5. Handle ordinary metadata and named streams deliberately. Test permissions, timestamps, hard links, antivirus interaction, and files changing during measurement.
6. Add preflight space estimates and conservative decompression checks. Handle actual out-of-space failures even when preflight passed.
7. Add folder-level results, failed-file retry, searchable errors, and a clear distinction between "disable future compression" and "decompress existing files."

Exit gate: repeated operations and interruption tests preserve contents, failures remain visible, and aggregate savings do not double-count files.

## Milestone 3: selective automation, approximately 3–5 engineering days

1. Sample multiple regions of candidate files and estimate likely benefit. Label estimates separately from measured results.
2. Start with explicit, configurable rules: minimum size, minimum expected absolute savings, time since modification, exclusions, and a cooldown after changes.
3. Run one bounded worker initially; pause for foreground load, low battery, or insufficient headroom. Benchmark before increasing concurrency.
4. Use filesystem notifications as hints and periodic reconciliation as the repair mechanism. Do not assume every event is delivered or that a last-access timestamp proves inactivity.
5. Offer an explainable preview: selected files, expected savings, skipped files, and reasons.
6. Separate native "compress new files" inheritance from selective optimization. The former applies broadly and persists without the app; the latter requires the app's policy worker. Do not imply that selective exclusions govern NTFS inheritance.
7. Test ordinary save, save-as, atomic replacement, application updates, same-volume moves, and copied-in files. Debounce changes rather than recompressing on every event.

Exit gate: automation produces worthwhile net savings without repeated compression loops or unacceptable foreground delays on representative workloads.

## Milestone 4: private beta, approximately 1–2 additional weeks

- Package and sign the app; document installation, updates, recovery, and uninstall behavior.
- Test multiple Windows configurations, disk cluster sizes, CPU/storage combinations, and permissions.
- Provide accessible keyboard navigation, useful empty/error states, and responsive large-directory browsing.
- Test backup/restore and cloud-sync interactions before enabling those locations.
- Add WOF only for explicitly tested workloads and with separate policy/space handling.
- Collect opt-in diagnostic summaries without uploading file names or contents by default.
- Run a small user pilot and observe whether users understand the controls and obtain useful savings.

Exit gate: no unresolved data-integrity failures, documented supported configurations, demonstrated user value, and a repeatable recovery procedure.

## Validation and decision criteria

Use a fixed corpus with repetitive text, mixed real documents, already-compressed media, random bytes, many small files, and larger files. Record exact OS, filesystem, hardware, and app version. Use copies of data; never benchmark the only copy.

Measure allocated bytes before and after, elapsed optimization time, CPU/memory use, temporary headroom, and cold/warm opening and saving latency. Collect enough repetitions to assess latency variation; do not present one successful run as a benchmark.

Proposed pilot gate, to adjust after baseline measurements: at least 10% net reduction in opted-in target folders and no more than 10% median latency regression on the chosen application workflows. Also inspect slow-tail latency and absolute delays; percentages alone can conceal noticeable stalls. These are product targets, not research findings.

Content hashes and metadata checks must pass on the fixed fixtures. Test partial completion, permission errors, cancellation, app termination, and a constrained test volume. A passing suite reduces risk but is not proof of safety under every failure.

If savings are weak on the target audience's files, narrow the audience before adding algorithms. If users only need occasional optimization, an Explorer integration or utility may be a better product than a replacement explorer. Validate that through interviews rather than assuming interface preference.

## Main risks and responses

- **Poor compressibility:** measure first, skip low-value work, and report actual results.
- **Almost-full disks:** preserve headroom and clearly separate reads from operations that expand files.
- **Concurrent edits:** use stable identities and native operations; distrust stale scan results.
- **Misleading accounting:** measure allocations, exclude unsupported cases, and distinguish baseline compression from savings achieved by this job.
- **Excessive background work:** cooldowns, resource budgets, cancellation, and clear pause controls.
- **Limited differentiation:** focus on trustworthy decisions and understandable results; compare the experience with CompactGUI before broadening scope.
- **Scope growth:** defer tabs, previews, search indexing, deduplication, cloud storage, custom drivers, and cross-platform support until the core has evidence.

## Research references

- [Microsoft: NTFS compression versus compression libraries](https://learn.microsoft.com/en-us/windows/win32/fileio/file-compression-and-decompression).
- [Microsoft: compact command and directory defaults](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact).
- [Microsoft: compression state and synchronous operations](https://learn.microsoft.com/en-us/windows/win32/fileio/compression-state).
- [Microsoft: WOF read/write behavior](https://devblogs.microsoft.com/oldnewthing/20190618-00/?p=102597).
- [Microsoft: WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).
- [Microsoft: last-access update behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior).
- [CompactGUI upstream](https://github.com/IridiumIO/CompactGUI).

Platform behavior above comes from these references. Architecture, milestones, estimates, and product gates are proposed decisions.
