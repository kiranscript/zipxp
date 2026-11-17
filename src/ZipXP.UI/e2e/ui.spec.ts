import { test, expect } from "@playwright/test";
import { writeFile } from "node:fs/promises";
import AxeBuilder from "@axe-core/playwright";
import { resolve } from "node:path";
import { openApp } from "./desktop";
const artifacts = resolve(
    "../../artifacts/ui-review",
    process.env.ZIPXP_REVIEW_ROUND || "current",
);
test("review all screens with realistic desktop data", async ({ page }) => {
    await openApp(page);
    await expect(
        page.getByRole("button", {
            name: "Compress all eligible",
            exact: true,
        }),
    ).toBeVisible();
    await page.screenshot({
        path: `${artifacts}/overview.png`,
        fullPage: true,
    });
    const audit = async (name: string) => {
        const report = await new AxeBuilder({ page })
            .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
            .analyze();
        await writeFile(
            `${artifacts}/${name}-accessibility.json`,
            JSON.stringify(report.violations, null, 2),
        );
        expect(report.violations).toEqual([]);
        const labels = await new AxeBuilder({ page })
            .withRules(["label-content-name-mismatch"])
            .analyze();
        await writeFile(
            `${artifacts}/${name}-label-accessibility.json`,
            JSON.stringify(labels.violations, null, 2),
        );
        expect(labels.violations).toEqual([]);
    };
    await audit("overview");
    for (const [name, file] of [
        ["Managed folders", "folders"],
        ["Storage insights", "insights"],
        ["Activity", "activity"],
    ]) {
        await page
            .getByRole("navigation")
            .getByRole("button", { name, exact: name !== "Activity" })
            .click();
        await expect(
            page.getByRole("heading", { name, exact: true }),
        ).toBeVisible();
        if (file === "insights")
            await expect(
                page.getByRole("button", { name: "Client projects" }),
            ).toBeVisible();
        await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
        await page.screenshot({
            path: `${artifacts}/${file}.png`,
            fullPage: true,
        });
        await audit(file);
    }
    await page
        .getByRole("navigation")
        .getByRole("button", { name: "Managed folders" })
        .click();
    await page.getByText("More actions", { exact: true }).click();
    await audit("more-actions");
    await page.screenshot({
        path: `${artifacts}/more-actions.png`,
        fullPage: true,
    });
    await page.keyboard.press("Escape");
    await page
        .getByRole("button", { name: "Settings for Work projects" })
        .click();
    await page.getByText("Advanced settings", { exact: true }).click();
    await expect(
        page.getByRole("button", { name: "Save settings", exact: true }),
    ).toBeInViewport();
    await page.screenshot({
        path: `${artifacts}/settings.png`,
        fullPage: true,
    });
    await audit("settings");
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await page.setViewportSize({ width: 1000, height: 720 });
    await page.screenshot({ path: `${artifacts}/compact.png`, fullPage: true });
    for (const [name, file] of [
        ["Storage insights", "compact-insights"],
        ["Activity", "compact-activity"],
    ]) {
        await page
            .getByRole("navigation", { name: "Main navigation" })
            .getByRole("button", { name, exact: name !== "Activity" })
            .click();
        if (name === "Storage insights")
            await expect(
                page.getByRole("button", { name: "Client projects" }),
            ).toBeVisible();
        await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
        await page.screenshot({
            path: `${artifacts}/${file}.png`,
            fullPage: true,
        });
        const table = page.getByRole("region", {
            name:
                name === "Activity" ? "Activity table" : "Storage items table",
        });
        await table.focus();
        await expect(table).toBeFocused();
        await table.evaluate((element) => {
            element.scrollLeft = element.scrollWidth;
        });
        await page.screenshot({
            path: `${artifacts}/${file}-scrolled.png`,
            fullPage: true,
        });
    }
    await page.setViewportSize({ width: 900, height: 640 });
    for (const [name, file] of [
        ["Overview", "minimum-overview"],
        ["Managed folders", "minimum-folders"],
        ["Storage insights", "minimum-insights"],
        ["Activity", "minimum-activity"],
    ]) {
        await page
            .getByRole("navigation", { name: "Main navigation" })
            .getByRole("button", { name, exact: name !== "Activity" })
            .click();
        await expect(
            page.getByRole("heading", { name, exact: true }),
        ).toBeInViewport();
        if (name === "Storage insights")
            await expect(
                page.getByRole("button", { name: "Client projects" }),
            ).toBeVisible();
        if (name === "Activity")
            await expect(
                page.getByRole("button", { name: "Try again", exact: true }),
            ).toBeInViewport({ ratio: 0.99 });
        await page.screenshot({
            path: `${artifacts}/${file}.png`,
            fullPage: true,
        });
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth > innerWidth,
            ),
        ).toBe(false);
    }
    const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth > window.innerWidth,
    );
    expect(overflow).toBe(false);
});
