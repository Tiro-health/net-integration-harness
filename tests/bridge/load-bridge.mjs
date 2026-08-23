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
export const flush = async (turns = 8) => {
    for (let i = 0; i < turns; i++) await Promise.resolve();
};

/**
 * Re-materialise a value the bridge created inside the vm realm as a host-realm one.
 *
 * Object literals built in the sandbox carry that realm's Object.prototype, so
 * assert.deepStrictEqual (which `node:assert/strict` also aliases deepEqual to)
 * fails on the prototype even when every key and value matches — the diff prints
 * two identical-looking objects. JSON round-tripping rebuilds them with host
 * intrinsics so the comparison is about content.
 *
 * Only needed for values the BRIDGE constructed. Anything the stub built (it runs
 * in the host realm) compares fine as-is.
 */
export const plain = value => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));

/**
 * @param {object[]} formFillers stub elements returned by querySelectorAll("tiro-form-filler")
 * @param {object} [opts]
 * @param {boolean} [opts.host] install a stub WebView2 transport
 * @param {"ok"|"fail"} [opts.sdkLoad] how the injected web-sdk script "loads" (GH-60)
 * @param {string} [opts.sdkVersion] static version the loaded element class exposes (GH-61)
 * @param {object} [opts.predefinedElement] pre-registers tiro-form-filler, as a leftover page script tag would
 * @returns {Promise<{window: any, document: any, warnings: string[]}>}
 */
export async function loadBridge(formFillers, { host = false, sdkLoad = "ok", sdkVersion, predefinedElement } = {}) {
    const warnings = [];
    const errors = [];
    let uuidCounter = 0;

    // customElements registry: bootSdk checks it for collisions and the "loaded"
    // SDK script defines the element class in it.
    const registry = new Map();
    if (predefinedElement) registry.set("tiro-form-filler", predefinedElement);
    const customElements = { get: name => registry.get(name) };

    // Scripts the bridge injected via document.head.appendChild, in order.
    const injectedScripts = [];

    // Outbound envelopes the bridge posted to the host, in order. Only captured when
    // a transport is installed; without one SmartWebMessaging.init() returns false and
    // the bridge stops before starting handshake retries.
    const outbound = [];

    // document CustomEvents the bridge fired — its page-facing status hooks
    // (tiro-connected, tiro-submitted, tiro-submit-error, tiro-cancelled, ...).
    const documentEvents = [];

    const document = {
        readyState: "complete", // bootstrap() runs immediately rather than waiting on DOMContentLoaded
        addEventListener() {},
        dispatchEvent(event) { documentEvents.push(event); return true; },
        querySelectorAll(selector) {
            return selector === "tiro-form-filler" ? formFillers : [];
        },
        querySelector() { return null; },
        createElement() { return { setAttribute() {} }; },
        // Simulates script loading: the web-sdk script "defines" the element class
        // (with the configured static version) then fires onload — or onerror when
        // sdkLoad is "fail". Non-script appends (Sentry meta tags) are inert.
        head: {
            appendChild(el) {
                if (!el || typeof el.src !== "string") return;
                injectedScripts.push(el);
                queueMicrotask(() => {
                    if (el.src.includes("tiro-web-sdk")) {
                        if (sdkLoad === "fail") { el.onerror && el.onerror(); return; }
                        registry.set("tiro-form-filler",
                            sdkVersion !== undefined ? { version: sdkVersion } : {});
                    }
                    el.onload && el.onload();
                });
            },
        },
    };

    const window = { document, customElements };
    window.window = window;

    // Minimal WebView2 transport. Installing it makes isWebView2() true, so the bridge
    // proceeds past init() into its handshake retry loop — harmless here because the
    // sandbox's setTimeout is unref'd below, so those pending timers never hold the
    // test process open.
    let hostMessageListener = null;
    if (host) {
        window.chrome = {
            webview: {
                postMessage: message => { outbound.push(message); },
                addEventListener: (type, cb) => { if (type === "message") hostMessageListener = cb; },
            },
        };
    }

    const sandbox = {
        window,
        document,
        customElements,
        console: {
            log() {},
            warn: (...a) => warnings.push(a.join(" ")),
            error: (...a) => errors.push(a.join(" ")),
        },
        // Deterministic but unique: pendingRequests is keyed by messageId, so a constant
        // would make concurrent requests collide. Sequential keeps failures readable.
        crypto: {
            randomUUID: () => `00000000-0000-4000-8000-${String(++uuidCounter).padStart(12, "0")}`,
        },
        CustomEvent: class CustomEvent {
            constructor(type, init) { this.type = type; this.detail = init?.detail; }
        },
        // unref'd so the bridge's handshake retry / timeout timers can never keep the
        // test process alive after the assertions are done.
        setTimeout: (fn, ms, ...args) => {
            const t = setTimeout(fn, ms, ...args);
            if (typeof t?.unref === "function") t.unref();
            return t;
        },
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

    return {
        window,
        document,
        warnings,
        errors,
        outbound,
        documentEvents,
        injectedScripts,
        /** Outbound envelopes of one messageType, newest last. */
        sent: messageType => outbound.filter(m => m.messageType === messageType),
        /** Response envelopes the bridge posted (acks and errors carry no messageType). */
        responses: () => outbound.filter(m => m.responseToMessageId),
        /** document CustomEvents of one type, newest last. */
        fired: type => documentEvents.filter(e => e.type === type),
        /** Simulate a host -> page envelope arriving over the transport. */
        receive: message => {
            if (!hostMessageListener) throw new Error("no host transport installed (pass { host: true })");
            hostMessageListener({ data: message });
        },
    };
}

/** Invoke a registered bridge handler for a host message type. */
export function deliver(window, messageType, payload) {
    const handler = window.SmartWebMessaging.listeners[messageType];
    if (!handler) throw new Error(`bridge registered no handler for ${messageType}`);
    return handler(payload);
}
