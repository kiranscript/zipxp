import { test, expect } from "@playwright/test";
import { resolve } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { openApp } from "./desktop";
const artifacts = resolve(
    "../../artifacts/ui-review",
    process.env.ZIPXP_REVIEW_ROUND || "current",
);
const nav = (page: import("@playwright/test").Page, name: string) =>
    page
        .getByRole("navigation", { name: "Main navigation" })
        .getByRole("button", { name, exact: name !== "Activity" });

test("settings keep actions visible, validate blanks, and restore keyboard focus", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1000, height: 720 });
    await openApp(page);
    await nav(page, "Managed folders").click();
    const opener = page.getByRole("button", {
        name: "Settings for Work projects",
    });
    await opener.click();
    await page.getByText("Advanced settings", { exact: true }).click();
    await expect(
        page.getByRole("button", { name: "Save settings", exact: true }),
    ).toBeInViewport();
    const minimum = page.getByRole("spinbutton", {
        name: "Minimum file size (KB)",
        exact: true,
    });
    await minimum.fill("");
    await page
        .getByRole("button", { name: "Save settings", exact: true })
        .click();
    await expect(minimum).toHaveAttribute("aria-invalid", "true");
    await expect(minimum).toBeFocused();
    await expect(
        page.getByText("Enter a number.", { exact: true }),
    ).toBeVisible();
    const explanation = page.getByText("Enter a number.", { exact: true });
    await expect(explanation).toBeInViewport();
    const body = await page.locator(".dialog-body").boundingBox();
    const message = await explanation.boundingBox();
    expect(message!.y).toBeGreaterThanOrEqual(body!.y);
    expect(message!.y + message!.height).toBeLessThanOrEqual(
        body!.y + body!.height,
    );
    await page.screenshot({
        path: `${artifacts}/settings-validation.png`,
        fullPage: false,
    });
    await page.keyboard.press("Tab");
    await expect(minimum).not.toBeFocused();
    await expect(page.locator(".field-error svg")).toBeVisible();
    await page.screenshot({
        path: `${artifacts}/settings-validation-unfocused.png`,
        fullPage: false,
    });
    await page.locator(".dialog-body").evaluate((el) => {
        el.scrollTop = el.scrollHeight;
    });
    await expect(
        page.getByText("1 setting needs attention", { exact: true }),
    ).toBeInViewport();
    await minimum.fill("64");
    await page.getByLabel("Only between", { exact: true }).check();
    await page.getByLabel("Start time", { exact: true }).selectOption("22");
    await page.getByLabel("End time", { exact: true }).selectOption("6");
    await page
        .getByRole("button", { name: "Save settings", exact: true })
        .click();
    await expect(page.getByRole("dialog")).not.toBeVisible();
    await expect(opener).toBeFocused();
    const sent = await page.evaluate(() =>
        (
            window as unknown as {
                __zipxpRequests: {
                    command: string;
                    args: { policy?: { startHour: number; endHour: number } };
                }[];
            }
        ).__zipxpRequests.filter((m) => m.command === "savePolicy"),
    );
    expect(sent).toHaveLength(1);
    expect(sent[0].args.policy).toMatchObject({ startHour: 22, endHour: 6 });
});

test("busy operations preserve focus and allow navigation and search", async ({
    page,
}) => {
    await openApp(page, { delay: { queue: 1800 } });
    const compress = page.getByRole("button", {
        name: "Compress all eligible",
        exact: true,
    });
    await compress.click();
    await expect(compress).toBeFocused();
    await expect(compress).toHaveAttribute("aria-busy", "true");
    await expect(compress.locator("svg:visible")).toHaveCount(1);
    await page.screenshot({ path: `${artifacts}/busy.png`, fullPage: false });
    await nav(page, "Activity").click();
    await page
        .getByRole("textbox", { name: "Search activity" })
        .fill("application");
    await expect(
        page.getByText("application.log", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByText("quarterly-report.csv", { exact: true }),
    ).not.toBeVisible();
    await expect(
        page.getByRole("status").filter({ hasText: "Done" }),
    ).toBeVisible();
});

test("action failures are dismissible and do not offer connection recovery", async ({
    page,
}) => {
    await openApp(page, {
        errors: {
            queue: "Not enough free space. Restore needs more room on the drive.",
        },
    });
    await page.getByRole("button", { name: "Compress all eligible" }).click();
    await expect(page.getByRole("alert")).toContainText(
        "Not enough free space",
    );
    await expect(
        page.getByRole("button", { name: "Retry connection" }),
    ).not.toBeVisible();
    await page.screenshot({
        path: `${artifacts}/action-error.png`,
        fullPage: false,
    });
    expect(
        (
            await new AxeBuilder({ page })
                .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
                .analyze()
        ).violations,
    ).toEqual([]);
    await page.getByRole("button", { name: "Dismiss error" }).click();
    await expect(page.getByRole("alert")).not.toBeVisible();
});

test("insights show loading, support selection, and browse real breadcrumbs", async ({
    page,
}) => {
    await openApp(page, { delay: { insights: 400 } });
    await nav(page, "Storage insights").click();
    await expect(page.getByRole("status")).toContainText("Loading files");
    await page.getByRole("button", { name: "Client projects" }).click();
    await expect(
        page.getByRole("navigation", { name: "Folder path" }),
    ).toContainText("Client projects");
    await page.getByRole("button", { name: "Go to parent folder" }).click();
    await page
        .getByRole("checkbox", { name: "Select all restorable files" })
        .check();
    await expect(
        page.getByRole("button", { name: "Restore selected (3)" }),
    ).toBeEnabled();
    await page
        .getByRole("button", { name: "Largest files", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Largest files", exact: true }),
    ).toHaveAttribute("aria-pressed", "true");
    await expect(
        page.getByRole("button", { name: "Client projects" }),
    ).not.toBeVisible();
});

test("insights failures replace loading with a retry control", async ({
    page,
}) => {
    await openApp(page, {
        errors: { insights: "Could not read this folder." },
    });
    await nav(page, "Storage insights").click();
    await expect(page.getByRole("alert")).toContainText(
        "Could not read this folder",
    );
    await expect(page.getByRole("status")).not.toContainText("Loading");
    await expect(page.getByRole("button", { name: "Try again" })).toBeVisible();
});

test("global pause is visible and attention links filter failed activity", async ({
    page,
}) => {
    await openApp(page);
    await page
        .getByRole("button", { name: "Pause all folders", exact: true })
        .click();
    await expect(
        page.getByText("All background work is paused", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Resume all folders", exact: true }),
    ).toHaveCount(1);
    await expect(page.getByRole("status")).toHaveCount(0);
    await page.screenshot({ path: `${artifacts}/paused.png`, fullPage: false });
    await nav(page, "Managed folders").click();
    await expect(
        page.getByText("Paused globally", { exact: true }),
    ).toBeVisible();
    await nav(page, "Overview").click();
    await page.getByRole("button", { name: "Review activity" }).click();
    await expect(
        page.getByRole("combobox", { name: "Filter activity" }),
    ).toHaveValue("Failed");
    await expect(
        page.getByText("application.log", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByText("quarterly-report.csv", { exact: true }),
    ).not.toBeVisible();
});

test("empty and disconnected states explain the next step", async ({
    page,
}) => {
    await openApp(page, { empty: true });
    await expect(
        page.getByRole("button", { name: "Choose your first folder" }),
    ).toBeVisible();
    await page.screenshot({ path: `${artifacts}/empty.png`, fullPage: false });
});

test("connection failure has retry and no misleading loading indicator", async ({
    page,
}) => {
    await openApp(page, {
        snapshotError: "The engine connection was interrupted.",
    });
    await expect(page.getByRole("alert")).toContainText(
        "The engine connection was interrupted",
    );
    await expect(
        page.getByRole("button", { name: "Retry connection" }),
    ).toBeVisible();
    await page.screenshot({
        path: `${artifacts}/connection-error.png`,
        fullPage: false,
    });
});

test("minimum desktop size keeps settings actions reachable, including high contrast", async ({
    page,
}) => {
    await page.setViewportSize({ width: 900, height: 640 });
    await openApp(page);
    await nav(page, "Managed folders").click();
    await page
        .getByRole("button", { name: "Settings for Work projects" })
        .click();
    await page.getByText("Advanced settings", { exact: true }).click();
    await expect(
        page.getByRole("button", { name: "Save settings", exact: true }),
    ).toBeInViewport();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth > innerWidth,
        ),
    ).toBe(false);
    await page.screenshot({
        path: `${artifacts}/minimum-settings.png`,
        fullPage: false,
    });
    await page.emulateMedia({ forcedColors: "active" });
    await expect(
        page.getByRole("button", { name: "Save settings", exact: true }),
    ).toBeInViewport();
    await page.screenshot({
        path: `${artifacts}/high-contrast.png`,
        fullPage: false,
    });
});

test("repeated storage navigation preserves the current results", async ({
    page,
}) => {
    await openApp(page);
    await nav(page, "Storage insights").click();
    const folder = page.getByRole("button", { name: "Client projects" });
    await expect(folder).toBeVisible();
    await page.getByRole("button", { name: "By folder", exact: true }).click();
    await expect(folder).toBeVisible();
    await page
        .getByRole("navigation", { name: "Folder path" })
        .getByRole("button", { name: "Work projects", exact: true })
        .click();
    await expect(folder).toBeVisible();
    await folder.click();
    const current = page
        .getByRole("navigation", { name: "Folder path" })
        .getByRole("button", { name: "Client projects", exact: true });
    await expect(current).toHaveAttribute("aria-current", "location");
    await current.click();
    await expect(
        page.getByText("annual-report.csv", { exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "By folder", exact: true }).click();
    await expect(
        page.getByText("annual-report.csv", { exact: true }),
    ).toBeVisible();
    await page
        .getByRole("button", { name: "Largest files", exact: true })
        .click();
    await expect(page.getByText("notes.md", { exact: true })).toBeVisible();
    await page
        .getByRole("button", { name: "Largest files", exact: true })
        .click();
    await expect(page.getByText("notes.md", { exact: true })).toBeVisible();
});

test("opening File Explorer does not lock actions or announce a file operation", async ({
    page,
}) => {
    await openApp(page, { delay: { showFile: 500 } });
    await nav(page, "Activity").click();
    await page
        .getByRole("button", {
            name: "Show application.log in folder",
            exact: true,
        })
        .click();
    await expect(page.getByRole("status")).toHaveCount(0);
    await expect(
        page.getByRole("button", { name: "Try again", exact: true }),
    ).not.toHaveAttribute("aria-disabled", "true");
    await page
        .getByRole("textbox", { name: "Search activity" })
        .fill("application");
    await expect(
        page.getByText("application.log", { exact: true }),
    ).toBeVisible();
});

test("many folders keep the selected folder actions nearby", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1000, height: 720 });
    await openApp(page, { folderCount: 20 });
    await nav(page, "Managed folders").click();
    await page
        .locator(".folder-main")
        .filter({ hasText: "Archive 20" })
        .click();
    await expect(
        page.getByRole("heading", { name: "Archive 20", exact: true }),
    ).toBeInViewport();
    await expect(
        page.getByRole("button", { name: "Compress now", exact: true }),
    ).toBeInViewport();
    const dimensions = await page.evaluate(() => ({
        doc: document.documentElement.scrollHeight,
        body: document.body.scrollHeight,
        viewport: innerHeight,
    }));
    expect(dimensions.doc).toBeLessThanOrEqual(dimensions.body + 1);
    expect(dimensions.doc).toBeLessThan(dimensions.viewport + 150);
    await page.screenshot({
        path: `${artifacts}/many-folders.png`,
        fullPage: false,
    });
});

test("compact activity keeps retry visible and usable without horizontal scrolling", async ({
    page,
}) => {
    for (const width of [1000, 900]) {
        await page.setViewportSize({ width, height: 720 });
        await openApp(page);
        await nav(page, "Activity").click();
        const retry = page.getByRole("button", {
            name: "Try again",
            exact: true,
        });
        await expect(retry).toBeInViewport({ ratio: 0.99 });
        await retry.click();
        await expect(
            page.getByRole("status").filter({ hasText: "Done" }),
        ).toBeVisible();
        const requested = await page.evaluate(() =>
            (
                window as unknown as { __zipxpRequests: { command: string }[] }
            ).__zipxpRequests.some((message) => message.command === "retry"),
        );
        expect(requested).toBe(true);
    }
});

test("Quit remains available when the engine connection fails", async ({
    page,
}) => {
    await openApp(page, { snapshotError: "The engine stopped responding." });
    await expect(page.getByRole("alert")).toBeVisible();
    const quit = page.getByRole("button", { name: "Quit ZipXP", exact: true });
    await expect(quit).toBeEnabled();
    await quit.click();
    await expect(page.getByRole("status")).toContainText("Quitting ZipXP");
    const requested = await page.evaluate(() =>
        (
            window as unknown as { __zipxpRequests: { command: string }[] }
        ).__zipxpRequests.some((message) => message.command === "quit"),
    );
    expect(requested).toBe(true);
    await expect(
        page.getByText("The engine isn’t responding", { exact: true }),
    ).not.toBeVisible();
    await expect(
        page.getByText("Retry the connection to load your folders.", {
            exact: true,
        }),
    ).not.toBeVisible();
    await expect(
        page.getByRole("button", { name: "Retry connection", exact: true }),
    ).not.toBeVisible();
    await page.screenshot({
        path: `${artifacts}/quit-disconnected.png`,
        fullPage: false,
    });
});

test("Quit and startup remain independent of a pending engine action", async ({
    page,
}) => {
    await openApp(page, { delay: { queue: 1800 } });
    await page
        .getByRole("button", { name: "Compress all eligible", exact: true })
        .click();
    const startup = page.getByRole("checkbox", {
        name: "Start when I sign in",
        exact: true,
    });
    await expect(startup).toBeEnabled();
    await startup.check();
    await expect(startup).toBeChecked();
    await page.getByRole("button", { name: "Quit ZipXP", exact: true }).click();
    await expect(page.getByRole("status")).toContainText("Quitting ZipXP");
    const commands = await page.evaluate(() =>
        (
            window as unknown as { __zipxpRequests: { command: string }[] }
        ).__zipxpRequests.map((message) => message.command),
    );
    expect(commands).toContain("queue");
    expect(commands).toContain("startup");
    expect(commands).toContain("quit");
    await expect(page.locator("svg.spin:visible")).toHaveCount(1);
    await page.screenshot({
        path: `${artifacts}/quit-busy.png`,
        fullPage: false,
    });
});

test("menu actions announce concise labels and return keyboard focus to their trigger", async ({
    page,
}) => {
    await openApp(page, { delay: { queue: 650, remove: 650 } });
    await nav(page, "Managed folders").click();
    const trigger = page.locator(".more-actions > summary");
    for (const label of [
        "Restore original size",
        "Stop managing this folder",
        "Turn off Windows compression",
    ]) {
        if (label === "Turn off Windows compression")
            await page
                .locator(".folder-main")
                .filter({ hasText: "Documents" })
                .click();
        await trigger.click();
        await page.getByRole("button", { name: label, exact: false }).focus();
        await page.keyboard.press("Enter");
        await expect(trigger).toBeFocused();
        await expect(page.locator(".more-actions")).not.toHaveAttribute("open");
        await expect(page.locator(".progress-strip strong")).toHaveText(
            `${label}…`,
        );
        await expect(
            page.getByRole("status").filter({ hasText: "Done" }),
        ).toBeVisible();
    }
});

test("compact tables keep failure details, file metadata and recovery visible together", async ({
    page,
}) => {
    await page.setViewportSize({ width: 900, height: 720 });
    await openApp(page);
    await nav(page, "Activity").click();
    await expect(
        page.locator(".compact-activity-detail").first(),
    ).toBeInViewport();
    await expect(
        page.getByRole("button", { name: "Try again", exact: true }),
    ).toBeInViewport({ ratio: 0.99 });
    const activity = page.getByRole("region", { name: "Activity table" });
    expect(
        await activity.evaluate((el) => el.scrollWidth <= el.clientWidth + 1),
    ).toBe(true);
    await nav(page, "Storage insights").click();
    await expect(
        page.getByRole("button", { name: "Client projects" }),
    ).toBeVisible();
    await expect(
        page.locator(".compact-insight-meta").first(),
    ).toBeInViewport();
    const storage = page.getByRole("region", { name: "Storage items table" });
    expect(
        await storage.evaluate((el) => el.scrollWidth <= el.clientWidth + 1),
    ).toBe(true);
});

test("advanced settings expose a working scroll cue and persistent save controls", async ({
    page,
}) => {
    await page.setViewportSize({ width: 900, height: 640 });
    await openApp(page);
    await nav(page, "Managed folders").click();
    await page
        .getByRole("button", { name: "Settings for Work projects" })
        .click();
    await page.getByText("Advanced settings", { exact: true }).click();
    const cue = page.getByRole("button", {
        name: "More settings below",
        exact: true,
    });
    await expect(cue).toBeInViewport();
    await cue.click();
    expect(
        await page.locator(".dialog-body").evaluate((el) => el.scrollTop),
    ).toBeGreaterThan(0);
    await expect(
        page.getByRole("button", { name: "Save settings", exact: true }),
    ).toBeInViewport();
});

test("the requested monochrome theme uses neutral text, surfaces, borders and focus colors", async ({
    page,
}) => {
    await openApp(page);
    await expect(
        page.getByRole("button", { name: "Compress all eligible" }),
    ).toBeVisible();
    const offPalette = await page.evaluate(() => {
        const found = new Set<string>();
        for (const element of document.querySelectorAll("body *")) {
            if (!element.getBoundingClientRect().width) continue;
            const style = getComputedStyle(element);
            for (const name of [
                "color",
                "background-color",
                "border-top-color",
                "outline-color",
            ]) {
                const color = style.getPropertyValue(name);
                const channels = color
                    .match(/\d+(\.\d+)?/g)
                    ?.slice(0, 3)
                    .map(Number);
                if (
                    channels?.length === 3 &&
                    Math.max(...channels) - Math.min(...channels) > 1
                )
                    found.add(color);
            }
        }
        return [...found];
    });
    expect(offPalette).toEqual([]);
});

test("waiting, running and retry jobs describe their actual state instead of claiming success", async ({
    page,
}) => {
    await openApp(page, { pendingJobs: true });
    await nav(page, "Activity").click();
    await expect(
        page.getByRole("row").filter({ hasText: "queued.txt" }),
    ).toContainText("Waiting to start.");
    await expect(
        page.getByRole("row").filter({ hasText: "running.txt" }),
    ).toContainText("Processing this file.");
    await expect(
        page.getByRole("row").filter({ hasText: "retry.txt" }),
    ).toContainText("Waiting to try again.");
    await expect(
        page.getByText("Completed successfully.", { exact: true }),
    ).toHaveCount(0);
    await page.screenshot({
        path: `${artifacts}/pending-activity.png`,
        fullPage: false,
    });
    await page.setViewportSize({ width: 900, height: 720 });
    await expect(
        page
            .locator(".compact-activity-detail")
            .filter({ hasText: "Waiting to start." }),
    ).toBeInViewport();
    await expect(
        page
            .locator(".compact-activity-detail")
            .filter({ hasText: "Processing this file." }),
    ).toBeInViewport();
    await expect(
        page
            .locator(".compact-activity-detail")
            .filter({ hasText: "Waiting to try again." }),
    ).toBeInViewport();
    await page.screenshot({
        path: `${artifacts}/compact-pending-activity.png`,
        fullPage: false,
    });
});

test("monochrome error, attention and operational feedback retain separate hierarchies", async ({
    page,
}) => {
    await openApp(page, {
        delay: { queue: 700 },
        errors: { queue: "The folder could not be processed." },
    });
    await page
        .getByRole("button", { name: "Compress all eligible", exact: true })
        .click();
    await expect(page.locator(".progress-strip")).toBeVisible();
    await expect(page.locator(".attention-card")).toHaveCount(0);
    await expect(
        page.getByRole("button", {
            name: "1 file needs attention, Review activity",
            exact: true,
        }),
    ).toBeVisible();
    await expect(page.getByRole("alert")).toContainText(
        "The folder could not be processed.",
    );
    await expect(page.locator(".message.error")).toHaveCSS(
        "border-left-width",
        "4px",
    );
    await expect(page.locator(".attention-card")).toHaveCount(0);
    await page.screenshot({
        path: `${artifacts}/error-hierarchy.png`,
        fullPage: false,
    });
});

test("failure counts remain in accessible names when another banner is showing", async ({
    page,
}) => {
    await openApp(page);
    await expect(
        page
            .getByRole("navigation", { name: "Main navigation" })
            .getByRole("button", {
                name: "Activity 1 failed file",
                exact: true,
            }),
    ).toBeVisible();
    await page
        .getByRole("button", { name: "Pause all folders", exact: true })
        .click();
    const attention = page.getByRole("button", {
        name: "1 file needs attention, Review activity",
        exact: true,
    });
    await expect(attention).toBeVisible();
    await expect(attention).toContainText("1 file needs attention");
    const report = await new AxeBuilder({ page })
        .withRules(["label-content-name-mismatch"])
        .analyze();
    expect(report.violations).toEqual([]);
    expect(
        report.passes.some((rule) => rule.id === "label-content-name-mismatch"),
    ).toBe(true);
    await attention.click();
    await expect(
        page.getByRole("combobox", { name: "Filter activity" }),
    ).toHaveValue("Failed");
    await expect(
        page.getByText("application.log", { exact: true }),
    ).toBeVisible();
});

test("settings contain scrolling and expose live errors through the footer", async ({
    page,
}) => {
    await page.setViewportSize({ width: 900, height: 640 });
    await openApp(page);
    await nav(page, "Managed folders").click();
    await page
        .getByRole("button", { name: "Settings for Work projects" })
        .click();
    await page.getByText("Advanced settings", { exact: true }).click();
    const originalY = await page.evaluate(() => scrollY);
    await page.mouse.move(890, 600);
    await page.mouse.wheel(0, 600);
    await expect.poll(() => page.evaluate(() => scrollY)).toBe(originalY);
    await expect(page.locator("html")).toHaveCSS("overflow", "hidden");
    const maximum = page.getByRole("spinbutton", {
        name: "Maximum file size (MB)",
        exact: true,
    });
    await page
        .getByRole("spinbutton", {
            name: "Minimum file size (KB)",
            exact: true,
        })
        .fill("2048");
    await maximum.fill("1");
    await maximum.press("Tab");
    await expect(maximum).toHaveAttribute("aria-invalid", "true");
    await expect(
        page.getByText("Minimum file size must not exceed maximum file size.", {
            exact: true,
        }),
    ).toBeVisible();
    await page.locator(".dialog-body").evaluate((el) => {
        el.scrollTop = el.scrollHeight;
    });
    await page
        .getByRole("button", { name: "1 setting needs attention", exact: true })
        .click();
    await expect(maximum).toBeFocused();
    await expect(
        page.getByText("Minimum file size must not exceed maximum file size.", {
            exact: true,
        }),
    ).toBeInViewport();
    await maximum.fill("256");
    await maximum.press("Tab");
    await page.getByLabel("Only between", { exact: true }).check();
    await page.getByLabel("Start time", { exact: true }).selectOption("8");
    const end = page.getByLabel("End time", { exact: true });
    await end.selectOption("8");
    await end.press("Tab");
    await expect(end).toHaveAttribute("aria-invalid", "true");
    await expect(end).toHaveAttribute(
        "aria-describedby",
        "schedule-hint schedule-error",
    );
    await page
        .getByRole("button", { name: "1 setting needs attention", exact: true })
        .click();
    await expect(page.getByLabel("Start time", { exact: true })).toBeFocused();
    await expect(page.locator("#schedule-error")).toBeInViewport();
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.locator("html")).not.toHaveCSS("overflow", "hidden");
});

test("sorting refetches the complete matching set before the 500-item bound", async ({
    page,
}) => {
    await openApp(page, { manyInsights: true });
    await nav(page, "Storage insights").click();
    await expect(
        page.getByText("502 matching items", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByText("aaa-compressed.txt", { exact: true }),
    ).toHaveCount(0);
    await page
        .getByRole("combobox", { name: "Sort storage items" })
        .selectOption("logical");
    await expect(
        page.getByText("aaa-compressed.txt", { exact: true }),
    ).toBeVisible();
    await expect(
        page.locator(".insights-table tbody tr").first(),
    ).toContainText("aaa-compressed.txt");
    await page
        .getByRole("combobox", { name: "Sort storage items" })
        .selectOption("name");
    await expect(
        page.getByText("Updating results…", { exact: true }),
    ).toHaveCount(0);
    await expect(
        page.locator(".insights-table tbody tr").first(),
    ).toContainText("aaa-compressed.txt");
    const tiny = page.locator(".insights-table tbody tr").first();
    await expect(tiny.getByText("<1%", { exact: true })).toBeVisible();
    await expect(tiny.getByRole("meter")).toHaveAttribute(
        "aria-valuetext",
        "Less than 1%",
    );
    await page.screenshot({ path: `${artifacts}/large-insights.png` });
});

test("long folder names remain contained with accessible full names", async ({
    page,
}) => {
    for (const width of [900, 1000]) {
        await page.setViewportSize({ width, height: 720 });
        await openApp(page, { longNames: true });
        await nav(page, "Managed folders").click();
        const name = page.locator(".folder-main strong").first();
        await expect(name).toHaveAttribute(
            "title",
            (await name.textContent()) ?? "",
        );
        const label = await name.boundingBox();
        const size = await page.locator(".folder-size").first().boundingBox();
        expect(label!.x + label!.width).toBeLessThanOrEqual(size!.x);
        const overflow = await page.evaluate(() => ({
            width: document.documentElement.scrollWidth,
            offenders: [...document.querySelectorAll("body *")]
                .filter(
                    (el) => el.getBoundingClientRect().right > innerWidth + 1,
                )
                .map((el) => `${el.tagName}.${el.className}`)
                .slice(0, 10),
        }));
        expect(
            overflow.width,
            JSON.stringify(overflow.offenders),
        ).toBeLessThanOrEqual(width);
        await page.screenshot({
            path: `${artifacts}/long-folders-${width}.png`,
        });
    }
});

test("modal backdrop covers the scrollbar gutter and forced-color primaries use paired system colors", async ({
    page,
}) => {
    await openApp(page);
    await nav(page, "Managed folders").click();
    const headerAction = page.getByRole("button", {
        name: "Add folder",
        exact: true,
    });
    const before = await headerAction.boundingBox();
    const contentWidth = await page.evaluate(
        () => document.documentElement.clientWidth,
    );
    await page
        .getByRole("button", { name: "Settings for Work projects" })
        .click();
    const modalHeight = await page
        .getByRole("dialog")
        .evaluate((el) => el.getBoundingClientRect().height);
    expect(modalHeight).toBeLessThan(900 * 0.75);
    await expect(page.locator("html")).toHaveCSS("scrollbar-gutter", "stable");
    expect(
        await page.evaluate(() => document.documentElement.clientWidth),
    ).toBe(contentWidth);
    expect(await headerAction.boundingBox()).toEqual(before);
    // This five-pixel crop records the previously uncovered scrollbar gutter.
    await page.screenshot({
        path: `${artifacts}/modal-right-edge.png`,
        clip: { x: 1275, y: 0, width: 5, height: 900 },
    });
    const edge = await page.screenshot({
        clip: { x: 1275, y: 400, width: 5, height: 5 },
    });
    const pixel = await page.evaluate(async (data) => {
        const image = new Image();
        image.src = `data:image/png;base64,${data}`;
        await image.decode();
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = 5;
        const context = canvas.getContext("2d")!;
        context.drawImage(image, 0, 0);
        return Array.from(context.getImageData(2, 2, 1, 1).data).slice(0, 3);
    }, edge.toString("base64"));
    expect(pixel.every((channel) => channel < 220)).toBe(true);
    await page.emulateMedia({ forcedColors: "active" });
    const primary = page.getByRole("button", {
        name: "Save settings",
        exact: true,
    });
    await expect(primary).toHaveCSS("forced-color-adjust", "none");
    expect(
        await primary.evaluate((el) => {
            const system = document.createElement("span");
            system.style.color = "ButtonFace";
            system.style.backgroundColor = "ButtonText";
            document.body.append(system);
            const actual = getComputedStyle(el),
                expected = getComputedStyle(system);
            const paired =
                actual.color === expected.color &&
                actual.backgroundColor === expected.backgroundColor;
            system.remove();
            return paired;
        }),
    ).toBe(true);
    await page.screenshot({ path: `${artifacts}/high-contrast-settings.png` });
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    for (const name of [
        "Overview",
        "Managed folders",
        "Storage insights",
        "Activity",
    ]) {
        await nav(page, name).click();
        await expect(
            page.getByRole("heading", { name, exact: true }),
        ).toBeVisible();
        if (name === "Storage insights")
            await expect(
                page.getByRole("button", { name: "Client projects" }),
            ).toBeVisible();
        await page.screenshot({
            path: `${artifacts}/high-contrast-${name.toLowerCase().replaceAll(" ", "-")}.png`,
        });
    }
});

test("forced-colors view selection remains visible after focus moves away", async ({
    page,
}) => {
    await page.emulateMedia({ forcedColors: "active" });
    await openApp(page);
    await nav(page, "Storage insights").click();
    for (const [name, file] of [
        ["By folder", "by-folder"],
        ["Largest files", "largest-files"],
    ]) {
        const selected = page.getByRole("button", { name, exact: true });
        await selected.click();
        await page.getByRole("textbox", { name: "Search files" }).focus();
        await expect(selected).not.toBeFocused();
        await expect(selected).toHaveAttribute("aria-pressed", "true");
        await expect(selected).toHaveCSS("border-width", "2px");
        const unselected = page.locator(
            '.segmented button[aria-pressed="false"]',
        );
        expect(
            await selected.evaluate((el) => getComputedStyle(el).borderColor),
        ).not.toBe(
            await unselected.evaluate((el) => getComputedStyle(el).borderColor),
        );
        expect(
            await selected.evaluate(
                (el) => getComputedStyle(el).backgroundColor,
            ),
        ).toBe(
            await unselected.evaluate(
                (el) => getComputedStyle(el).backgroundColor,
            ),
        );
        expect(
            await selected.evaluate((el) => getComputedStyle(el).color),
        ).toBe(await unselected.evaluate((el) => getComputedStyle(el).color));
        await expect(
            page.getByText("Loading files…", { exact: true }),
        ).toHaveCount(0);
        await page.screenshot({
            path: `${artifacts}/high-contrast-${file}.png`,
        });
        await page.keyboard.press("Shift+Tab");
        if (name === "By folder") await page.keyboard.press("Shift+Tab");
        await expect(selected).toBeFocused();
        await expect(selected).toHaveCSS("outline-width", "3px");
        await expect(selected).toHaveCSS("outline-offset", "3px");
    }
});

test("storage status preserves compression state alongside the eligibility reason", async ({
    page,
}) => {
    await openApp(page, { uncompressedRow: true });
    await nav(page, "Storage insights").click();
    const compressed = page
        .locator(".insights-table tbody tr")
        .filter({ hasText: "application.log" });
    await expect(compressed.locator("td.notes")).toHaveText(
        "Compressed · Below the size limit for new compression",
    );
    await expect(compressed.getByRole("checkbox")).toBeEnabled();
    const uncompressed = page
        .locator(".insights-table tbody tr")
        .filter({ hasText: "notes.md" });
    await expect(uncompressed.locator("td.notes")).toHaveText(
        "Not compressed · Can be compressed",
    );
    await expect(uncompressed.getByRole("checkbox")).toHaveCount(0);
    await page.screenshot({
        path: `${artifacts}/compression-and-eligibility.png`,
    });
});

test("search focus surrounds its complete control and high contrast keeps logo and disabled controls legible", async ({
    page,
}) => {
    await openApp(page);
    for (const contrast of [false, true]) {
        await page.emulateMedia({ forcedColors: contrast ? "active" : "none" });
        for (const [screen, name] of [
            ["Storage insights", "Search files"],
            ["Activity", "Search activity"],
        ]) {
            await nav(page, screen).click();
            if (screen === "Storage insights")
                await expect(
                    page.getByRole("button", { name: "Client projects" }),
                ).toBeVisible();
            const search = page.getByRole("textbox", { name });
            await search.focus();
            const container = search.locator("..");
            await expect(container).toHaveCSS("outline-width", "3px");
            await expect(container).toHaveCSS("outline-offset", "3px");
            await expect(search).toHaveCSS("outline-style", "none");
            await page.screenshot({
                path: `${artifacts}/search-focus-${contrast ? "high-contrast" : "normal"}-${screen === "Activity" ? "activity" : "insights"}.png`,
            });
        }
        if (contrast) {
            await expect(page.locator(".brand-icon")).toHaveCSS(
                "forced-color-adjust",
                "none",
            );
            const colors = await page.locator(".brand-icon").evaluate((el) => ({
                foreground: getComputedStyle(el).color,
                background: getComputedStyle(el).backgroundColor,
                glyph: getComputedStyle(el.querySelector("svg")!).stroke,
            }));
            expect(colors.foreground).not.toBe(colors.background);
            expect(colors.glyph).toBe(colors.foreground);
            await nav(page, "Storage insights").click();
            const disabled = page.getByRole("button", {
                name: "Restore selected",
                exact: true,
            });
            await expect(disabled).toBeDisabled();
            await expect(disabled).toHaveCSS("opacity", "1");
            expect(
                await disabled.evaluate((el) => {
                    const probe = document.createElement("span");
                    probe.style.color = "GrayText";
                    document.body.append(probe);
                    const gray = getComputedStyle(probe).color,
                        style = getComputedStyle(el);
                    const correct =
                        style.color === gray && style.borderColor === gray;
                    probe.remove();
                    return correct;
                }),
            ).toBe(true);
        }
    }
});
