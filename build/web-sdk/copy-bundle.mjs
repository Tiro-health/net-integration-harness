/*
 * Stages the pinned @tiro-health/web-sdk bundle for embedding into
 * Tiro.Health.FormFiller.WebView2 (GH-59/GH-60). Run after `npm ci` in this
 * directory:
 *
 *   cd build/web-sdk && npm ci && node copy-bundle.mjs
 *
 * Copies the pinned bundle to src/.../WebAssets/tiro-web-sdk.iife.js and writes
 * WebAssets/web-sdk.version.json ({ version }) — both gitignored, both embedded as
 * resources at build time. The version is generated from the installed package, never
 * hand-written, so the version in the served URL provably describes the bytes shipped.
 *
 * Fails hard on any mismatch between the pin and the installed package: a
 * staged bundle that doesn't match build/web-sdk/package.json would silently
 * ship an unvalidated pairing.
 */
import { readFileSync, writeFileSync, copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

const pin = JSON.parse(readFileSync(join(here, "package.json"), "utf8"));
const pinnedVersion = pin.dependencies?.["@tiro-health/web-sdk"];
if (!pinnedVersion || !/^\d+\.\d+\.\d+/.test(pinnedVersion)) {
    throw new Error(`build/web-sdk/package.json must pin an exact @tiro-health/web-sdk version; found: ${pinnedVersion}`);
}

const installedDir = join(here, "node_modules", "@tiro-health", "web-sdk");
if (!existsSync(installedDir)) {
    throw new Error("@tiro-health/web-sdk is not installed. Run `npm ci` in build/web-sdk first (requires GitHub Packages read auth — see build/web-sdk/README.md).");
}

const installed = JSON.parse(readFileSync(join(installedDir, "package.json"), "utf8"));
if (installed.version !== pinnedVersion) {
    throw new Error(`Installed @tiro-health/web-sdk ${installed.version} does not match the pin ${pinnedVersion}. Run \`npm ci\` (not \`npm install\`) so the lockfile wins.`);
}

const bundleSrc = join(installedDir, "dist", "tiro-web-sdk.iife.js");
if (!existsSync(bundleSrc)) {
    throw new Error(`Pinned package has no dist/tiro-web-sdk.iife.js: ${bundleSrc}`);
}

const webAssets = join(here, "..", "..", "src", "Tiro.Health.FormFiller.WebView2", "WebAssets");
mkdirSync(webAssets, { recursive: true });

copyFileSync(bundleSrc, join(webAssets, "tiro-web-sdk.iife.js"));
writeFileSync(
    join(webAssets, "web-sdk.version.json"),
    JSON.stringify({ version: pinnedVersion }) + "\n"
);

console.log(`Staged @tiro-health/web-sdk ${pinnedVersion} into WebAssets/.`);
