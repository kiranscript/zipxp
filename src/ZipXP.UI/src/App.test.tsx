// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
    cleanup,
    render,
    screen,
    waitFor,
    within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "./App";
import type { Snapshot } from "./types";
import { request } from "./bridge";
vi.mock("./bridge", () => ({ connected: true, request: vi.fn() }));
const fixture: Snapshot = {
    folders: [
        {
            id: "work",
            name: "Work",
            path: "C:\\Work",
            status: "Watching",
            canRestore: true,
            scan: {
                utc: "2026-01-01T12:00:00Z",
                logical: 1048576,
                allocated: 524288,
                candidates: 262144,
                estimatedSaving: 131072,
                files: 4,
                unknown: 0,
                errors: 0,
                complete: true,
                nativeDefaults: 0,
            },
            policy: {
                enabled: false,
                paused: false,
                recursive: true,
                runOnBattery: false,
                minimumBytes: 65536,
                maximumBytes: 268435456,
                minimumSavingBytes: 16384,
                reserveBytes: 268435456,
                quietSeconds: 60,
                cooldownSeconds: 3600,
                startHour: 0,
                endHour: 0,
                exclusions: "*.zip",
            },
        },
    ],
    jobs: [
        {
            id: "failed",
            rootId: "work",
            path: "C:\\Work\\locked.txt",
            fileName: "locked.txt",
            state: "Failed",
            stateText: "Failed",
            action: "Compress",
            message: "File is locked",
            delta: null,
            utc: "2026-01-01T12:00:00Z",
            pending: false,
        },
    ],
    paused: false,
    startup: false,
    status: "Ready",
    currentFile: null,
    savings: { delta: 0, measured: 0 },
    allocated: 524288,
    unknown: 0,
    volumes: [],
};
beforeEach(() => {
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "snapshot") return structuredClone(fixture) as never;
        return "Done" as never;
    });
    HTMLDialogElement.prototype.close = function () {
        this.removeAttribute("open");
    };
    HTMLDialogElement.prototype.showModal = function () {
        this.setAttribute("open", "");
    };
});
afterEach(() => {
    cleanup();
    vi.resetAllMocks();
});
it("selects the newly added folder and keeps the scan separate from compression", async () => {
    const added = {
        ...fixture.folders[0],
        id: "new-folder",
        name: "New folder",
        path: "C:\\New folder",
        scan: null,
        canRestore: false,
    };
    let folderAdded = false;
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "chooseFolder")
            return { path: added.path, name: added.name } as never;
        if (command === "addFolder") {
            folderAdded = true;
            return added.id as never;
        }
        if (command === "snapshot")
            return {
                ...fixture,
                folders: folderAdded
                    ? [...fixture.folders, added]
                    : fixture.folders,
            } as never;
        throw new Error(`Unexpected command: ${command}`);
    });
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Add folder" }),
    );
    const dialog = await screen.findByRole("dialog");
    expect(
        within(dialog).getByText("How should ZipXP handle New folder?"),
    ).toBeTruthy();
    expect(
        (
            within(dialog).getByRole("radio", {
                name: /Scan, then let me choose/,
            }) as HTMLInputElement
        ).checked,
    ).toBe(true);
    await userEvent.click(
        within(dialog).getByRole("button", { name: "Add folder" }),
    );
    const heading = await screen.findByRole("heading", { name: "New folder" });
    expect(document.activeElement).toBe(heading);
    expect(screen.getByText("Auto-compression is off.")).toBeTruthy();
    expect(
        screen.getByText(/An estimate will appear after the first scan/),
    ).toBeTruthy();
    expect(
        (
            screen.getByRole("button", {
                name: "Compress once",
            }) as HTMLButtonElement
        ).disabled,
    ).toBe(true);
    expect(screen.queryByRole("button", { name: "Restore files" })).toBeNull();
    expect(
        vi
            .mocked(request)
            .mock.calls.every(([command]) =>
                ["snapshot", "chooseFolder", "addFolder"].includes(command),
            ),
    ).toBe(true);
    expect(request).toHaveBeenCalledWith("addFolder", { autoCompress: false });
});

it("keeps the estimate and ZipXP's measured savings separate", async () => {
    render(<App />);
    const estimate = (
        await screen.findByRole("heading", { name: "Estimated savings" })
    ).parentElement!;
    const actual = screen.getByRole("heading", {
        name: "Space saved by ZipXP",
    }).parentElement!;
    expect(within(estimate).getByText("128 KB")).toBeTruthy();
    expect(within(actual).getByText("Nothing compressed yet")).toBeTruthy();
    await userEvent.click(
        screen.getByRole("button", { name: "Manage compression" }),
    );
    expect(screen.getByRole("heading", { name: "Managed folders" })).toBe(
        document.activeElement,
    );
    expect(screen.getByRole("button", { name: "Restore files" })).toBeTruthy();
});

it("offers an explicit auto-compression action for the selected folder", async () => {
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Manage compression" }),
    );
    const enable = screen.getByRole("button", {
        name: "Turn on auto-compression",
    });
    await userEvent.click(enable);
    expect(request).toHaveBeenCalledWith("setAutoCompression", {
        rootId: "work",
        enabled: true,
    });
});
it("saves an explicitly enabled folder policy", async () => {
    render(<App />);
    await screen.findByRole("button", { name: "Compress all once" });
    await userEvent.click(
        screen.getByRole("button", { name: "Managed folders" }),
    );
    await userEvent.click(
        screen.getByRole("button", { name: "Folder settings for Work" }),
    );
    const dialog = screen.getByRole("dialog");
    await userEvent.click(
        within(dialog).getByLabelText("Automatically compress files"),
    );
    await userEvent.click(
        within(dialog).getByRole("button", { name: "Save settings" }),
    );
    await waitFor(() =>
        expect(request).toHaveBeenCalledWith("savePolicy", {
            rootId: "work",
            policy: { ...fixture.folders[0].policy, enabled: true },
        }),
    );
});
it("keeps a rejected operation visible to the user", async () => {
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "queue") throw new Error("Not enough free space");
        return structuredClone(fixture) as never;
    });
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Compress all once" }),
    );
    await waitFor(() =>
        expect(screen.getByRole("alert").textContent).toContain(
            "Not enough free space",
        ),
    );
    await waitFor(() => expect(request).toHaveBeenCalledWith("snapshot", {}));
    expect(screen.getByRole("alert").textContent).toContain(
        "Not enough free space",
    );
});
it("filters activity and retries the selected failed item", async () => {
    render(<App />);
    await screen.findByRole("button", { name: "Compress all once" });
    await userEvent.click(screen.getByRole("button", { name: /Activity/ }));
    await userEvent.type(
        screen.getByRole("textbox", { name: "Search activity" }),
        "missing",
    );
    expect(screen.getByText("No matching activity")).toBeTruthy();
    await userEvent.clear(
        screen.getByRole("textbox", { name: "Search activity" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(request).toHaveBeenCalledWith("retry", { jobId: "failed" });
});

it("updates the sign-in checkbox immediately and restores it if Windows rejects the change", async () => {
    let rejectStartup!: (reason: Error) => void;
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "startup")
            return new Promise((_resolve, reject) => {
                rejectStartup = reject;
            }) as never;
        return structuredClone(fixture) as never;
    });
    render(<App />);
    await screen.findByRole("button", { name: "Compress all once" });
    const checkbox = screen.getByRole("checkbox", {
        name: "Start when I sign in",
    }) as HTMLInputElement;
    await userEvent.click(checkbox);
    expect(checkbox.checked).toBe(true);
    rejectStartup(new Error("Windows could not save the sign-in preference."));
    await waitFor(() => expect(checkbox.checked).toBe(false));
    expect(screen.getByRole("alert").textContent).toContain(
        "Windows could not save",
    );
});

it("announces plural failure counts in both navigation and consolidated attention", async () => {
    vi.mocked(request).mockImplementation(async (command) =>
        command === "snapshot"
            ? ({
                  ...fixture,
                  paused: true,
                  jobs: [
                      fixture.jobs[0],
                      {
                          ...fixture.jobs[0],
                          id: "second",
                          fileName: "second.txt",
                      },
                  ],
              } as never)
            : ("Done" as never),
    );
    render(<App />);
    await screen.findByRole("button", {
        name: "Activity 2 failed files",
    });
    expect(
        screen.getByRole("button", {
            name: "2 files need attention, Review activity",
        }).textContent,
    ).toContain("2 files need attention");
});

const noSavings = (
    skips: NonNullable<Snapshot["folders"][number]["skips"]>,
) => {
    const folder = structuredClone(fixture.folders[0]);
    folder.scan!.estimatedSaving = 0;
    folder.scan!.candidates = 0;
    folder.skips = skips;
    return { ...structuredClone(fixture), folders: [folder], jobs: [] };
};
const skipCounts = {
    alreadyCompressed: { files: 0, logical: 0, allocated: 0 },
    tooSmall: 0,
    lowBenefit: 0,
    excluded: 0,
    other: 0,
};
async function openManagedFolders(snapshot: Snapshot) {
    vi.mocked(request).mockImplementation(async (command) =>
        command === "snapshot"
            ? (structuredClone(snapshot) as never)
            : ("Done" as never),
    );
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Manage compression" }),
    );
}
it("calls a folder of already compressed files compact, not empty", async () => {
    await openManagedFolders(
        noSavings({
            ...skipCounts,
            alreadyCompressed: {
                files: 22,
                logical: 50000000,
                allocated: 12000000,
            },
            lowBenefit: 1,
        }),
    );
    expect(screen.getAllByText("Already compact").length).toBeGreaterThan(0);
    expect(screen.getByText(/22 files use .* on disk instead of/)).toBeTruthy();
    expect(screen.queryByText("No savings found")).toBeNull();
    expect(
        screen.queryByRole("button", { name: "Try another folder" }),
    ).toBeNull();
});
it("tells media and archive folders apart and offers another folder", async () => {
    await openManagedFolders(noSavings({ ...skipCounts, lowBenefit: 6 }));
    expect(screen.getAllByText("Nothing compressible").length).toBeGreaterThan(
        0,
    );
    await userEvent.click(
        screen.getByRole("button", { name: "Try another folder" }),
    );
    expect(request).toHaveBeenCalledWith("chooseFolder", {});
});
it("explains small and excluded files, and lists why files were skipped", async () => {
    await openManagedFolders(
        noSavings({ ...skipCounts, tooSmall: 5, excluded: 2, other: 1 }),
    );
    expect(
        screen.getAllByText("Mostly small or excluded files").length,
    ).toBeGreaterThan(0);
    const details = screen
        .getByText("Why some files were skipped")
        .closest("details")!;
    expect(
        within(details).getByText("5 files smaller than 64 KB"),
    ).toBeTruthy();
    expect(
        within(details).getByText("2 files excluded by your settings"),
    ).toBeTruthy();
});
it("makes Compress once the primary action when files can be compressed", async () => {
    await openManagedFolders(fixture);
    expect(
        screen.getByRole("button", { name: "Compress once" }).className,
    ).toContain("primary");
    expect(
        screen.getByRole("button", { name: "Turn on auto-compression" })
            .className,
    ).not.toContain("primary");
});
it("leads with measured savings once ZipXP has saved space", async () => {
    const snapshot = structuredClone(fixture);
    snapshot.savings = { delta: 2097152, measured: 1 };
    vi.mocked(request).mockImplementation(async (command) =>
        command === "snapshot" ? (snapshot as never) : ("Done" as never),
    );
    render(<App />);
    const hero = (
        await screen.findByRole("heading", { name: "Space saved by ZipXP" })
    ).closest("section")!;
    expect(hero.className).toContain("measured-first");
});
it("still lets you choose a folder when no suggestions are available", async () => {
    const empty = {
        ...structuredClone(fixture),
        folders: [],
        jobs: [],
        volumes: [],
    };
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "snapshot") return structuredClone(empty) as never;
        throw new Error("unavailable");
    });
    render(<App />);
    expect(
        await screen.findByRole("button", { name: "Choose a folder" }),
    ).toBeTruthy();
    expect(screen.queryByText("Good places to start")).toBeNull();
});

it("describes a folder by what most of its files are", async () => {
    await openManagedFolders(
        noSavings({
            ...skipCounts,
            alreadyCompressed: {
                files: 3,
                logical: 3000000,
                allocated: 900000,
            },
            lowBenefit: 5000,
        }),
    );
    expect(screen.getAllByText("Nothing compressible").length).toBeGreaterThan(
        0,
    );
    expect(screen.queryByText("Already compact")).toBeNull();
});
it("does not push auto-compression when there is nothing to compress", async () => {
    await openManagedFolders(noSavings({ ...skipCounts, lowBenefit: 6 }));
    expect(
        screen.getByRole("button", { name: "Turn on auto-compression" })
            .className,
    ).not.toContain("primary");
});

const run = (
    over: Partial<NonNullable<Snapshot["folders"][number]["run"]>> = {},
) => ({
    operation: "Compress" as const,
    startedUtc: "2026-01-01T12:00:00Z",
    total: 40,
    pending: 28,
    failed: 0,
    skipped: 0,
    savedSoFar: 3145728,
    phase: "working" as const,
    waiting: null,
    currentFile: "report.csv",
    ...over,
});
const withRun = (
    r: ReturnType<typeof run>,
    patch: Partial<Snapshot["folders"][number]> = {},
) => {
    const snapshot = structuredClone(fixture);
    snapshot.folders[0] = { ...snapshot.folders[0], ...patch, run: r };
    return snapshot;
};
it("shows live progress while a folder is compressing", async () => {
    await openManagedFolders(withRun(run()));
    const card = screen.getByRole("group", { name: "Compressing Work" });
    expect(within(card).getByText(/12 of 40 files/)).toBeTruthy();
    expect(
        within(card).getByRole("progressbar").getAttribute("aria-valuenow"),
    ).toBe("30");
    expect(within(card).getByText(/saved so far/)).toBeTruthy();
    expect(within(card).getByText("Now: report.csv")).toBeTruthy();
    expect(screen.getByText("Compressing · 30%")).toBeTruthy();
    expect(
        screen.getAllByText("Compressing Work: 12/40").length,
    ).toBeGreaterThan(0);
});
it("says why queued work is waiting", async () => {
    await openManagedFolders(
        withRun(
            run({
                phase: "waiting",
                waiting: "Waiting until the PC is plugged in",
                currentFile: null,
            }),
        ),
    );
    expect(screen.getByText("Waiting until the PC is plugged in")).toBeTruthy();
    expect(screen.getByText("Waiting · 30%")).toBeTruthy();
});
it("pauses and stops a running one-time job", async () => {
    await openManagedFolders(withRun(run()));
    const card = screen.getByRole("group", { name: "Compressing Work" });
    await userEvent.click(
        within(card).getByRole("button", { name: "Pause folder" }),
    );
    expect(request).toHaveBeenCalledWith("pauseFolder", {
        rootId: "work",
        paused: true,
    });
    await userEvent.click(within(card).getByRole("button", { name: "Stop" }));
    expect(request).toHaveBeenCalledWith("stop", { rootId: "work" });
});
it("only offers Pause, not Stop, when auto-compression keeps feeding the queue", async () => {
    const snapshot = withRun(run());
    snapshot.folders[0].policy.enabled = true;
    await openManagedFolders(snapshot);
    const card = screen.getByRole("group", { name: "Compressing Work" });
    expect(
        within(card).getByRole("button", { name: "Pause folder" }),
    ).toBeTruthy();
    expect(within(card).queryByRole("button", { name: "Stop" })).toBeNull();
});
it("explains progress on the Overview while work runs", async () => {
    const snapshot = withRun(run());
    snapshot.savings = { delta: 3145728, measured: 12 };
    vi.mocked(request).mockImplementation(async (command) =>
        command === "snapshot" ? (snapshot as never) : ("Done" as never),
    );
    render(<App />);
    expect(
        await screen.findByText(/saved so far from 12 files, still working/),
    ).toBeTruthy();
    expect(screen.getByText("Compressing Work: 12/40.")).toBeTruthy();
});
it("reports a finished run and offers auto-compression once", async () => {
    const snapshot = withRun(
        run({
            phase: "done",
            pending: 0,
            total: 5,
            failed: 1,
            savedSoFar: 2097152,
        }),
    );
    await openManagedFolders(snapshot);
    expect(
        screen.getByText("Done. ZipXP saved 2 MB across 4 files."),
    ).toBeTruthy();
    expect(screen.getByText(/1 file couldn’t be processed/)).toBeTruthy();
    await userEvent.click(
        screen.getByRole("button", { name: "Review activity" }),
    );
    expect(screen.getByRole("heading", { name: "Activity" })).toBeTruthy();
    cleanup();
    await openManagedFolders(snapshot);
    await userEvent.click(
        screen.getByRole("button", { name: "Keep compressing automatically" }),
    );
    expect(request).toHaveBeenCalledWith("setAutoCompression", {
        rootId: "work",
        enabled: true,
    });
});
it("lets a finished run be dismissed and does not announce a tiny automatic run", async () => {
    const snapshot = withRun(
        run({ phase: "done", pending: 0, total: 5, savedSoFar: 2097152 }),
    );
    await openManagedFolders(snapshot);
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByText(/Done\. ZipXP saved/)).toBeNull();
    cleanup();
    const auto = withRun(
        run({ phase: "done", pending: 0, total: 1, savedSoFar: 4096 }),
    );
    auto.folders[0].policy.enabled = true;
    await openManagedFolders(auto);
    expect(screen.queryByText(/Done\. ZipXP saved/)).toBeNull();
});
it("offers the folder choice when a suggested folder is scanned", async () => {
    const empty = {
        ...structuredClone(fixture),
        folders: [],
        jobs: [],
        volumes: [],
    };
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "snapshot") return structuredClone(empty) as never;
        if (command === "suggestions")
            return [
                {
                    path: "C:\\Users\\Kiran\\source",
                    name: "source",
                    note: "Source code and build output usually compress well.",
                },
            ] as never;
        return "Done" as never;
    });
    render(<App />);
    expect(
        await screen.findByRole("heading", {
            name: "Free up space without deleting anything",
        }),
    ).toBeTruthy();
    await userEvent.click(
        await screen.findByRole("button", { name: "Scan source" }),
    );
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(
        within(dialog).getByRole("radio", {
            name: /keep compressed automatically/,
        }),
    );
    expect(within(dialog).getByText(/hard disk/)).toBeTruthy();
    await userEvent.click(
        within(dialog).getByRole("button", { name: "Add folder" }),
    );
    expect(request).toHaveBeenCalledWith("addFolder", {
        path: "C:\\Users\\Kiran\\source",
        autoCompress: true,
    });
});
it("adds nothing when the folder choice is cancelled", async () => {
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "snapshot") return structuredClone(fixture) as never;
        if (command === "chooseFolder")
            return { path: "C:\\Other", name: "Other" } as never;
        return "Done" as never;
    });
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Add folder" }),
    );
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(
        within(dialog).getByRole("button", { name: "Cancel" }),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(request).not.toHaveBeenCalledWith("addFolder", expect.anything());
});

it("describes work that is queued behind other folders", async () => {
    await openManagedFolders(
        withRun(run({ phase: "queued", currentFile: null })),
    );
    expect(screen.getByText("Queued behind other work.")).toBeTruthy();
    expect(screen.getByText("Queued · 30%")).toBeTruthy();
});
it("hides progress for tiny automatic runs so it does not flicker", async () => {
    const snapshot = withRun(run({ total: 1, pending: 1, savedSoFar: 0 }));
    snapshot.folders[0].policy.enabled = true;
    await openManagedFolders(snapshot);
    expect(
        screen.queryByRole("group", { name: "Compressing Work" }),
    ).toBeNull();
    expect(screen.queryByText(/Compressing · /)).toBeNull();
});
it("does not bring back a finished run that ended long ago, and keeps a dismissal across pages", async () => {
    const old = withRun(
        run({
            phase: "done",
            pending: 0,
            total: 5,
            finishedUtc: "2020-01-01T00:00:00Z",
        }),
    );
    await openManagedFolders(old);
    expect(screen.queryByText(/Done\. ZipXP saved/)).toBeNull();
    cleanup();
    const fresh = withRun(
        run({
            phase: "done",
            pending: 0,
            total: 5,
            finishedUtc: new Date().toISOString(),
        }),
    );
    await openManagedFolders(fresh);
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await userEvent.click(screen.getByRole("button", { name: "Overview" }));
    await userEvent.click(
        screen.getByRole("button", { name: "Managed folders" }),
    );
    expect(screen.queryByText(/Done\. ZipXP saved/)).toBeNull();
});
it("discards the picked folder when the add dialog is cancelled", async () => {
    vi.mocked(request).mockImplementation(async (command) => {
        if (command === "snapshot") return structuredClone(fixture) as never;
        if (command === "chooseFolder")
            return { path: "C:\\Other", name: "Other" } as never;
        return "Done" as never;
    });
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Add folder" }),
    );
    await userEvent.click(
        within(await screen.findByRole("dialog")).getByRole("button", {
            name: "Cancel",
        }),
    );
    expect(request).toHaveBeenCalledWith("discardPick", {});
});
