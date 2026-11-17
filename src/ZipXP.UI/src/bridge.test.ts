import { describe, expect, it, vi } from "vitest";
import { createBridge } from "./bridge";
import type { WebView } from "./bridge";

function host() {
    const listeners = new Set<(e: MessageEvent) => void>();
    const webview: WebView = {
        postMessage: vi.fn(),
        addEventListener: (_, fn) => {
            listeners.add(fn);
        },
        removeEventListener: (_, fn) => {
            listeners.delete(fn);
        },
    };
    return {
        webview,
        listeners,
        reply: (data: unknown) => {
            for (const fn of [...listeners]) fn({ data } as MessageEvent);
        },
    };
}
describe("desktop bridge", () => {
    it("rejects browser writes when no desktop host is available", async () => {
        await expect(
            createBridge()("queue", { operation: "Compress" }),
        ).rejects.toThrow("browser preview cannot access your files");
    });
    it("correlates concurrent responses and removes listeners", async () => {
        const h = host(),
            request = createBridge(h.webview);
        const a = request("scan", { rootId: "a" }),
            b = request("pause", { paused: true });
        h.reply({ id: "2", result: "Paused" });
        h.reply({ id: "unrelated", result: "Wrong result" });
        h.reply({ id: "1", result: "Scanning" });
        expect(await a).toBe("Scanning");
        expect(await b).toBe("Paused");
        expect(h.listeners.size).toBe(0);
        expect(h.webview.postMessage).toHaveBeenCalledWith({
            id: "1",
            command: "scan",
            args: { rootId: "a" },
        });
    });
    it("propagates engine errors without reporting success", async () => {
        const h = host(),
            pending = createBridge(h.webview)("queue", {
                rootId: "a",
                operation: "Decompress",
            });
        h.reply({ id: "1", error: "Not enough free space" });
        await expect(pending).rejects.toThrow("Not enough free space");
        expect(h.listeners.size).toBe(0);
    });
    it("cleans up after a timeout", async () => {
        vi.useFakeTimers();
        try {
            const h = host(),
                pending = createBridge(h.webview, 50)("snapshot", {});
            const rejected = expect(pending).rejects.toThrow(
                "Refresh to check the latest state",
            );
            await vi.advanceTimersByTimeAsync(51);
            await rejected;
            expect(h.listeners.size).toBe(0);
        } finally {
            vi.useRealTimers();
        }
    });
});
