/*
 * Loads the ACTUAL shipped bridge (src/.../WebAssets/tiro-swm-bridge.js) into a
 * vm context with the minimum DOM surface it touches. The bytes under test are
 * the bytes shipped — the file is read from its real location, never copied.
 *
 * No host transport is installed (window.chrome.webview is absent), so
 * SmartWebMessaging.init() returns false and the bridge stops before starting
 * handshake retries. The message handlers are registered by that point, which is
 * what the tests drive directly — no timers, no network, no jsdom.
 */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
export const BRIDGE_PATH = resolve(here, "../../src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js");

/** Flush pending microtasks (the bridge bootstraps through a promise chain). */
export const flush = async (turns = 6) => {
    for (let i = 0; i < turns; i++) await Promise.resolve();
};

/**
 * @param {object[]} formFillers stub elements returned by querySelectorAll("tiro-form-filler")
 * @returns {Promise<{window: any, document: any, warnings: string[]}>}
 */
export async function loadBridge(formFillers) {
    const warnings = [];
    const errors = [];

    const document = {
        readyState: "complete", // bootstrap() runs immediately rather than waiting on DOMContentLoaded
        addEventListener() {},
        dispatchEvent() { return true; },
        querySelectorAll(selector) {
            return selector === "tiro-form-filler" ? formFillers : [];
        },
        querySelector() { return null; },
        createElement() { return { setAttribute() {} }; },
        head: { appendChild() {} },
    };

    const window = { document };
    window.window = window;

    const sandbox = {
        window,
        document,
        console: {
            log() {},
            warn: (...a) => warnings.push(a.join(" ")),
            error: (...a) => errors.push(a.join(" ")),
        },
        crypto: { randomUUID: () => "00000000-0000-4000-8000-000000000000" },
        CustomEvent: class CustomEvent {
            constructor(type, init) { this.type = type; this.detail = init?.detail; }
        },
        setTimeout,
        clearTimeout,
        queueMicrotask,
        Promise,
        JSON,
        Object,
        Array,
        Map,
        Date,
        Error,
    };
    sandbox.globalThis = sandbox;

    const context = vm.createContext(sandbox);
    vm.runInContext(readFileSync(BRIDGE_PATH, "utf8"), context, { filename: BRIDGE_PATH });

    // bootSentry() resolves through a promise chain before handlers are wired.
    await flush();

    return { window, document, warnings, errors };
}

/** Invoke a registered bridge handler for a host message type. */
export function deliver(window, messageType, payload) {
    const handler = window.SmartWebMessaging.listeners[messageType];
    if (!handler) throw new Error(`bridge registered no handler for ${messageType}`);
    return handler(payload);
}
