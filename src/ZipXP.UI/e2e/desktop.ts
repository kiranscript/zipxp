import type { Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve, sep } from "node:path";
import { snapshot, insights } from "./fixtures";
export interface Scenario {
    folderCount?: number;
    longNames?: boolean;
    uncompressedRow?: boolean;
    manyInsights?: boolean;
    pendingJobs?: boolean;
    empty?: boolean;
    snapshotError?: string;
    errors?: Record<string, string>;
    delay?: Record<string, number>;
    browserOnly?: boolean;
}
export async function openApp(page: Page, scenario: Scenario = {}) {
    await page.route("https://zipxp.internal/**", async (route) => {
        const path = new URL(route.request().url()).pathname;
        const file = resolve("dist", "." + path);
        if (!file.startsWith(resolve("dist") + sep)) return route.abort();
        await route.fulfill({
            body: await readFile(file),
            contentType: path.endsWith(".html")
                ? "text/html"
                : path.endsWith(".js")
                  ? "application/javascript"
                  : "text/css",
        });
    });
    await page.addInitScript(
        ({ snapshot, insights, scenario }) => {
            if (scenario.browserOnly) return;
            if (scenario.empty) {
                snapshot.folders = [];
                snapshot.jobs = [];
                snapshot.volumes = [];
                snapshot.original = 0;
                snapshot.allocated = 0;
            }
            if (scenario.folderCount) {
                const first = snapshot.folders[0];
                snapshot.folders = Array.from(
                    { length: scenario.folderCount },
                    (_, i) =>
                        i < 3
                            ? snapshot.folders[i]
                            : {
                                  ...first,
                                  id: String(i),
                                  name: `Archive ${i + 1}`,
                                  path: `C:\\Users\\Kiran\\Archive ${i + 1}`,
                              },
                );
            }
            if (scenario.uncompressedRow) {
                insights.rows[4].compressed = false;
                insights.rows[4].notes = "Can be compressed";
                insights.rows[4].logical = 128 * 1024;
                insights.rows[4].allocated = 128 * 1024;
            }
            if (scenario.longNames) {
                snapshot.folders[0].name =
                    "Quarterly planning and customer delivery archives "
                        .repeat(5)
                        .trim();
                snapshot.folders[0].path =
                    "C:\\Projects\\" + snapshot.folders[0].name;
            }
            if (scenario.manyInsights) {
                const template = insights.rows.find((r) => !r.directory)!;
                insights.rows = Array.from({ length: 501 }, (_, i) => ({
                    ...template,
                    name: `file-${String(i).padStart(3, "0")}.txt`,
                    path: insights.path + `\\file-${i}.txt`,
                    logical: 10000 + i,
                    allocated: 10000 + i,
                }));
                insights.rows.push({
                    ...template,
                    name: "aaa-compressed.txt",
                    path: insights.path + "\\aaa-compressed.txt",
                    logical: 1000000,
                    allocated: 1,
                });
            }
            if (scenario.pendingJobs) {
                snapshot.jobs = [
                    ["Queued", "Waiting", "queued.txt"],
                    ["Running", "Working", "running.txt"],
                    ["Retry", "Will retry", "retry.txt"],
                ].map(([state, stateText, fileName], i) => ({
                    ...snapshot.jobs[0],
                    id: `pending-${i}`,
                    fileName,
                    path: `C:\\Work\\${fileName}`,
                    state,
                    stateText,
                    message: "",
                    delta: null,
                    pending: true,
                }));
                snapshot.currentFile = snapshot.jobs[1].path;
                snapshot.status = "Working";
            }
            const listeners = new Set<(e: { data: unknown }) => void>();
            const commands: unknown[] = [];
            Object.assign(window, {
                __zipxpRequests: commands,
                chrome: {
                    webview: {
                        addEventListener: (
                            _: string,
                            fn: (e: { data: unknown }) => void,
                        ) => listeners.add(fn),
                        removeEventListener: (
                            _: string,
                            fn: (e: { data: unknown }) => void,
                        ) => listeners.delete(fn),
                        postMessage: (m: {
                            id: string;
                            command: string;
                            args: Record<string, unknown>;
                        }) => {
                            commands.push(m);
                            const error =
                                m.command === "snapshot"
                                    ? scenario.snapshotError
                                    : scenario.errors?.[m.command];
                            setTimeout(() => {
                                let result: unknown = "Done";
                                if (!error) {
                                    if (m.command === "snapshot")
                                        result = snapshot;
                                    if (m.command === "insights") {
                                        const path = String(
                                            m.args.path || insights.path,
                                        );
                                        const source =
                                            path === insights.path
                                                ? insights.rows
                                                : insights.rows
                                                      .filter(
                                                          (r) => !r.directory,
                                                      )
                                                      .slice(0, 1)
                                                      .map((r) => ({
                                                          ...r,
                                                          path:
                                                              path +
                                                              "\\" +
                                                              r.name,
                                                      }));
                                        const rows = source.filter(
                                            (r) =>
                                                (!m.args.largest ||
                                                    !r.directory) &&
                                                r.name
                                                    .toLowerCase()
                                                    .includes(
                                                        String(
                                                            m.args.search || "",
                                                        ).toLowerCase(),
                                                    ),
                                        );
                                        result = {
                                            path,
                                            parent:
                                                path === insights.path
                                                    ? null
                                                    : insights.path,
                                            rows: [...rows]
                                                .sort((a, b) =>
                                                    m.args.sort === "name"
                                                        ? a.name.localeCompare(
                                                              b.name,
                                                          )
                                                        : (b[
                                                              m.args.sort ===
                                                              "logical"
                                                                  ? "logical"
                                                                  : "allocated"
                                                          ] ?? -1) -
                                                          (a[
                                                              m.args.sort ===
                                                              "logical"
                                                                  ? "logical"
                                                                  : "allocated"
                                                          ] ?? -1),
                                                )
                                                .slice(0, 500),
                                            totalRows: rows.length,
                                        };
                                    }
                                    if (m.command === "pause")
                                        snapshot.paused = Boolean(
                                            m.args.paused,
                                        );
                                    if (m.command === "startup")
                                        snapshot.startup = Boolean(
                                            m.args.enabled,
                                        );
                                    if (m.command === "savePolicy")
                                        snapshot.folders.find(
                                            (f) => f.id === m.args.rootId,
                                        )!.policy = m.args
                                            .policy as (typeof snapshot.folders)[0]["policy"];
                                }
                                listeners.forEach((fn) =>
                                    fn({ data: { id: m.id, result, error } }),
                                );
                            }, scenario.delay?.[m.command] ?? 5);
                        },
                    },
                },
            });
        },
        { snapshot, insights, scenario },
    );
    await page.goto("https://zipxp.internal/index.html");
}
