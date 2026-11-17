import type { Snapshot, Insights } from "../src/types";
const gb = 1024 ** 3;
const policy = {
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
    exclusions: "*.zip;*.jpg;*.mp4;node_modules/*;.git/*",
};
export const snapshot: Snapshot = {
    folders: ["Work projects", "Documents", "Downloads"].map((name, i) => ({
        id: String(i),
        name,
        path: `C:\\Users\\Kiran\\${name}`,
        policy: { ...policy, enabled: i === 0, paused: i === 2 },
        status: i === 2 ? "Paused" : "Watching for changes",
        scan: {
            utc: new Date().toISOString(),
            logical: (10 - i * 2) * gb,
            allocated: (6 - i) * gb,
            candidates: gb,
            estimatedSaving: 0.4 * gb,
            files: 2400 - i * 700,
            unknown: 0,
            errors: 0,
            complete: true,
            nativeDefaults: i === 1 ? 2 : 0,
        },
    })),
    jobs: [
        "quarterly-report.csv",
        "application.log",
        "design-notes.md",
        "customer-export.json",
        "project-brief.docx",
    ].map((name, i) => ({
        id: String(i),
        rootId: "0",
        path: `C:\\Users\\Kiran\\Work projects\\${name}`,
        fileName: name,
        state: i === 1 ? "Failed" : "Succeeded",
        stateText: i === 1 ? "Failed" : "Done",
        action: i === 4 ? "Restore original size" : "Compress",
        message:
            i === 1
                ? "This file is open in another app. Close it and try again."
                : "",
        delta: i === 1 ? null : i === 4 ? -1478656 : 1478656,
        utc: new Date(Date.now() - i * 600000).toISOString(),
        pending: false,
    })),
    paused: false,
    startup: false,
    status: "Watching for changes",
    currentFile: null,
    original: 24 * gb,
    allocated: 15 * gb,
    unknown: 0,
    volumes: [{ name: "Local Disk (C:)", total: 512 * gb, free: 124.8 * gb }],
};
export const insights: Insights = {
    path: snapshot.folders[0].path,
    parent: null,
    totalRows: 5,
    rows: [
        "Client projects",
        "Research",
        "annual-report.csv",
        "application.log",
        "notes.md",
    ].map((name, i) => ({
        path: `${snapshot.folders[0].path}\\${name}`,
        name,
        directory: i < 2,
        logical: (2 - i * 0.35) * gb,
        allocated: (1.4 - i * 0.25) * gb,
        compressed: i >= 2,
        notes: i >= 2 ? "Compressed" : "",
    })),
};
