import { defineConfig } from "@playwright/test";
export default defineConfig({
    testDir: "./e2e",
    workers: 1,
    reporter: "list",
    use: {
        reducedMotion: "reduce",
        viewport: { width: 1280, height: 900 },
        launchOptions: process.env.ZIPXP_CHROMIUM
            ? { executablePath: process.env.ZIPXP_CHROMIUM }
            : {},
    },
});
