import {
    Children,
    createContext,
    useCallback,
    useContext,
    useEffect,
    useId,
    useRef,
    useState,
} from "react";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import {
    Activity,
    AlertCircle,
    ArrowDownToLine,
    ArrowLeft,
    ArrowUpRight,
    Check,
    ChevronDown,
    ChevronRight,
    Clock3,
    CircleMinus,
    FileText,
    Eye,
    Folder,
    FolderPlus,
    HardDrive,
    LayoutDashboard,
    LoaderCircle,
    MoreHorizontal,
    Pause,
    Play,
    Power,
    RefreshCw,
    Search,
    Settings2,
    ShieldCheck,
    Sparkles,
    Unplug,
    X,
} from "lucide-react";
import { connected, request } from "./bridge";
import type {
    Folder as ManagedFolder,
    Insights,
    Insight,
    Policy,
    Run,
    Snapshot,
    Suggestion,
    Job,
} from "./types";

type Page = "Overview" | "Managed folders" | "Storage insights" | "Activity";
type Act = (action: () => Promise<unknown>) => Promise<void>;
const pages = [
    { name: "Overview", icon: LayoutDashboard },
    { name: "Managed folders", icon: Folder },
    { name: "Storage insights", icon: HardDrive },
    { name: "Activity", icon: Activity },
] as const;
const hints: Record<Page, string> = {
    Overview: "See what you could save and what ZipXP has already saved.",
    "Managed folders":
        "Choose a folder to compress once, turn on auto-compression, or restore files.",
    "Storage insights": "Find the files and folders using the most space.",
    Activity: "Track work and resolve files that need attention.",
};
const ActionState = createContext({ busy: false, active: "" });
function ActionButton({
    children,
    actionLabel,
    confirmation,
    onClick,
    disabled,
    ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
    actionLabel?: string;
    confirmation?: boolean;
}) {
    const { busy, active } = useContext(ActionState);
    const id = useId();
    const label =
        actionLabel ??
        props["aria-label"] ??
        Children.toArray(children)
            .filter(
                (child) =>
                    typeof child === "string" || typeof child === "number",
            )
            .join(" ")
            .replace(/\s+/g, " ")
            .trim();
    return (
        <button
            {...props}
            data-action-id={id}
            data-action-label={label}
            data-confirmation={confirmation || undefined}
            aria-label={props["aria-label"]}
            disabled={disabled}
            aria-disabled={busy || undefined}
            aria-busy={(busy && active === id) || undefined}
            onClick={(e) => {
                if (!busy) onClick?.(e);
            }}
        >
            {busy && active === id && (
                <LoaderCircle size={16} className="spin" />
            )}
            {children}
        </button>
    );
}
export function bytes(value: number | null | undefined): string {
    if (value == null) return "—";
    const units = ["bytes", "KB", "MB", "GB", "TB"];
    let size = value,
        unit = 0;
    while (Math.abs(size) >= 1024 && unit < units.length - 1) {
        size /= 1024;
        unit++;
    }
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: unit ? 1 : 0 }).format(size)} ${unit === 0 && Math.abs(value) === 1 ? "byte" : units[unit]}`;
}
function storageStatus(row: Insight): string {
    if (row.directory) return row.notes || "Folder";
    const state = row.compressed
        ? "Compressed"
        : row.logical == null
          ? "Compression state unavailable"
          : "Not compressed";
    const note =
        row.compressed && row.notes === "Too small to be worth compressing"
            ? "Below the minimum file size for new compression"
            : row.notes;
    return note && !(row.compressed && note === "Already compressed")
        ? `${state} · ${note}`
        : state;
}
function when(value: string): string {
    const date = new Date(value);
    return date.toLocaleDateString() === new Date().toLocaleDateString()
        ? `Today, ${date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`
        : date.toLocaleDateString([], { month: "short", day: "numeric" });
}
function percentage(value: number): string {
    return value > 0 && value < 0.01 ? "<1%" : `${Math.round(value * 100)}%`;
}
interface Outcome {
    short: string;
    detail: string;
    another: boolean;
}
// Explains a scan without a dead end: "already compact" is a good result, not an empty one.
function folderOutcome(f: ManagedFolder): Outcome {
    if (!f.scan)
        return {
            short: "Waiting for scan",
            detail: "Scanning leaves your files as they are. An estimate will appear after the first scan.",
            another: false,
        };
    if (f.scan.estimatedSaving > 0)
        return {
            short: `Could save ${bytes(f.scan.estimatedSaving)}`,
            detail: `Estimated savings: ${bytes(f.scan.estimatedSaving)}.`,
            another: false,
        };
    const skips = f.skips;
    const done = skips?.alreadyCompressed;
    // Describe what most of the files are; one stray file shouldn't decide the message.
    const compact = done?.files ?? 0;
    const media = skips?.lowBenefit ?? 0;
    const small = (skips?.tooSmall ?? 0) + (skips?.excluded ?? 0);
    const most = Math.max(compact, media, small);
    if (done && compact > 0 && compact === most)
        return {
            short: "Already compact",
            detail: `Already compact: ${done.files.toLocaleString()} ${done.files === 1 ? "file uses" : "files use"} ${bytes(done.allocated)} on disk instead of ${bytes(done.logical)}. Windows or ZipXP has already compressed ${done.files === 1 ? "it" : "them"}, so there is nothing more to save here.`,
            another: false,
        };
    if (skips && media > 0 && media === most)
        return {
            short: "Nothing compressible",
            detail: "These files are mostly photos, video and archives, which are already compressed. Try Documents or a code folder.",
            another: true,
        };
    if (skips && small > 0 && small === most)
        return {
            short: "Mostly small or excluded files",
            detail: `Most files are smaller than ${bytes(f.policy.minimumBytes)} or excluded by your settings. Check Folder settings.`,
            another: true,
        };
    return {
        short: "No savings found",
        detail: "No additional savings found in the files checked so far.",
        another: true,
    };
}
function SkipDetails({ folder }: { folder: ManagedFolder }) {
    const s = folder.skips;
    if (!s) return null;
    const files = (n: number) =>
        `${n.toLocaleString()} ${n === 1 ? "file" : "files"}`;
    const rows: string[] = [];
    if (s.alreadyCompressed.files > 0)
        rows.push(`${files(s.alreadyCompressed.files)} already compressed`);
    if (s.lowBenefit > 0)
        rows.push(
            `${files(s.lowBenefit)} wouldn’t shrink enough to be worth compressing`,
        );
    if (s.tooSmall > 0)
        rows.push(
            `${files(s.tooSmall)} smaller than ${bytes(folder.policy.minimumBytes)}`,
        );
    if (s.excluded > 0)
        rows.push(`${files(s.excluded)} excluded by your settings`);
    if (s.other > 0)
        rows.push(
            `${files(s.other)} skipped (hidden, system, encrypted, hard-linked, too large, recently changed or recently processed)`,
        );
    if (!rows.length) return null;
    return (
        <details className="skip-details">
            <summary>Why some files were skipped</summary>
            <ul>
                {rows.map((row) => (
                    <li key={row}>{row}</li>
                ))}
            </ul>
        </details>
    );
}
function SuggestedFolders({
    add,
}: {
    add: (path?: string, name?: string) => Promise<void>;
}) {
    const [items, setItems] = useState<Suggestion[]>([]);
    useEffect(() => {
        let live = true;
        void (async () => {
            try {
                const found = await request("suggestions", {});
                if (live && Array.isArray(found)) setItems(found);
            } catch {
                // Suggestions are optional; the folder picker always works.
            }
        })();
        return () => {
            live = false;
        };
    }, []);
    if (!items.length) return null;
    return (
        <div
            className="suggestions"
            role="group"
            aria-label="Suggested folders"
        >
            <h3>Good places to start</h3>
            <div className="suggestion-list">
                {items.map((item) => (
                    <div className="suggestion" key={item.path}>
                        <span className="folder-icon">
                            <Folder size={20} />
                        </span>
                        <span>
                            <strong>{item.name}</strong>
                            <small>{item.note}</small>
                            <small title={item.path}>{item.path}</small>
                        </span>
                        <ActionButton
                            aria-label={`Scan ${item.name}`}
                            onClick={() => void add(item.path, item.name)}
                        >
                            Scan
                        </ActionButton>
                    </div>
                ))}
            </div>
        </div>
    );
}
function runVerb(run: Run): string {
    return run.operation === "Decompress"
        ? "Restoring"
        : run.operation === "ClearDefault"
          ? "Turning off Windows compression for"
          : "Compressing";
}
function runUnit(run: Run, count: number): string {
    return run.operation === "ClearDefault"
        ? count === 1
            ? "folder"
            : "folders"
        : count === 1
          ? "file"
          : "files";
}
// Auto-compression turns every changed file into a tiny run; only surface runs worth watching.
const runActive = (f: ManagedFolder) =>
    Boolean(
        f.run &&
        f.run.phase !== "done" &&
        (!f.policy.enabled || f.run.total >= 3),
    );
const runDone = (run: Run) => run.total - run.pending;
const runPercent = (run: Run) =>
    run.total ? Math.round((runDone(run) / run.total) * 100) : 0;
// Eases a number toward its new value between the 3-second polls; skipped when reduced motion is requested.
function useCountUp(target: number, ms = 600): number {
    const [shown, setShown] = useState(target);
    const from = useRef(target);
    useEffect(() => {
        const reduced =
            typeof window.matchMedia === "function" &&
            window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        if (reduced || typeof requestAnimationFrame !== "function") {
            from.current = target;
            setShown(target);
            return;
        }
        const begin = from.current;
        const start = performance.now();
        let frame = 0;
        const tick = (now: number) => {
            const progress = Math.min(1, (now - start) / ms);
            from.current = begin + (target - begin) * progress;
            setShown(from.current);
            if (progress < 1) frame = requestAnimationFrame(tick);
        };
        frame = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(frame);
    }, [target, ms]);
    return Math.round(shown);
}
function SavedSoFar({ value }: { value: number }) {
    return <>{bytes(useCountUp(value))}</>;
}
function RunCard({ folder, act }: { folder: ManagedFolder; act: Act }) {
    const run = folder.run!;
    const percent = runPercent(run);
    return (
        <div
            className="run-card"
            role="group"
            aria-label={`${runVerb(run)} ${folder.name}`}
        >
            <div className="run-head">
                <strong>
                    {runVerb(run)} {folder.name}… {runDone(run)} of {run.total}{" "}
                    {runUnit(run, run.total)}
                </strong>
                <span>{percent}%</span>
            </div>
            <div
                className="run-bar"
                role="progressbar"
                aria-label="Progress"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={percent}
            >
                <span style={{ width: `${percent}%` }} />
            </div>
            {run.operation === "Compress" && (
                <p>
                    <SavedSoFar value={run.savedSoFar} /> saved so far
                </p>
            )}
            <p className="muted">
                {run.phase === "waiting"
                    ? (run.waiting ?? "Waiting until conditions allow work")
                    : run.phase === "queued"
                      ? "Queued behind other work."
                      : run.currentFile
                        ? `Now: ${run.currentFile}`
                        : "Starting…"}
            </p>
            <div className="actions wrap">
                <ActionButton
                    onClick={() =>
                        void act(() =>
                            request("pauseFolder", {
                                rootId: folder.id,
                                paused: !folder.policy.paused,
                            }),
                        )
                    }
                >
                    {folder.policy.paused ? (
                        <Play size={16} />
                    ) : (
                        <Pause size={16} />
                    )}
                    {folder.policy.paused ? "Resume folder" : "Pause folder"}
                </ActionButton>
                {!folder.policy.enabled && (
                    <ActionButton
                        onClick={() =>
                            void act(() =>
                                request("stop", { rootId: folder.id }),
                            )
                        }
                    >
                        <CircleMinus size={16} />
                        Stop
                    </ActionButton>
                )}
            </div>
        </div>
    );
}
function AddFolderDialog({
    name,
    busy,
    error,
    close,
    confirm,
}: {
    name: string;
    busy: boolean;
    error: string;
    close: () => void;
    confirm: (autoCompress: boolean) => void;
}) {
    const dialog = useRef<HTMLDialogElement>(null);
    const [auto, setAuto] = useState(false);
    useEffect(() => {
        const previous = document.activeElement as HTMLElement | null;
        const element = dialog.current!;
        const root = document.documentElement;
        const overflow = root.style.overflow;
        root.style.overflow = "hidden";
        element.showModal();
        return () => {
            root.style.overflow = overflow;
            element.close();
            previous?.focus({ preventScroll: true });
        };
    }, []);
    return (
        <dialog
            ref={dialog}
            className="policy-dialog add-dialog"
            aria-labelledby="add-folder-title"
            onCancel={(e) => {
                e.preventDefault();
                if (!busy) close();
            }}
        >
            <form
                onSubmit={(e) => {
                    e.preventDefault();
                    if (!busy) confirm(auto);
                }}
            >
                <div className="dialog-heading">
                    <div>
                        <div className="eyebrow">ADD FOLDER</div>
                        <h2 id="add-folder-title">
                            How should ZipXP handle {name}?
                        </h2>
                    </div>
                </div>
                <div className="dialog-body">
                    <fieldset className="choice-list">
                        <legend className="sr-only">
                            How ZipXP handles this folder
                        </legend>
                        <label className="choice">
                            <input
                                type="radio"
                                name="handling"
                                checked={!auto}
                                onChange={() => setAuto(false)}
                            />
                            <span>
                                <strong>Scan, then let me choose</strong>
                                <small>
                                    ZipXP reads the folder and shows possible
                                    savings. Nothing changes yet.
                                </small>
                            </span>
                        </label>
                        <label className="choice">
                            <input
                                type="radio"
                                name="handling"
                                checked={auto}
                                onChange={() => setAuto(true)}
                            />
                            <span>
                                <strong>
                                    Scan and keep compressed automatically
                                </strong>
                                <small>
                                    ZipXP compresses eligible files now and
                                    keeps new and changed files compressed while
                                    it runs. Files open as usual. You can
                                    restore them any time.
                                </small>
                            </span>
                        </label>
                    </fieldset>
                    {auto && (
                        <p className="footnote">
                            Compressed files use a little extra CPU when opened.
                            If this folder is on a hard disk, the first pass can
                            slow it down for a while.
                        </p>
                    )}
                    {error && (
                        <div className="message warning" role="alert">
                            <AlertCircle size={17} />
                            <span>{error}</span>
                        </div>
                    )}
                </div>
                <div className="dialog-actions">
                    <button type="button" disabled={busy} onClick={close}>
                        Cancel
                    </button>
                    <ActionButton
                        type="submit"
                        className="primary"
                        actionLabel="Add folder"
                        confirmation
                    >
                        Add folder
                    </ActionButton>
                </div>
            </form>
        </dialog>
    );
}
function Meter({ value, label }: { value: number; label: string }) {
    const fraction = Math.max(0, Math.min(1, value));
    const percent = fraction * 100;
    return (
        <div
            className="meter"
            role="meter"
            aria-label={label}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
            aria-valuetext={percentage(fraction).replace("<", "Less than ")}
        >
            <span
                style={{ width: `${percent}%`, minWidth: percent > 0 ? 2 : 0 }}
            />
        </div>
    );
}
function Empty({
    title,
    children,
    icon = "folder",
}: {
    title: string;
    children: ReactNode;
    icon?: "folder" | "activity" | "search" | "connection";
}) {
    const Icon =
        icon === "connection"
            ? Unplug
            : icon === "activity"
              ? Activity
              : icon === "search"
                ? Search
                : FolderPlus;
    return (
        <div className="empty">
            <div className="empty-icon">
                <Icon size={28} />
            </div>
            <h2>{title}</h2>
            {children}
        </div>
    );
}

function useScrollEdges(contentKey?: string | number) {
    const ref = useRef<HTMLDivElement>(null);
    const [edges, setEdges] = useState({ above: false, below: false });
    useEffect(() => {
        const element = ref.current;
        if (!element) return;
        const measure = () => {
            const above = element.scrollTop > 2;
            const below =
                element.scrollHeight -
                    element.clientHeight -
                    element.scrollTop >
                2;
            setEdges((current) =>
                current.above === above && current.below === below
                    ? current
                    : { above, below },
            );
        };
        measure();
        const observer =
            typeof ResizeObserver === "undefined"
                ? undefined
                : new ResizeObserver(measure);
        observer?.observe(element);
        Array.from(element.children).forEach((child) =>
            observer?.observe(child),
        );
        element.addEventListener("scroll", measure, { passive: true });
        return () => {
            observer?.disconnect();
            element.removeEventListener("scroll", measure);
        };
    }, [contentKey]);
    return { ref, ...edges };
}

export default function App() {
    const [page, setPage] = useState<Page>("Overview");
    const heading = useRef<HTMLHeadingElement>(null);
    const focusPageHeading = useRef(false);
    const [data, setData] = useState<Snapshot | null>(null);
    const [actionError, setError] = useState("");
    const [connectionError, setConnectionError] = useState("");
    const [notice, setNotice] = useState("");
    const [busy, setBusy] = useState(false);
    const [quitting, setQuitting] = useState(false);
    const [startupBusy, setStartupBusy] = useState(false);
    const exitRequested = useRef(false);
    const startupRevision = useRef(0);
    const [active, setActive] = useState("");
    const [actionLabel, setActionLabel] = useState("");
    const [confirmation, setConfirmation] = useState(false);
    const [selectedId, setSelectedId] = useState("");
    const [addedFolderId, setAddedFolderId] = useState("");
    const [editing, setEditing] = useState<ManagedFolder | null>(null);
    const [dismissedRuns, setDismissedRuns] = useState<string[]>([]);
    const [choice, setChoice] = useState<{
        name: string;
        path?: string;
    } | null>(null);
    const [activityFilter, setActivityFilter] = useState("All");
    const loading = useRef(false),
        command = useRef(false);
    const refresh = useCallback(async () => {
        if (loading.current) return;
        loading.current = true;
        const revision = startupRevision.current;
        try {
            const snapshot = await request("snapshot", {});
            setData((current) =>
                revision === startupRevision.current || !current
                    ? snapshot
                    : { ...snapshot, startup: current.startup },
            );
            setConnectionError("");
        } catch (e) {
            setConnectionError((e as Error).message);
        } finally {
            loading.current = false;
        }
    }, []);
    useEffect(() => {
        void refresh();
        const timer = setInterval(() => {
            if (connected && !document.hidden && !command.current)
                void refresh();
        }, 3000);
        return () => clearInterval(timer);
    }, [refresh]);
    useEffect(() => {
        document.scrollingElement?.scrollTo?.({ top: 0, left: 0 });
        if (focusPageHeading.current) {
            focusPageHeading.current = false;
            heading.current?.focus({ preventScroll: true });
        }
    }, [page]);
    const openPage = (next: Page) => {
        if (next === page) return;
        focusPageHeading.current = true;
        setPage(next);
    };
    const folders = data?.folders ?? [];
    const selected =
        folders.find((f) => f.id === selectedId) ??
        (addedFolderId ? undefined : folders[0]);
    const selectFolder = (id: string) => {
        setAddedFolderId("");
        setSelectedId(id);
    };
    const newFolderReady = Boolean(
        addedFolderId && selected?.id === addedFolderId,
    );
    useEffect(() => {
        if (!newFolderReady || page !== "Managed folders") return;
        document.getElementById("selected-folder-title")?.focus();
        setAddedFolderId("");
    }, [newFolderReady, page]);
    async function act(action: () => Promise<unknown>, announce = true) {
        if (command.current || connectionError || exitRequested.current) return;
        command.current = true;
        const button = document.activeElement as HTMLElement | null;
        setActive(button?.dataset.actionId ?? "");
        setConfirmation(button?.dataset.confirmation === "true");
        setActionLabel(button?.dataset.actionLabel || "Updating your folders");
        setBusy(true);
        setError("");
        setNotice("");
        try {
            const result = await action();
            if (
                announce &&
                typeof result === "string" &&
                result !== "No changes made."
            )
                setNotice(result);
        } catch (e) {
            setError((e as Error).message);
        } finally {
            command.current = false;
            setBusy(false);
            setActive("");
            void refresh();
        }
    }
    // Choosing a folder only starts the add: the dialog asks whether ZipXP should also keep it compressed.
    const add = async (path?: string, name?: string) => {
        if (path) {
            setError("");
            setChoice({ path, name: name ?? path });
            return;
        }
        await act(async () => {
            const picked = await request("chooseFolder", {});
            if (picked && typeof picked === "object" && "name" in picked) {
                setError("");
                setChoice({ name: picked.name });
            }
        });
    };
    const confirmAdd = (autoCompress: boolean) =>
        act(async () => {
            const id = await request("addFolder", {
                ...(choice?.path ? { path: choice.path } : {}),
                autoCompress,
            });
            setChoice(null);
            if (id) {
                setAddedFolderId(id);
                setSelectedId(id);
                openPage("Managed folders");
                return autoCompress
                    ? "Folder added. ZipXP is scanning it and will compress eligible files afterwards. You can pause, stop or restore at any time."
                    : "Folder added. ZipXP scans for possible savings and leaves your files as they are.";
            }
        });
    const queue = (
        operation: "Compress" | "Decompress" | "ClearDefault",
        rootId?: string,
    ) => act(() => request("queue", { rootId, operation }));
    const quit = async () => {
        if (!connected || exitRequested.current) return;
        exitRequested.current = true;
        setQuitting(true);
        setNotice("");
        try {
            await request("quit", {});
        } catch (error) {
            exitRequested.current = false;
            setQuitting(false);
            setError((error as Error).message);
        }
    };
    const updateStartup = async (enabled: boolean) => {
        const previous = data?.startup ?? false;
        startupRevision.current++;
        setStartupBusy(true);
        setData((current) =>
            current ? { ...current, startup: enabled } : current,
        );
        try {
            await request("startup", { enabled });
        } catch (error) {
            startupRevision.current++;
            setData((current) =>
                current ? { ...current, startup: previous } : current,
            );
            setError((error as Error).message);
        } finally {
            setStartupBusy(false);
        }
    };
    const reveal = async (jobId: string) => {
        try {
            await request("showFile", { jobId });
        } catch (error) {
            setError((error as Error).message);
        }
    };
    const edit = (folder: ManagedFolder) => {
        setError("");
        setEditing(folder);
    };
    const failed = data?.jobs.filter((j) => j.state === "Failed").length ?? 0;
    const openFailures = () => {
        setActivityFilter("Failed");
        openPage("Activity");
    };
    const working = folders.find(runActive);
    const footerStatus = quitting
        ? { Icon: Power, text: "Quitting ZipXP…" }
        : busy
          ? { Icon: Activity, text: "Working on your request…" }
          : connectionError
            ? { Icon: Unplug, text: "Engine connection unavailable" }
            : data?.paused
              ? { Icon: Pause, text: "Background work paused" }
              : working?.run
                ? {
                      Icon: Activity,
                      text:
                          working.run.phase === "waiting"
                              ? `${working.name}: ${working.run.waiting ?? "waiting until conditions allow work"}`
                              : `${runVerb(working.run)} ${working.name}: ${runDone(working.run)}/${working.run.total}`,
                  }
                : data && !folders.length
                  ? { Icon: Folder, text: "No folders yet" }
                  : data?.currentFile
                    ? { Icon: Activity, text: `Working on ${data.currentFile}` }
                    : {
                          Icon: data ? Eye : Unplug,
                          text: data?.status ?? "Not connected",
                      };
    return (
        <ActionState.Provider
            value={{
                busy: busy || quitting || Boolean(connectionError),
                active: quitting ? "" : active,
            }}
        >
            <div className="app-shell">
                <a className="skip-link" href="#workspace">
                    Skip to content
                </a>
                <aside className="sidebar">
                    <button
                        className="brand"
                        onClick={() => setPage("Overview")}
                        title="Open Overview"
                    >
                        <span className="brand-icon">
                            <ArrowDownToLine size={23} />
                        </span>
                        <span>
                            ZipXP<small>Local storage manager</small>
                        </span>
                    </button>
                    <nav aria-label="Main navigation">
                        {pages.map(({ name, icon: Icon }) => (
                            <button
                                key={name}
                                className={`nav-item ${page === name ? "active" : ""}`}
                                aria-label={
                                    name === "Activity" ? undefined : name
                                }
                                title={name}
                                aria-current={
                                    page === name ? "page" : undefined
                                }
                                onClick={() => setPage(name)}
                            >
                                <Icon size={19} />
                                <span>{name}</span>
                                {name === "Activity" && failed > 0 && (
                                    <span
                                        className="badge"
                                        role="img"
                                        aria-label={`${failed} failed ${failed === 1 ? "file" : "files"}`}
                                        title={`${failed} failed ${failed === 1 ? "file" : "files"}`}
                                    >
                                        {failed}
                                    </span>
                                )}
                            </button>
                        ))}
                    </nav>
                    <div className="sidebar-bottom">
                        <div className="local-note">
                            <ShieldCheck size={19} />
                            <div>
                                Files stay on your PC.
                                <small>No cloud. No uploads.</small>
                            </div>
                        </div>
                        <label className="startup">
                            <input
                                type="checkbox"
                                disabled={!data || startupBusy || quitting}
                                checked={data?.startup ?? false}
                                onChange={(e) =>
                                    void updateStartup(e.target.checked)
                                }
                            />
                            Start when I sign in
                        </label>
                        <small className="sidebar-hint">
                            Background work needs ZipXP running.
                        </small>
                        <button
                            className="nav-item"
                            disabled={!connected || quitting}
                            onClick={() => void quit()}
                        >
                            {quitting ? (
                                <LoaderCircle size={17} className="spin" />
                            ) : (
                                <Power size={17} />
                            )}
                            {quitting ? "Quitting ZipXP…" : "Quit ZipXP"}
                        </button>
                    </div>
                </aside>
                <main id="workspace" tabIndex={-1}>
                    <header>
                        <div>
                            <h1 ref={heading} tabIndex={-1}>
                                {page}
                            </h1>
                            <p>{hints[page]}</p>
                        </div>
                        <div className="actions">
                            {folders.length > 0 && !data?.paused && (
                                <ActionButton
                                    onClick={() =>
                                        void act(
                                            () =>
                                                request("pause", {
                                                    paused: true,
                                                }),
                                            false,
                                        )
                                    }
                                >
                                    <Pause size={16} />
                                    Pause all folders
                                </ActionButton>
                            )}
                            {((page !== "Overview" &&
                                page !== "Managed folders") ||
                                folders.length > 0) && (
                                <ActionButton
                                    disabled={!data}
                                    onClick={() => void add()}
                                >
                                    <FolderPlus size={17} />
                                    Add folder
                                </ActionButton>
                            )}
                        </div>
                    </header>
                    {connectionError && !quitting && (
                        <div role="alert" className="message error">
                            <AlertCircle size={19} />
                            <div>
                                <strong>
                                    {connected
                                        ? "Engine connection unavailable"
                                        : "Desktop app unavailable"}
                                </strong>
                                <p>{connectionError}</p>
                            </div>
                            <button onClick={() => void refresh()}>
                                <RefreshCw size={16} />
                                Retry connection
                            </button>
                        </div>
                    )}
                    {actionError && !editing && !choice && !quitting && (
                        <div role="alert" className="message error">
                            <AlertCircle size={19} />
                            <div>
                                <strong>That action couldn’t finish</strong>
                                <p>{actionError}</p>
                            </div>
                            <button
                                aria-label="Dismiss error"
                                onClick={() => setError("")}
                            >
                                <X size={16} />
                            </button>
                        </div>
                    )}
                    {notice && !quitting && (
                        <div role="status" className="message">
                            <Check size={18} />
                            <span>{notice}</span>
                            <button
                                aria-label="Dismiss message"
                                onClick={() => setNotice("")}
                            >
                                <X size={16} />
                            </button>
                        </div>
                    )}
                    {(busy || quitting) && (
                        <div role="status" className="progress-strip">
                            <Clock3 size={18} />
                            <div>
                                <strong>
                                    {quitting
                                        ? "Quitting ZipXP…"
                                        : `${actionLabel}…`}
                                </strong>
                                <span>
                                    {quitting
                                        ? "Stopping background work. Pending work stays queued for next time."
                                        : confirmation
                                          ? "Review the Windows preview or confirmation dialog to continue. You can still browse and search."
                                          : "Folder actions will be available when this request finishes. You can still browse and search."}
                                </span>
                            </div>
                        </div>
                    )}
                    {!quitting && data?.paused && (
                        <div className="message warning">
                            <Pause size={19} />
                            <div>
                                <strong>All background work is paused</strong>
                                <p>
                                    The current file will finish. New work waits
                                    until you resume.
                                </p>
                            </div>
                            <ActionButton
                                onClick={() =>
                                    void act(
                                        () =>
                                            request("pause", { paused: false }),
                                        false,
                                    )
                                }
                            >
                                <Play size={16} />
                                Resume all folders
                            </ActionButton>
                        </div>
                    )}
                    {quitting && (
                        <section
                            className="shutdown-state"
                            aria-label="Shutdown information"
                        >
                            <Power size={32} />
                            <h2>See you next time</h2>
                            <p>
                                Your folders and settings are saved on this PC.
                            </p>
                        </section>
                    )}
                    {!quitting &&
                        (!data ? (
                            <section className="card">
                                <Empty
                                    icon={connected ? "connection" : "folder"}
                                    title={
                                        connectionError
                                            ? connected
                                                ? "The engine isn’t responding"
                                                : "Open ZipXP on Windows"
                                            : "Connecting to ZipXP…"
                                    }
                                >
                                    <p>
                                        {connected
                                            ? connectionError
                                                ? "Retry the connection to load your folders."
                                                : "Waiting for the local compression engine."
                                            : "Use the Windows desktop app to manage folders and compress files."}
                                    </p>
                                    {!connectionError && (
                                        <LoaderCircle className="spin" />
                                    )}
                                </Empty>
                            </section>
                        ) : (
                            <div className="workspace">
                                {page === "Overview" && (
                                    <Overview
                                        hasBanner={
                                            busy ||
                                            Boolean(actionError) ||
                                            Boolean(connectionError) ||
                                            Boolean(notice) ||
                                            data.paused
                                        }
                                        data={data}
                                        add={add}
                                        queue={queue}
                                        manage={() =>
                                            openPage("Managed folders")
                                        }
                                        openFailures={openFailures}
                                    />
                                )}
                                {page === "Managed folders" && (
                                    <FolderPanel
                                        globallyPaused={data.paused}
                                        openFailures={openFailures}
                                        dismissed={dismissedRuns}
                                        dismiss={(key) =>
                                            setDismissedRuns((d) => [...d, key])
                                        }
                                        folders={folders}
                                        selected={selected}
                                        adding={Boolean(addedFolderId)}
                                        select={selectFolder}
                                        add={add}
                                        edit={edit}
                                        browse={() =>
                                            openPage("Storage insights")
                                        }
                                        queue={queue}
                                        act={act}
                                    />
                                )}
                                {page === "Storage insights" && (
                                    <InsightsPage
                                        key={selected?.id ?? "empty"}
                                        folders={folders}
                                        selected={selected}
                                        select={selectFolder}
                                        act={act}
                                    />
                                )}
                                {page === "Activity" && (
                                    <ActivityPage
                                        data={data}
                                        state={activityFilter}
                                        setState={setActivityFilter}
                                        act={act}
                                        reveal={reveal}
                                    />
                                )}
                            </div>
                        ))}
                    <footer>
                        <footerStatus.Icon size={14} />
                        <span title={footerStatus.text}>
                            {footerStatus.text}
                        </span>
                        <span className="footer-brand">
                            <ShieldCheck size={14} />
                            Local NTFS compression
                        </span>
                    </footer>
                </main>
                {choice && (
                    <AddFolderDialog
                        name={choice.name}
                        busy={busy}
                        error={actionError}
                        close={() => {
                            setChoice(null);
                            setError("");
                            void request("discardPick", {}).catch(() => {});
                        }}
                        confirm={(auto) => void confirmAdd(auto)}
                    />
                )}
                {editing && (
                    <PolicyDialog
                        folder={editing}
                        busy={busy}
                        error={actionError}
                        close={() => {
                            setEditing(null);
                            setError("");
                        }}
                        save={(policy) =>
                            void act(async () => {
                                const result = await request("savePolicy", {
                                    rootId: editing.id,
                                    policy,
                                });
                                setEditing(null);
                                return result;
                            })
                        }
                    />
                )}
            </div>
        </ActionState.Provider>
    );
}

function Overview({
    hasBanner,
    data,
    add,
    queue,
    manage,
    openFailures,
}: {
    hasBanner: boolean;
    data: Snapshot;
    add: (path?: string, name?: string) => Promise<void>;
    queue: (op: "Compress" | "Decompress", id?: string) => Promise<void>;
    manage: () => void;
    openFailures: () => void;
}) {
    const { folders } = data;
    if (!folders.length)
        return (
            <section className="card welcome">
                <Empty title="Free up space without deleting anything">
                    <p>
                        Pick a folder to see how much ZipXP could save. Scanning
                        only reads your files.
                    </p>
                    <SuggestedFolders add={add} />
                    <ActionButton
                        className="primary"
                        onClick={() => void add()}
                    >
                        <FolderPlus size={17} />
                        Choose a folder
                    </ActionButton>
                    <div className="trust-row">
                        <ShieldCheck size={17} />
                        Same files, same names. Nothing is deleted, and Restore
                        undoes compression.
                    </div>
                </Empty>
            </section>
        );
    const scanned = folders.filter((f) => f.scan).length;
    const eligible = folders.filter(
        (f) => !f.policy.paused && (f.scan?.candidates ?? 0) > 0,
    );
    const enabled = folders.filter((f) => f.policy.enabled).length;
    const estimate = folders.reduce(
        (sum, f) => sum + (f.scan?.estimatedSaving ?? 0),
        0,
    );
    const { delta, measured } = data.savings;
    const runs = folders.filter(runActive);
    const compressing = runs.filter((f) => f.run!.operation === "Compress");
    const soFar = compressing.reduce((sum, f) => sum + f.run!.savedSoFar, 0);
    const soFarFiles = compressing.reduce(
        (sum, f) => sum + runDone(f.run!) - f.run!.failed - f.run!.skipped,
        0,
    );
    const compressNote = data.paused
        ? "Resume all folders to compress them."
        : eligible.length > 0
          ? "Preview one-time compression for folders with eligible files."
          : !scanned
            ? "One-time compression is available after the first scan."
            : folders.some((f) => (f.scan?.candidates ?? 0) > 0)
              ? "Resume the folders with eligible files to compress them."
              : "The scans found no files to compress right now.";
    const pending = data.jobs.filter((j) => j.pending).length;
    const failed = data.jobs.filter((j) => j.state === "Failed").length;
    return (
        <>
            {failed > 0 && !hasBanner && (
                <div className="attention-card">
                    <span className="attention-icon">
                        <AlertCircle size={20} />
                    </span>
                    <div>
                        <strong id="attention-heading">
                            {failed}{" "}
                            {failed === 1 ? "file needs" : "files need"}{" "}
                            attention
                        </strong>
                        <p>
                            Some files couldn’t be processed. Review the details
                            and retry when ready.
                        </p>
                    </div>
                    <button
                        aria-describedby="attention-heading"
                        onClick={openFailures}
                    >
                        Review activity <ArrowUpRight size={16} />
                    </button>
                </div>
            )}
            <section
                className={`savings card${measured > 0 ? " measured-first" : ""}`}
            >
                <div className="savings-copy">
                    <h2 className="savings-label">Estimated savings</h2>
                    <strong className="savings-value">
                        {!scanned
                            ? "Waiting for scan"
                            : estimate
                              ? bytes(estimate)
                              : "None found yet"}
                    </strong>
                    <p>
                        {!scanned
                            ? "ZipXP will estimate what compression could save."
                            : estimate
                              ? "Based on the latest scans. Actual savings may differ."
                              : "The latest scans found no additional savings."}
                    </p>
                    <div className="actions wrap">
                        <button className="primary" onClick={manage}>
                            Manage compression <ArrowUpRight size={16} />
                        </button>
                        <ActionButton
                            confirmation
                            disabled={!eligible.length || data.paused}
                            aria-describedby="compress-all-note"
                            onClick={() => void queue("Compress")}
                        >
                            <Sparkles size={17} />
                            Compress all once
                        </ActionButton>
                    </div>
                    <p className="scope-note" id="compress-all-note">
                        {compressNote}
                    </p>
                </div>
                <div className="savings-result">
                    <h2 className="savings-label">Space saved by ZipXP</h2>
                    <strong className="savings-value">
                        {!measured
                            ? "Nothing compressed yet"
                            : delta > 0
                              ? bytes(delta)
                              : delta < 0
                                ? `${bytes(-delta)} more used`
                                : "No net change"}
                    </strong>
                    <p>
                        {compressing.length > 0 ? (
                            <>
                                <SavedSoFar value={soFar} /> saved so far from{" "}
                                {soFarFiles.toLocaleString()}{" "}
                                {soFarFiles === 1 ? "file" : "files"}, still
                                working.
                            </>
                        ) : (
                            "All-time net change from ZipXP’s compression and restores."
                        )}
                    </p>
                </div>
            </section>
            <div className="stats-grid">
                <section className="card stat">
                    <span className="stat-icon">
                        <HardDrive size={20} />
                    </span>
                    <div className="section-label">FOLDER SIZE ON DISK</div>
                    <strong>{scanned ? bytes(data.allocated) : "—"}</strong>
                    <p>Files measured in your managed folders.</p>
                </section>
                <section className="card stat">
                    <span className="stat-icon">
                        <Folder size={20} />
                    </span>
                    <div className="section-label">AUTO-COMPRESSION</div>
                    <strong>
                        {enabled}
                        <small> of {folders.length} folders</small>
                    </strong>
                    <p>
                        {enabled
                            ? "Covers existing, new and changed files."
                            : "Off for all folders."}
                    </p>
                </section>
                <section className="card stat">
                    <span className="stat-icon">
                        <Activity size={20} />
                    </span>
                    <div className="section-label">BACKGROUND QUEUE</div>
                    <strong>
                        {pending}
                        <small> active or waiting</small>
                    </strong>
                    {failed > 0 && hasBanner ? (
                        <button
                            className="stat-attention"
                            aria-label={`${failed} ${failed === 1 ? "file needs" : "files need"} attention, Review activity`}
                            onClick={openFailures}
                        >
                            <AlertCircle size={15} />
                            <span>
                                {failed}{" "}
                                {failed === 1 ? "file needs" : "files need"}{" "}
                                attention
                            </span>
                            <ArrowUpRight size={14} />
                        </button>
                    ) : (
                        <p>
                            {data.paused
                                ? "Paused until you resume."
                                : runs.length === 1 && runs[0].run
                                  ? `${runVerb(runs[0].run)} ${runs[0].name}: ${runDone(runs[0].run)}/${runs[0].run.total}.`
                                  : runs.length > 1
                                    ? `Working on ${runs.length} folders.`
                                    : data.currentFile
                                      ? "Processing a file in the background."
                                      : pending
                                        ? "Queued files will run when conditions allow."
                                        : enabled
                                          ? "Watching for file changes."
                                          : "No work queued."}
                        </p>
                    )}
                </section>
            </div>
            <section className="card">
                <div className="section-heading">
                    <h2>Your drives</h2>
                    <span className="muted">Current disk usage</span>
                </div>
                <div className="drives">
                    {data.volumes.map((v) => (
                        <div className="drive" key={v.name}>
                            <HardDrive size={23} />
                            <div>
                                <div className="bar-label">
                                    <strong>{v.name}</strong>
                                    <span>
                                        {bytes(v.free)} free of {bytes(v.total)}
                                    </span>
                                </div>
                                <Meter
                                    value={1 - v.free / Math.max(1, v.total)}
                                    label={`${v.name} used space`}
                                />
                            </div>
                        </div>
                    ))}
                    {!data.volumes.length && (
                        <p>Drive information is currently unavailable.</p>
                    )}
                </div>
            </section>
            {data.unknown > 0 && (
                <div className="message warning">
                    <AlertCircle size={18} />
                    <span>
                        Some items were skipped or couldn’t be measured. The
                        folder size includes measured files only.
                    </span>
                </div>
            )}
        </>
    );
}

function MoreActions({ children }: { children: ReactNode }) {
    const ref = useRef<HTMLDetailsElement>(null);
    useEffect(() => {
        const outside = (e: PointerEvent) => {
            if (!ref.current?.contains(e.target as Node))
                ref.current?.removeAttribute("open");
        };
        const escape = (e: KeyboardEvent) => {
            if (e.key === "Escape" && ref.current?.open) {
                ref.current.open = false;
                ref.current.querySelector("summary")?.focus();
            }
        };
        document.addEventListener("pointerdown", outside);
        document.addEventListener("keydown", escape);
        return () => {
            document.removeEventListener("pointerdown", outside);
            document.removeEventListener("keydown", escape);
        };
    }, []);
    return (
        <details className="more-actions" ref={ref}>
            <summary>
                <MoreHorizontal size={17} />
                More actions
                <ChevronDown size={14} />
            </summary>
            <div
                className="action-menu"
                onClick={(e) => {
                    const button = (e.target as HTMLElement).closest("button");
                    if (
                        button &&
                        !button.disabled &&
                        button.getAttribute("aria-disabled") !== "true"
                    ) {
                        ref.current?.removeAttribute("open");
                        ref.current?.querySelector("summary")?.focus();
                    }
                }}
            >
                {children}
            </div>
        </details>
    );
}
function FolderPanel({
    globallyPaused,
    openFailures,
    dismissed,
    dismiss,
    folders,
    selected,
    adding,
    select,
    add,
    edit,
    browse,
    queue,
    act,
}: {
    globallyPaused: boolean;
    openFailures: () => void;
    dismissed: string[];
    dismiss: (key: string) => void;
    folders: ManagedFolder[];
    selected?: ManagedFolder;
    adding: boolean;
    select: (id: string) => void;
    add: (path?: string, name?: string) => Promise<void>;
    edit: (f: ManagedFolder) => void;
    browse: () => void;
    queue: (
        op: "Compress" | "Decompress" | "ClearDefault",
        id: string,
    ) => Promise<void>;
    act: Act;
}) {
    const scroll = useScrollEdges(folders.length);
    const outcome = selected ? folderOutcome(selected) : null;
    const run = selected?.run ?? null;
    const finished = run && run.phase === "done" ? run : null;
    const finishedKey =
        selected && finished ? `${selected.id}:${finished.startedUtc}` : "";
    const succeeded = finished
        ? finished.total - finished.failed - finished.skipped
        : 0;
    // Background auto-compression finishes small runs constantly; only report the ones worth reading.
    const showFinished = Boolean(
        selected &&
        finished &&
        !dismissed.includes(finishedKey) &&
        // Don't resurface a run that ended long ago (it is stored across restarts).
        (!finished.finishedUtc ||
            Date.now() - Date.parse(finished.finishedUtc) < 3_600_000) &&
        (!selected.policy.enabled ||
            finished.savedSoFar >= 1048576 ||
            finished.failed > 0),
    );
    // Compressing once is the gentle first step; background automation comes second.
    const compressFirst = Boolean(
        selected &&
        !selected.policy.enabled &&
        !selected.policy.paused &&
        !globallyPaused &&
        (selected.scan?.candidates ?? 0) > 0,
    );
    const turnOnAuto = () =>
        selected
            ? void act(() =>
                  request("setAutoCompression", {
                      rootId: selected.id,
                      enabled: true,
                  }),
              )
            : undefined;
    return (
        <section className="card folders-card">
            {adding && !selected ? (
                <Empty title="Loading your new folder…">
                    <p>ZipXP will scan it for possible savings.</p>
                </Empty>
            ) : !folders.length ? (
                <Empty title="Choose a folder to manage">
                    <p>
                        ZipXP scans for possible savings. After the scan, choose
                        whether to compress once or turn on auto-compression.
                    </p>
                    <ActionButton
                        className="primary"
                        onClick={() => void add()}
                    >
                        Add folder
                    </ActionButton>
                </Empty>
            ) : (
                <>
                    <div className="section-heading">
                        <h2>
                            Your folders{" "}
                            <span className="count">{folders.length}</span>
                        </h2>
                        <span className="muted">
                            {scroll.above || scroll.below
                                ? "Scroll to browse folders"
                                : "Select a folder for actions"}
                        </span>
                    </div>
                    <div
                        className="folder-list"
                        ref={scroll.ref}
                        data-above={scroll.above}
                        role="list"
                        aria-label="Managed folders"
                    >
                        {folders.map((f) => (
                            <div
                                key={f.id}
                                role="listitem"
                                className={`folder-row ${selected?.id === f.id ? "selected" : ""}`}
                            >
                                <button
                                    className="folder-main"
                                    onClick={() => select(f.id)}
                                    aria-pressed={selected?.id === f.id}
                                >
                                    <span className="folder-icon">
                                        <Folder size={23} />
                                    </span>
                                    <span>
                                        <strong title={f.name}>{f.name}</strong>
                                        <small title={f.path}>{f.path}</small>
                                    </span>
                                </button>
                                <div className="folder-size">
                                    <strong>
                                        {bytes(f.scan?.allocated)}
                                        <span className="sr-only">
                                            {" "}
                                            used on disk
                                        </span>
                                    </strong>
                                    <small>{folderOutcome(f).short}</small>
                                </div>
                                {runActive(f) && f.run ? (
                                    <span
                                        className={`pill progress ${f.run.phase === "waiting" ? "amber" : ""}`}
                                    >
                                        {f.run.phase === "waiting"
                                            ? "Waiting"
                                            : f.run.phase === "queued"
                                              ? "Queued"
                                              : f.run.operation === "Decompress"
                                                ? "Restoring"
                                                : f.run.operation ===
                                                    "ClearDefault"
                                                  ? "Working"
                                                  : "Compressing"}{" "}
                                        · {runPercent(f.run)}%
                                    </span>
                                ) : (
                                    <span
                                        className={`pill ${f.policy.paused || (globallyPaused && f.policy.enabled) ? "amber" : f.policy.enabled ? "green" : ""}`}
                                    >
                                        {f.policy.enabled
                                            ? f.policy.paused || globallyPaused
                                                ? "Auto on · paused"
                                                : "Auto on"
                                            : f.policy.paused
                                              ? "Auto off · paused"
                                              : "Auto off"}
                                    </span>
                                )}
                            </div>
                        ))}
                    </div>
                    {selected && (
                        <div className="folder-detail">
                            <div className="section-heading">
                                <div>
                                    <h3
                                        id="selected-folder-title"
                                        tabIndex={-1}
                                        title={selected.path}
                                    >
                                        {selected.name}
                                    </h3>
                                    <p>
                                        {selected.policy.paused ||
                                        globallyPaused ? (
                                            <Pause size={14} />
                                        ) : (
                                            <Eye size={14} />
                                        )}
                                        {globallyPaused
                                            ? "Background work paused globally"
                                            : selected.policy.paused
                                              ? "This folder is paused. Resume it from More actions."
                                              : selected.status}
                                    </p>
                                </div>
                                <button
                                    aria-label={`Folder settings for ${selected.name}`}
                                    onClick={() => edit(selected)}
                                >
                                    <Settings2 size={16} />
                                    Folder settings
                                </button>
                            </div>
                            {runActive(selected) && (
                                <RunCard folder={selected} act={act} />
                            )}
                            {showFinished && finished && (
                                <div className="success-card">
                                    <span className="success-icon">
                                        <Check size={18} />
                                    </span>
                                    <div>
                                        <strong>
                                            {finished.operation === "Decompress"
                                                ? `Restored ${succeeded.toLocaleString()} ${runUnit(finished, succeeded)} to their original size.`
                                                : finished.operation ===
                                                    "ClearDefault"
                                                  ? "Windows folder compression is turned off."
                                                  : finished.savedSoFar > 0
                                                    ? `Done. ZipXP saved ${bytes(finished.savedSoFar)} across ${succeeded.toLocaleString()} ${runUnit(finished, succeeded)}.`
                                                    : "Finished. No space was saved."}
                                        </strong>
                                        <p>
                                            {finished.failed > 0
                                                ? `${finished.failed} ${runUnit(finished, finished.failed)} couldn’t be processed. `
                                                : ""}
                                            {finished.skipped > 0
                                                ? `${finished.skipped} ${finished.skipped === 1 ? "was" : "were"} skipped. `
                                                : ""}
                                            Your files open as usual.
                                            {finished.operation ===
                                                "Compress" &&
                                            !selected.policy.enabled
                                                ? " New and changed files stay as they are unless auto-compression is on."
                                                : ""}
                                        </p>
                                    </div>
                                    <div className="actions wrap">
                                        {finished.operation === "Compress" &&
                                            !selected.policy.enabled &&
                                            finished.savedSoFar > 0 && (
                                                <ActionButton
                                                    className="primary"
                                                    confirmation
                                                    onClick={turnOnAuto}
                                                >
                                                    Keep compressing
                                                    automatically
                                                </ActionButton>
                                            )}
                                        {finished.failed > 0 && (
                                            <button onClick={openFailures}>
                                                Review activity
                                            </button>
                                        )}
                                        <button
                                            onClick={() => dismiss(finishedKey)}
                                        >
                                            Dismiss
                                        </button>
                                    </div>
                                </div>
                            )}
                            <div className="folder-compression-summary">
                                <strong>
                                    Auto-compression is{" "}
                                    {selected.policy.enabled ? "on" : "off"}.
                                </strong>
                                <p>
                                    {selected.policy.enabled
                                        ? "ZipXP checks existing, new and changed files while it runs."
                                        : "Turn it on for existing and future files, or compress eligible files once."}
                                </p>
                                {Boolean(selected.scan?.nativeDefaults) && (
                                    <p>
                                        Windows can also compress new files here
                                        using its own folder setting.
                                    </p>
                                )}
                                <p>{outcome?.detail}</p>
                                {outcome?.another && (
                                    <div>
                                        <ActionButton
                                            onClick={() => void add()}
                                        >
                                            <FolderPlus size={16} />
                                            Try another folder
                                        </ActionButton>
                                    </div>
                                )}
                            </div>
                            <div className="actions wrap">
                                <ActionButton
                                    className={
                                        compressFirst ? "primary" : undefined
                                    }
                                    disabled={
                                        !selected.scan?.candidates ||
                                        globallyPaused ||
                                        selected.policy.paused
                                    }
                                    title={
                                        globallyPaused
                                            ? "Resume all folders before compressing"
                                            : selected.policy.paused
                                              ? "Resume this folder before compressing"
                                              : !selected.scan
                                                ? "Wait for the first scan"
                                                : selected.scan.candidates === 0
                                                  ? "No files currently benefit from compression"
                                                  : "Preview a one-time compression job"
                                    }
                                    confirmation
                                    onClick={() =>
                                        void queue("Compress", selected.id)
                                    }
                                >
                                    <Sparkles size={17} />
                                    Compress once
                                </ActionButton>
                                <ActionButton
                                    className={
                                        selected.policy.enabled ||
                                        compressFirst ||
                                        !selected.scan?.candidates
                                            ? undefined
                                            : "primary"
                                    }
                                    confirmation
                                    onClick={() =>
                                        void act(() =>
                                            request("setAutoCompression", {
                                                rootId: selected.id,
                                                enabled:
                                                    !selected.policy.enabled,
                                            }),
                                        )
                                    }
                                >
                                    Turn{" "}
                                    {selected.policy.enabled ? "off" : "on"}{" "}
                                    auto-compression
                                </ActionButton>
                                {selected.canRestore && (
                                    <ActionButton
                                        confirmation
                                        onClick={() =>
                                            void queue(
                                                "Decompress",
                                                selected.id,
                                            )
                                        }
                                    >
                                        Restore files
                                    </ActionButton>
                                )}
                                <button onClick={browse}>
                                    Browse files <ArrowUpRight size={16} />
                                </button>
                                <MoreActions>
                                    <ActionButton
                                        onClick={() =>
                                            void act(() =>
                                                request("scan", {
                                                    rootId: selected.id,
                                                }),
                                            )
                                        }
                                    >
                                        <RefreshCw size={16} /> Scan again
                                    </ActionButton>
                                    <ActionButton
                                        onClick={() =>
                                            void act(() =>
                                                request("pauseFolder", {
                                                    rootId: selected.id,
                                                    paused: !selected.policy
                                                        .paused,
                                                }),
                                            )
                                        }
                                    >
                                        {selected.policy.paused ? (
                                            <Play size={16} />
                                        ) : (
                                            <Pause size={16} />
                                        )}
                                        {selected.policy.paused
                                            ? "Resume folder"
                                            : "Pause folder"}
                                    </ActionButton>
                                    <ActionButton
                                        actionLabel="Turn off Windows compression"
                                        confirmation
                                        disabled={
                                            !selected.scan?.nativeDefaults
                                        }
                                        onClick={() =>
                                            void queue(
                                                "ClearDefault",
                                                selected.id,
                                            )
                                        }
                                    >
                                        <div>
                                            <strong>
                                                Turn off Windows compression
                                            </strong>
                                            <small>
                                                {selected.scan?.nativeDefaults
                                                    ? "Change Windows’ own folder setting. ZipXP auto-compress is separate."
                                                    : "Windows’ folder setting is already off. ZipXP auto-compress is separate."}
                                            </small>
                                        </div>
                                    </ActionButton>
                                    <ActionButton
                                        actionLabel="Stop managing this folder"
                                        confirmation
                                        className="danger"
                                        onClick={() =>
                                            void act(() =>
                                                request("remove", {
                                                    rootId: selected.id,
                                                }),
                                            )
                                        }
                                    >
                                        <div>
                                            <strong>
                                                Stop managing this folder
                                            </strong>
                                            <small>
                                                Remove it from ZipXP. Leave its
                                                files as they are.
                                            </small>
                                        </div>
                                    </ActionButton>
                                </MoreActions>
                            </div>
                            <div className="folder-scan-note">
                                <Clock3 size={14} />
                                {selected.scan
                                    ? `Last scanned ${when(selected.scan.utc)} · ${selected.scan.files.toLocaleString()} files`
                                    : "Waiting for the first scan"}
                            </div>
                            <SkipDetails folder={selected} />
                            {selected.scan &&
                                (!selected.scan.complete ||
                                    selected.scan.unknown > 0 ||
                                    selected.scan.errors > 0) && (
                                    <div className="message warning">
                                        <AlertCircle size={17} />
                                        <span>
                                            Some items were skipped or couldn’t
                                            be checked. Use Browse files to see
                                            details.
                                        </span>
                                    </div>
                                )}
                        </div>
                    )}
                </>
            )}
        </section>
    );
}

function InsightsPage({
    folders,
    selected,
    select,
    act,
}: {
    folders: ManagedFolder[];
    selected?: ManagedFolder;
    select: (id: string) => void;
    act: Act;
}) {
    const [path, setPath] = useState(""),
        [largest, setLargest] = useState(false),
        [search, setSearch] = useState("");
    const [result, setResult] = useState<Insights | null>(null),
        [error, setError] = useState(""),
        [loading, setLoading] = useState(true);
    const [checked, setChecked] = useState<string[]>([]),
        [retry, setRetry] = useState(0);
    const [sort, setSort] = useState<"allocated" | "logical" | "name">(
        "allocated",
    );
    // Scan changes refresh the view; unrelated snapshot/job polls do not interrupt a search.
    const scanUtc = selected?.scan?.utc;
    useEffect(() => {
        if (!selected) return;
        let active = true;
        setLoading(true);
        setError("");
        setChecked([]);
        const timer = setTimeout(() => {
            request("insights", {
                rootId: selected.id,
                path,
                largest,
                search,
                sort,
            })
                .then((value) => {
                    if (active) {
                        setResult(value);
                        setLoading(false);
                    }
                })
                .catch((e) => {
                    if (active) {
                        setError(e.message);
                        setLoading(false);
                    }
                });
        }, 180);
        return () => {
            active = false;
            clearTimeout(timer);
        };
    }, [selected?.id, scanUtc, path, largest, search, sort, retry]);
    const rows = result?.rows ?? [];
    const total = rows.reduce((sum, r) => sum + (r.allocated ?? 0), 0);
    const selectable = rows
        .filter((r) => !r.directory && r.compressed)
        .map((r) => r.path);
    const crumbs =
        selected && result
            ? result.path
                  .slice(selected.path.length)
                  .split("\\")
                  .filter(Boolean)
            : [];
    const navigate = (next: string) => {
        const destination = next === selected?.path ? "" : next;
        if (destination === path) return;
        setPath(destination);
        setResult(null);
        setChecked([]);
    };
    const switchView = (next: boolean) => {
        if (next === largest) return;
        setLargest(next);
        setPath("");
        setResult(null);
        setChecked([]);
    };
    return (
        <section className="card">
            {!selected ? (
                <Empty title="Explore your storage">
                    <p>Add a managed folder to browse the files inside it.</p>
                </Empty>
            ) : (
                <>
                    <div className="insight-toolbar">
                        <label>
                            Folder
                            <select
                                aria-label="Folder to inspect"
                                value={selected.id}
                                onChange={(e) => select(e.target.value)}
                            >
                                {folders.map((f) => (
                                    <option key={f.id} value={f.id}>
                                        {f.name}
                                    </option>
                                ))}
                            </select>
                        </label>
                        <div className="segmented" aria-label="Storage view">
                            <button
                                aria-pressed={!largest}
                                className={!largest ? "chosen" : ""}
                                onClick={() => switchView(false)}
                            >
                                By folder
                            </button>
                            <button
                                aria-pressed={largest}
                                className={largest ? "chosen" : ""}
                                onClick={() => switchView(true)}
                            >
                                Largest files
                            </button>
                        </div>
                        <label className="search">
                            <Search size={17} />
                            <input
                                aria-label="Search files"
                                placeholder="Search files…"
                                value={search}
                                onChange={(e) => setSearch(e.target.value)}
                            />
                        </label>
                    </div>
                    <div className="path-row">
                        <button
                            aria-label="Go to parent folder"
                            disabled={!result?.parent || largest || loading}
                            onClick={() => navigate(result!.parent!)}
                        >
                            <ArrowLeft size={16} />
                        </button>
                        <nav className="breadcrumbs" aria-label="Folder path">
                            <button onClick={() => navigate("")}>
                                {selected.name}
                            </button>
                            {crumbs.map((part, i) => (
                                <span key={i}>
                                    <ChevronRight size={14} />
                                    <button
                                        aria-current={
                                            i === crumbs.length - 1
                                                ? "location"
                                                : undefined
                                        }
                                        onClick={() =>
                                            navigate(
                                                selected.path +
                                                    "\\" +
                                                    crumbs
                                                        .slice(0, i + 1)
                                                        .join("\\"),
                                            )
                                        }
                                    >
                                        {part}
                                    </button>
                                </span>
                            ))}
                        </nav>
                        <ActionButton
                            disabled={
                                !checked.length || loading || Boolean(error)
                            }
                            onClick={() =>
                                void act(async () => {
                                    const message = await request(
                                        "restoreSelected",
                                        { rootId: selected.id, paths: checked },
                                    );
                                    setChecked([]);
                                    return message;
                                })
                            }
                        >
                            Restore selected
                            {checked.length > 0 ? ` (${checked.length})` : ""}
                        </ActionButton>
                    </div>
                    <div className="table-meta">
                        <span role="status">
                            {loading ? (
                                <>
                                    <LoaderCircle size={14} className="spin" />
                                    {result
                                        ? "Updating results…"
                                        : "Loading files…"}
                                </>
                            ) : error ? (
                                "Couldn’t update files"
                            ) : (
                                `${result?.totalRows.toLocaleString() ?? 0} matching items`
                            )}
                        </span>
                        <label>
                            Sort by
                            <select
                                aria-label="Sort storage items"
                                value={sort}
                                onChange={(e) =>
                                    setSort(
                                        e.target.value as
                                            "allocated" | "logical" | "name",
                                    )
                                }
                            >
                                <option value="allocated">
                                    Used now: largest first
                                </option>
                                <option value="logical">
                                    Original size: largest first
                                </option>
                                <option value="name">Name: A–Z</option>
                            </select>
                        </label>
                    </div>
                    {error && (
                        <div role="alert" className="message error table-error">
                            <AlertCircle size={18} />
                            <span>{error}</span>
                            <button onClick={() => setRetry((n) => n + 1)}>
                                <RefreshCw size={16} />
                                Try again
                            </button>
                        </div>
                    )}
                    <p className="table-scroll-hint" id="insights-scroll-hint">
                        Scroll horizontally for more columns. File names stay
                        visible.
                    </p>
                    <div
                        aria-describedby="insights-scroll-hint"
                        className={`table-scroll ${loading && result ? "updating" : ""}`}
                        role="region"
                        aria-label="Storage items table"
                        tabIndex={0}
                        aria-busy={loading}
                    >
                        <table className="insights-table">
                            <thead>
                                <tr>
                                    <th className="selection-cell">
                                        {selectable.length > 0 ? (
                                            <input
                                                type="checkbox"
                                                aria-label="Select all restorable files"
                                                disabled={
                                                    loading || Boolean(error)
                                                }
                                                checked={
                                                    selectable.length > 0 &&
                                                    selectable.every((p) =>
                                                        checked.includes(p),
                                                    )
                                                }
                                                onChange={(e) =>
                                                    setChecked(
                                                        e.target.checked
                                                            ? selectable
                                                            : [],
                                                    )
                                                }
                                            />
                                        ) : (
                                            <span className="sr-only">
                                                Selection
                                            </span>
                                        )}
                                    </th>
                                    <th scope="col">Name</th>
                                    <th scope="col">
                                        Share of
                                        <br />
                                        shown items
                                    </th>
                                    <th scope="col" className="number">
                                        Original size
                                    </th>
                                    <th scope="col" className="number">
                                        Used now
                                    </th>
                                    <th scope="col" className="number">
                                        Saved
                                    </th>
                                    <th scope="col">Status</th>
                                </tr>
                            </thead>
                            <tbody>
                                {rows.map((r) => (
                                    <tr key={r.path}>
                                        <td className="selection-cell">
                                            {!r.directory && r.compressed && (
                                                <input
                                                    type="checkbox"
                                                    disabled={
                                                        loading ||
                                                        Boolean(error)
                                                    }
                                                    aria-label={`Select ${r.name}`}
                                                    checked={checked.includes(
                                                        r.path,
                                                    )}
                                                    onChange={(e) =>
                                                        setChecked((old) =>
                                                            e.target.checked
                                                                ? [
                                                                      ...old,
                                                                      r.path,
                                                                  ]
                                                                : old.filter(
                                                                      (p) =>
                                                                          p !==
                                                                          r.path,
                                                                  ),
                                                        )
                                                    }
                                                />
                                            )}
                                        </td>
                                        <td>
                                            {r.directory ? (
                                                <button
                                                    className="file-name folder-link"
                                                    disabled={loading}
                                                    title={r.path}
                                                    onClick={() =>
                                                        navigate(r.path)
                                                    }
                                                >
                                                    <Folder size={18} />
                                                    <span>{r.name}</span>
                                                    <ChevronRight size={14} />
                                                </button>
                                            ) : (
                                                <span
                                                    className="file-name"
                                                    title={r.path}
                                                >
                                                    <FileText size={18} />
                                                    <span>{r.name}</span>
                                                </span>
                                            )}
                                            <small className="compact-insight-meta">
                                                Original {bytes(r.logical)} ·{" "}
                                                {storageStatus(r)}
                                            </small>
                                        </td>
                                        <td>
                                            {r.allocated == null ? (
                                                "—"
                                            ) : (
                                                <div className="share">
                                                    <Meter
                                                        value={
                                                            total
                                                                ? r.allocated /
                                                                  total
                                                                : 0
                                                        }
                                                        label={`Share of displayed measured space for ${r.name}`}
                                                    />
                                                    <span>
                                                        {percentage(
                                                            total
                                                                ? r.allocated /
                                                                      total
                                                                : 0,
                                                        )}
                                                    </span>
                                                </div>
                                            )}
                                        </td>
                                        <td className="number">
                                            {bytes(r.logical)}
                                        </td>
                                        <td className="number">
                                            {bytes(r.allocated)}
                                        </td>
                                        <td className="number saved-text">
                                            {r.logical != null &&
                                            r.allocated != null
                                                ? bytes(
                                                      Math.max(
                                                          0,
                                                          r.logical -
                                                              r.allocated,
                                                      ),
                                                  )
                                                : "—"}
                                        </td>
                                        <td className="notes">
                                            {storageStatus(r)}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                    {!rows.length && !loading && !error && (
                        <Empty
                            title={
                                search
                                    ? "No matching files"
                                    : "No scanned files yet"
                            }
                            icon="search"
                        >
                            <p>
                                {search
                                    ? "Try a different file name or clear the search."
                                    : "The folder may still be scanning. You can request a new scan from Managed folders."}
                            </p>
                            {search && (
                                <button onClick={() => setSearch("")}>
                                    Clear search
                                </button>
                            )}
                        </Empty>
                    )}
                    <div className="card-note">
                        {result && result.totalRows > rows.length
                            ? `Showing ${rows.length.toLocaleString()} of ${result.totalRows.toLocaleString()} matching items in the selected order. Narrow your search to see other items.`
                            : "Open a folder to browse. Select compressed files to restore their original size."}
                    </div>
                </>
            )}
        </section>
    );
}

function jobDetail(job: Job): string {
    if (job.message.trim()) return job.message;
    switch (job.state) {
        case "Succeeded":
            return "Completed successfully.";
        case "Queued":
            return "Waiting to start.";
        case "Running":
            return "Processing this file.";
        case "Retry":
            return "Waiting to try again.";
        case "Failed":
            return "Couldn’t process this file. Review it before trying again.";
        case "Skipped":
            return "This file was skipped.";
        case "Unchanged":
            return "No change was needed.";
        default:
            return "Status unavailable.";
    }
}

function ActivityPage({
    data,
    state,
    setState,
    act,
    reveal,
}: {
    data: Snapshot;
    state: string;
    setState: (state: string) => void;
    act: Act;
    reveal: (jobId: string) => Promise<void>;
}) {
    const [search, setSearch] = useState("");
    const jobs = data.jobs.filter(
        (j) =>
            (state === "All" ||
                (state === "Pending" ? j.pending : j.state === state)) &&
            `${j.path} ${jobDetail(j)} ${j.action} ${j.stateText}`
                .toLowerCase()
                .includes(search.toLowerCase()),
    );
    return (
        <section className="card">
            <div className="insight-toolbar activity-toolbar">
                <h2>
                    Recent activity <span className="count">{jobs.length}</span>
                </h2>
                <div className="activity-filters">
                    <select
                        aria-label="Filter activity"
                        value={state}
                        onChange={(e) => setState(e.target.value)}
                    >
                        {[
                            ["All", "Everything"],
                            ["Pending", "Waiting"],
                            ["Failed", "Failed"],
                            ["Succeeded", "Done"],
                            ["Skipped", "Skipped"],
                            ["Unchanged", "No change"],
                        ].map(([value, label]) => (
                            <option key={value} value={value}>
                                {label}
                            </option>
                        ))}
                    </select>
                    <label className="search">
                        <Search size={17} />
                        <input
                            aria-label="Search activity"
                            placeholder="Search activity…"
                            value={search}
                            onChange={(e) => setSearch(e.target.value)}
                        />
                    </label>
                </div>
            </div>
            <p className="table-scroll-hint" id="activity-scroll-hint">
                Scroll horizontally for more columns. Retry actions stay
                visible.
            </p>
            <div
                className="table-scroll"
                aria-describedby="activity-scroll-hint"
                role="region"
                aria-label="Activity table"
                tabIndex={0}
            >
                <table className="activity-table">
                    <thead>
                        <tr>
                            <th scope="col">When</th>
                            <th scope="col">Result</th>
                            <th scope="col">File</th>
                            <th scope="col" className="number">
                                Space change
                            </th>
                            <th scope="col">Details</th>
                            <th scope="col">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {jobs.slice(0, 500).map((j) => (
                            <tr key={j.id}>
                                <td className="nowrap">
                                    <time
                                        dateTime={j.utc}
                                        title={new Date(j.utc).toLocaleString()}
                                    >
                                        {when(j.utc)}
                                    </time>
                                </td>
                                <td>
                                    <span
                                        className={`pill ${j.state === "Succeeded" ? "green" : j.state === "Failed" ? "red" : ""}`}
                                    >
                                        {j.state === "Failed" ? (
                                            <AlertCircle size={13} />
                                        ) : j.state === "Succeeded" ? (
                                            <Check size={13} />
                                        ) : j.state === "Running" ? (
                                            <Activity size={13} />
                                        ) : j.pending ? (
                                            <Clock3 size={13} />
                                        ) : (
                                            <CircleMinus size={13} />
                                        )}
                                        {j.stateText}
                                    </span>
                                    <small>{j.action}</small>
                                </td>
                                <td className="activity-file" title={j.path}>
                                    <FileText size={17} />
                                    <span>{j.fileName}</span>
                                    <div className="compact-activity-meta">
                                        <time
                                            dateTime={j.utc}
                                            title={new Date(
                                                j.utc,
                                            ).toLocaleString()}
                                        >
                                            {when(j.utc)}
                                        </time>
                                        <span>
                                            {j.delta == null
                                                ? ""
                                                : j.delta < 0
                                                  ? `${bytes(-j.delta)} added`
                                                  : `${bytes(j.delta)} saved`}
                                        </span>
                                    </div>
                                    {(j.state !== "Succeeded" ||
                                        Boolean(j.message.trim())) && (
                                        <p className="compact-activity-detail">
                                            {jobDetail(j)}
                                        </p>
                                    )}
                                </td>
                                <td
                                    className={`number ${j.delta != null && j.delta < 0 ? "space-added" : "saved-text"}`}
                                >
                                    {j.delta == null
                                        ? "—"
                                        : j.delta < 0
                                          ? `${bytes(-j.delta)} added`
                                          : `${bytes(j.delta)} saved`}
                                </td>
                                <td className="notes">{jobDetail(j)}</td>
                                <td className="activity-actions">
                                    <div className="actions">
                                        <button
                                            aria-label={`Show ${j.fileName} in folder`}
                                            title="Show in File Explorer"
                                            onClick={() => void reveal(j.id)}
                                        >
                                            <Folder size={16} />
                                            <span className="reveal-label">
                                                Show
                                            </span>
                                        </button>
                                        {["Failed", "Skipped"].includes(
                                            j.state,
                                        ) && (
                                            <ActionButton
                                                onClick={() =>
                                                    void act(() =>
                                                        request("retry", {
                                                            jobId: j.id,
                                                        }),
                                                    )
                                                }
                                            >
                                                <RefreshCw size={14} />
                                                Try again
                                            </ActionButton>
                                        )}
                                    </div>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
            {!jobs.length && (
                <Empty
                    title={
                        data.jobs.length
                            ? "No matching activity"
                            : "No activity yet"
                    }
                    icon="activity"
                >
                    <p>
                        {data.jobs.length
                            ? "Try another filter or search."
                            : "Files you compress or restore will appear here."}
                    </p>
                    {data.jobs.length > 0 && (
                        <button
                            onClick={() => {
                                setState("All");
                                setSearch("");
                            }}
                        >
                            Clear filters
                        </button>
                    )}
                </Empty>
            )}
            {jobs.length > 500 && (
                <div className="card-note">
                    Showing the most recent 500 matching items.
                </div>
            )}
        </section>
    );
}

type NumberKey =
    | "minimumBytes"
    | "maximumBytes"
    | "minimumSavingBytes"
    | "reserveBytes"
    | "quietSeconds"
    | "cooldownSeconds";
const numberFields: {
    key: NumberKey;
    label: string;
    unit: number;
    min: number;
    max?: number;
    hint: string;
}[] = [
    {
        key: "minimumBytes",
        label: "Minimum file size (KB)",
        unit: 1024,
        min: 64,
        hint: "At least 64 KB. Small files rarely benefit.",
    },
    {
        key: "maximumBytes",
        label: "Maximum file size (MB)",
        unit: 1048576,
        min: 1,
        max: 256,
        hint: "Up to 256 MB per file.",
    },
    {
        key: "minimumSavingBytes",
        label: "Minimum worthwhile saving (KB)",
        unit: 1024,
        min: 0,
        hint: "Keep compression only when it saves this much.",
    },
    {
        key: "reserveBytes",
        label: "Keep drive space free (MB)",
        unit: 1048576,
        min: 256,
        hint: "At least 256 MB is reserved for Windows.",
    },
    {
        key: "quietSeconds",
        label: "Wait after a file changes (seconds)",
        unit: 1,
        min: 0,
        max: 2147483647,
        hint: "Avoid working on files still being edited.",
    },
    {
        key: "cooldownSeconds",
        label: "Recheck unchanged files after (minutes)",
        unit: 60,
        min: 0,
        max: 35791394,
        hint: "60 minutes is one hour.",
    },
];
function PolicyDialog({
    folder,
    busy,
    error,
    close,
    save,
}: {
    folder: ManagedFolder;
    busy: boolean;
    error: string;
    close: () => void;
    save: (policy: Policy) => void;
}) {
    const dialog = useRef<HTMLDialogElement>(null),
        errorRef = useRef<HTMLDivElement>(null);
    const [policy, setPolicy] = useState(folder.policy);
    const [numbers, setNumbers] = useState(
        () =>
            Object.fromEntries(
                numberFields.map((f) => [
                    f.key,
                    String(folder.policy[f.key] / f.unit),
                ]),
            ) as Record<NumberKey, string>,
    );
    const [limited, setLimited] = useState(
        folder.policy.startHour !== folder.policy.endHour,
    );
    const [touched, setTouched] = useState<Record<string, boolean>>({}),
        [submitted, setSubmitted] = useState(false),
        [advanced, setAdvanced] = useState(false);
    useEffect(() => {
        const previous = document.activeElement as HTMLElement | null;
        const element = dialog.current!;
        const root = document.documentElement;
        const overflow = root.style.overflow;
        root.style.overflow = "hidden";
        element.showModal();
        return () => {
            root.style.overflow = overflow;
            element.close();
            previous?.focus({ preventScroll: true });
        };
    }, []);
    useEffect(() => {
        if (error) errorRef.current?.focus();
    }, [error]);
    const scroll = useScrollEdges(`${advanced}:${error}:${submitted}`);
    const set = <K extends keyof Policy>(key: K, value: Policy[K]) =>
        setPolicy((p) => ({ ...p, [key]: value }));
    const errors: Record<string, string> = {};
    for (const f of numberFields) {
        const value = Number(numbers[f.key]);
        if (!numbers[f.key].trim() || !Number.isFinite(value))
            errors[f.key] = "Enter a number.";
        else if (!Number.isSafeInteger(value * f.unit))
            errors[f.key] =
                "Use a value that represents a whole number of bytes or seconds.";
        else if (value < f.min || (f.max != null && value > f.max))
            errors[f.key] =
                `Use ${f.min}${f.max != null ? ` to ${f.max}` : " or more"}.`;
    }
    if (
        !errors.minimumBytes &&
        !errors.maximumBytes &&
        Number(numbers.minimumBytes) * 1024 >
            Number(numbers.maximumBytes) * 1048576
    ) {
        const key = touched.maximumBytes ? "maximumBytes" : "minimumBytes";
        errors[key] = "Minimum file size must not exceed maximum file size.";
    }
    if (limited && policy.startHour === policy.endHour)
        errors.schedule =
            "Choose different start and end times, or select Any time.";
    const issueCount = Object.keys(errors).filter(
        (key) => submitted || touched[key],
    ).length;
    const revealInvalid = () => {
        setAdvanced(true);
        requestAnimationFrame(() => {
            const invalid = dialog.current?.querySelector<HTMLElement>(
                '[aria-invalid="true"]',
            );
            invalid?.focus({ preventScroll: true });
            (
                invalid?.closest(".number-field") ??
                invalid?.closest(".settings-group")
            )?.scrollIntoView({ block: "center" });
        });
    };
    function field(key: NumberKey) {
        const f = numberFields.find((f) => f.key === key)!;
        const invalid = Boolean(errors[key] && (submitted || touched[key]));
        return (
            <label className="number-field" key={key} htmlFor={`policy-${key}`}>
                <span id={`label-${key}`}>{f.label}</span>
                <input
                    aria-labelledby={`label-${key}`}
                    id={`policy-${key}`}
                    type="number"
                    inputMode="decimal"
                    step="any"
                    value={numbers[key]}
                    aria-invalid={invalid || undefined}
                    aria-describedby={`hint-${key}${invalid ? ` error-${key}` : ""}`}
                    onBlur={() => setTouched((t) => ({ ...t, [key]: true }))}
                    onChange={(e) =>
                        setNumbers((n) => ({ ...n, [key]: e.target.value }))
                    }
                />
                <small id={`hint-${key}`}>{f.hint}</small>
                {invalid && (
                    <span className="field-error" id={`error-${key}`}>
                        <AlertCircle size={15} />
                        <span>{errors[key]}</span>
                    </span>
                )}
            </label>
        );
    }
    return (
        <dialog
            ref={dialog}
            aria-labelledby="policy-title"
            className="policy-dialog"
            onCancel={(e) => {
                if (busy) e.preventDefault();
                else close();
            }}
        >
            <form
                noValidate
                onSubmit={(e) => {
                    e.preventDefault();
                    setSubmitted(true);
                    if (Object.keys(errors).length) {
                        revealInvalid();
                        return;
                    }
                    const numeric = Object.fromEntries(
                        numberFields.map((f) => [
                            f.key,
                            Number(numbers[f.key]) * f.unit,
                        ]),
                    );
                    save({
                        ...policy,
                        ...numeric,
                        startHour: limited ? policy.startHour : 0,
                        endHour: limited ? policy.endHour : 0,
                        exclusions: policy.exclusions
                            .split(/[;\n]/)
                            .map((p) => p.trim())
                            .filter(Boolean)
                            .join(";"),
                    });
                }}
            >
                <div className="dialog-heading">
                    <div>
                        <div className="eyebrow">FOLDER SETTINGS</div>
                        <h2 id="policy-title">{folder.name}</h2>
                        <p className="folder-path">{folder.path}</p>
                    </div>
                    <button
                        type="button"
                        disabled={busy}
                        aria-label="Close settings"
                        onClick={close}
                    >
                        <X size={19} />
                    </button>
                </div>
                <div className="dialog-body" ref={scroll.ref}>
                    {error && (
                        <div
                            role="alert"
                            tabIndex={-1}
                            ref={errorRef}
                            className="message error"
                        >
                            <AlertCircle size={18} />
                            <span>{error}</span>
                        </div>
                    )}
                    {submitted && Object.keys(errors).length > 0 && (
                        <div role="alert" className="message error">
                            Review the highlighted settings before saving.
                        </div>
                    )}
                    <fieldset disabled={busy} className="policy-fields">
                        <div className="auto-setting">
                            <label className="check-row">
                                <input
                                    type="checkbox"
                                    aria-labelledby="auto-compress-label"
                                    aria-describedby="auto-compress-hint"
                                    checked={policy.enabled}
                                    onChange={(e) =>
                                        set("enabled", e.target.checked)
                                    }
                                />
                                <span>
                                    <strong id="auto-compress-label">
                                        Automatically compress files
                                    </strong>
                                    <small id="auto-compress-hint">
                                        Compress eligible existing files, plus
                                        files you add, copy or change later.
                                    </small>
                                </span>
                            </label>
                        </div>
                        <p className="footnote">
                            Works while ZipXP is running. Turning this off
                            leaves existing files compressed.
                        </p>
                        <label className="check-row">
                            <input
                                type="checkbox"
                                checked={policy.recursive}
                                onChange={(e) =>
                                    set("recursive", e.target.checked)
                                }
                            />
                            Include subfolders
                        </label>
                        <label className="check-row">
                            <input
                                type="checkbox"
                                checked={policy.runOnBattery}
                                onChange={(e) =>
                                    set("runOnBattery", e.target.checked)
                                }
                            />
                            Allow compression and restores on battery power
                        </label>
                        {(folder.scan?.nativeDefaults ?? 0) > 0 && (
                            <div className="message warning">
                                <AlertCircle size={18} />
                                <span>
                                    Windows compression is on in{" "}
                                    {folder.scan?.nativeDefaults} folders.
                                    Windows may compress new files
                                    independently. Use More actions to turn it
                                    off.
                                </span>
                            </div>
                        )}
                        <details
                            open={advanced}
                            onToggle={(e) => setAdvanced(e.currentTarget.open)}
                        >
                            <summary>Advanced settings</summary>
                            <section className="settings-group">
                                <h3>Which files to compress</h3>
                                <div className="policy-grid">
                                    {field("minimumBytes")}
                                    {field("maximumBytes")}
                                    {field("minimumSavingBytes")}
                                </div>
                            </section>
                            <section className="settings-group">
                                <h3>Space and timing</h3>
                                <div className="policy-grid">
                                    {field("reserveBytes")}
                                    {field("quietSeconds")}
                                    {field("cooldownSeconds")}
                                </div>
                            </section>
                            <section className="settings-group">
                                <h3>Work schedule</h3>
                                <div className="schedule-options">
                                    <label>
                                        <input
                                            type="radio"
                                            name="schedule"
                                            checked={!limited}
                                            onChange={() => setLimited(false)}
                                        />
                                        Any time
                                    </label>
                                    <label>
                                        <input
                                            type="radio"
                                            name="schedule"
                                            checked={limited}
                                            onChange={() => {
                                                setLimited(true);
                                                if (
                                                    policy.startHour ===
                                                    policy.endHour
                                                )
                                                    set(
                                                        "endHour",
                                                        (policy.startHour + 8) %
                                                            24,
                                                    );
                                            }}
                                        />
                                        Only between
                                    </label>
                                </div>
                                {limited && (
                                    <div className="policy-grid schedule-hours">
                                        {(
                                            ["startHour", "endHour"] as const
                                        ).map((key, i) => (
                                            <label key={key}>
                                                {i ? "End time" : "Start time"}
                                                <select
                                                    aria-label={
                                                        i
                                                            ? "End time"
                                                            : "Start time"
                                                    }
                                                    aria-invalid={
                                                        errors.schedule &&
                                                        (submitted ||
                                                            touched.schedule)
                                                            ? true
                                                            : undefined
                                                    }
                                                    aria-describedby={
                                                        errors.schedule &&
                                                        (submitted ||
                                                            touched.schedule)
                                                            ? "schedule-hint schedule-error"
                                                            : "schedule-hint"
                                                    }
                                                    value={policy[key]}
                                                    onBlur={() =>
                                                        setTouched((t) => ({
                                                            ...t,
                                                            schedule: true,
                                                        }))
                                                    }
                                                    onChange={(e) =>
                                                        set(
                                                            key,
                                                            Number(
                                                                e.target.value,
                                                            ),
                                                        )
                                                    }
                                                >
                                                    {Array.from(
                                                        { length: 24 },
                                                        (_, h) => (
                                                            <option
                                                                key={h}
                                                                value={h}
                                                            >
                                                                {String(
                                                                    h,
                                                                ).padStart(
                                                                    2,
                                                                    "0",
                                                                )}
                                                                :00
                                                            </option>
                                                        ),
                                                    )}
                                                </select>
                                            </label>
                                        ))}
                                    </div>
                                )}
                                <p className="footnote" id="schedule-hint">
                                    Uses your PC’s local time. Overnight
                                    schedules are supported.
                                </p>
                                {errors.schedule &&
                                    (submitted || touched.schedule) && (
                                        <span
                                            className="field-error"
                                            id="schedule-error"
                                        >
                                            <AlertCircle size={15} />
                                            <span>{errors.schedule}</span>
                                        </span>
                                    )}
                            </section>
                            <section className="settings-group">
                                <h3>Files and folders to skip</h3>
                                <label
                                    className="exclusions-label"
                                    htmlFor="exclusions"
                                >
                                    Excluded patterns
                                    <textarea
                                        id="exclusions"
                                        rows={5}
                                        value={policy.exclusions.replaceAll(
                                            ";",
                                            "\n",
                                        )}
                                        onChange={(e) =>
                                            set("exclusions", e.target.value)
                                        }
                                        aria-describedby="exclusions-hint"
                                    />
                                </label>
                                <p className="footnote" id="exclusions-hint">
                                    One pattern per line. For example,{" "}
                                    <code>*.zip</code> skips ZIP files;{" "}
                                    <code>node_modules/*</code> skips that
                                    folder’s contents. Semicolons also work.
                                </p>
                            </section>
                        </details>
                    </fieldset>
                </div>
                <div className="dialog-actions">
                    <div className="dialog-footer-info">
                        {issueCount > 0 && (
                            <div role="status">
                                <button
                                    type="button"
                                    className="settings-issues"
                                    onClick={revealInvalid}
                                >
                                    <AlertCircle size={16} />
                                    <span>
                                        {issueCount}{" "}
                                        {issueCount === 1
                                            ? "setting needs"
                                            : "settings need"}{" "}
                                        attention
                                    </span>
                                </button>
                            </div>
                        )}
                        {(scroll.above || scroll.below) && (
                            <button
                                type="button"
                                className="scroll-cue"
                                disabled={busy}
                                onClick={() => {
                                    if (scroll.below)
                                        scroll.ref.current?.scrollBy({
                                            top:
                                                scroll.ref.current
                                                    .clientHeight * 0.65,
                                        });
                                    else
                                        scroll.ref.current?.scrollTo({
                                            top: 0,
                                        });
                                }}
                            >
                                <ChevronDown
                                    size={14}
                                    style={
                                        scroll.below
                                            ? undefined
                                            : { transform: "rotate(180deg)" }
                                    }
                                />
                                {scroll.below
                                    ? "More settings below"
                                    : "Back to top"}
                            </button>
                        )}
                    </div>
                    <button type="button" disabled={busy} onClick={close}>
                        Cancel
                    </button>
                    <button
                        className="primary"
                        disabled={busy}
                        type="submit"
                        data-action-label="Save folder settings"
                    >
                        {busy && <LoaderCircle size={16} className="spin" />}
                        {busy ? "Saving settings…" : "Save settings"}
                    </button>
                </div>
            </form>
        </dialog>
    );
}
