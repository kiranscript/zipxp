export interface Policy {
    enabled: boolean;
    paused: boolean;
    recursive: boolean;
    runOnBattery: boolean;
    minimumBytes: number;
    maximumBytes: number;
    minimumSavingBytes: number;
    reserveBytes: number;
    quietSeconds: number;
    cooldownSeconds: number;
    startHour: number;
    endHour: number;
    exclusions: string;
}
export interface Scan {
    utc: string;
    logical: number;
    allocated: number;
    candidates: number;
    estimatedSaving: number;
    files: number;
    unknown: number;
    errors: number;
    complete: boolean;
    nativeDefaults: number;
}
export interface Folder {
    id: string;
    name: string;
    path: string;
    policy: Policy;
    status: string;
    scan: Scan | null;
}
export interface Job {
    id: string;
    rootId: string;
    path: string;
    fileName: string;
    state: string;
    stateText: string;
    action: string;
    message: string;
    delta: number | null;
    utc: string;
    pending: boolean;
}
export interface Snapshot {
    folders: Folder[];
    jobs: Job[];
    paused: boolean;
    startup: boolean;
    status: string;
    currentFile: string | null;
    original: number;
    allocated: number;
    unknown: number;
    volumes: { name: string; total: number; free: number }[];
}
export interface Insight {
    path: string;
    name: string;
    directory: boolean;
    logical: number | null;
    allocated: number | null;
    compressed: boolean;
    notes: string;
}
export interface Insights {
    path: string;
    parent: string | null;
    rows: Insight[];
    totalRows: number;
}
export interface Commands {
    snapshot: { args: Record<string, never>; result: Snapshot };
    insights: {
        args: {
            rootId: string;
            path: string;
            largest: boolean;
            search: string;
        };
        result: Insights;
    };
    addFolder: { args: Record<string, never>; result: string | null };
    scan: { args: { rootId: string }; result: string };
    pause: { args: { paused: boolean }; result: string };
    pauseFolder: { args: { rootId: string; paused: boolean }; result: string };
    startup: { args: { enabled: boolean }; result: string };
    savePolicy: { args: { rootId: string; policy: Policy }; result: string };
    queue: {
        args: {
            rootId?: string;
            operation: "Compress" | "Decompress" | "ClearDefault";
        };
        result: string;
    };
    restoreSelected: {
        args: { rootId: string; paths: string[] };
        result: string;
    };
    remove: { args: { rootId: string }; result: string };
    retry: { args: { jobId: string }; result: string };
    showFile: { args: { jobId: string }; result: string };
    quit: { args: Record<string, never>; result: string };
}
