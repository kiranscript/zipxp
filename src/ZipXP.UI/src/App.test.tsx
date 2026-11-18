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
    original: 1048576,
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
it("keeps adding a folder separate from enabling compression", async () => {
    vi.mocked(request).mockImplementation(async (command) =>
        command === "snapshot"
            ? ({ ...fixture, folders: [], jobs: [] } as never)
            : ("new-folder" as never),
    );
    render(<App />);
    await userEvent.click(
        await screen.findByRole("button", { name: "Choose your first folder" }),
    );
    expect(request).toHaveBeenCalledWith("addFolder", {});
    expect(
        vi
            .mocked(request)
            .mock.calls.some(([command]) =>
                ["queue", "savePolicy"].includes(command),
            ),
    ).toBe(false);
});
it("saves an explicitly enabled folder policy", async () => {
    render(<App />);
    await screen.findByRole("button", { name: "Compress all eligible" });
    await userEvent.click(
        screen.getByRole("button", { name: "Managed folders" }),
    );
    await userEvent.click(
        screen.getByRole("button", { name: "Settings for Work" }),
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
        await screen.findByRole("button", { name: "Compress all eligible" }),
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
    await screen.findByRole("button", { name: "Compress all eligible" });
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
    await screen.findByRole("button", { name: "Compress all eligible" });
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
