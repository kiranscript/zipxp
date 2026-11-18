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
                ["snapshot", "addFolder"].includes(command),
            ),
    ).toBe(true);
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
    expect(within(actual).getByText("None recorded")).toBeTruthy();
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
