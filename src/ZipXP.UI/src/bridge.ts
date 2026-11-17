import type { Commands } from "./types";

export interface WebView {
    postMessage(message: unknown): void;
    addEventListener(
        type: "message",
        listener: (event: MessageEvent) => void,
    ): void;
    removeEventListener(
        type: "message",
        listener: (event: MessageEvent) => void,
    ): void;
}
declare global {
    interface Window {
        chrome?: { webview?: WebView };
    }
}

// One listener per request also makes cleanup explicit on errors and timeouts.
export function createBridge(host?: WebView, timeout = 180_000) {
    let sequence = 0;
    return function request<K extends keyof Commands>(
        command: K,
        args: Commands[K]["args"],
    ): Promise<Commands[K]["result"]> {
        if (!host)
            return Promise.reject(
                new Error(
                    "Open ZipXP on Windows to connect to your folders. The browser preview cannot access your files.",
                ),
            );
        const id = String(++sequence);
        return new Promise((resolve, reject) => {
            const cleanup = () => {
                clearTimeout(timer);
                host.removeEventListener("message", listener);
            };
            const listener = (event: MessageEvent) => {
                const message = event.data;
                if (!message || message.id !== id) return;
                cleanup();
                if (message.error) reject(new Error(message.error));
                else resolve(message.result);
            };
            const timer = setTimeout(() => {
                cleanup();
                reject(
                    new Error(
                        "ZipXP took too long to respond. Refresh to check the latest state before trying again.",
                    ),
                );
            }, timeout);
            host.addEventListener("message", listener);
            try {
                host.postMessage({ id, command, args });
            } catch (error) {
                cleanup();
                reject(error);
            }
        });
    };
}
const desktopHost =
    typeof window === "undefined" ? undefined : window.chrome?.webview;
export const request = createBridge(desktopHost);
export const connected = Boolean(desktopHost);
