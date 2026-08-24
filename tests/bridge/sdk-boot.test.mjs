/*
 * GH-60: the bridge injects the embedded tiro-web-sdk itself. The page carries no
 * SDK script tag; a leftover one (foreign element already defined) must be reported
 * and must NOT trigger a second injection.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { loadBridge, flush } from "./load-bridge.mjs";
import { FormFillerStub } from "./form-filler-stub.mjs";

// What the .NET host injects: the bundle URL with the pinned SDK version in the file name.
const SDK_URL = "https://tiro-sdk.example/tiro-web-sdk.9.9.9-test.iife.js";

test("injects the embedded SDK from the harness virtual host before wiring", async () => {
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true });
    await flush();

    assert.deepEqual(h.injectedScripts.map(s => s.src), [SDK_URL]);
    // Wiring happened after the load: the requestSubmit handler is registered.
    assert.ok(h.window.SmartWebMessaging.listeners["ui.form.requestSubmit"]);
    assert.equal(h.fired("tiro-sdk-error").length, 0);
    assert.equal(h.fired("tiro-sdk-collision").length, 0);
});

test("no injected URL is a load failure, not a guessed path", async () => {
    // There is no default URL: only the host knows the versioned file name. A missing
    // injection must report as a load error, which the host refuses on, rather than
    // fetching a path nothing publishes.
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkUrl: null });
    await flush();

    assert.deepEqual(h.injectedScripts, [], "nothing should be fetched without a URL");
    assert.equal(h.fired("tiro-sdk-error").length, 1);
    assert.ok(h.errors.some(e => e.includes("__tiroSdkUrl was not injected")),
        `expected a diagnostic, got: ${JSON.stringify(h.errors)}`);
});

test("foreign pre-defined element: no injection, hard error, wiring continues", async () => {
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true, predefinedElement: { version: "0.2.1" } });
    await flush();

    assert.equal(h.injectedScripts.length, 0);
    assert.equal(h.fired("tiro-sdk-collision").length, 1);
    assert.ok(h.errors.some(e => e.includes("Remove the tiro-web-sdk <script> tag")),
        "must tell the integrator to remove their script tag");
    // The foreign element is still wired so the handshake (and its version report) happens.
    assert.ok(h.window.SmartWebMessaging.listeners["sdc.displayQuestionnaire"]);
});

test("not-yet-executed page SDK script tag: no injection, collision reported", async () => {
    // An async/defer page tag hasn't defined the element at bootstrap time — the
    // DOM scan must still catch it (the registry check alone cannot).
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true, pageSdkScriptTag: true });
    await flush();

    assert.equal(h.injectedScripts.length, 0);
    assert.equal(h.fired("tiro-sdk-collision").length, 1);
    assert.ok(h.errors.some(e => e.includes("Remove the tiro-web-sdk <script> tag")));
});

test("a refused handshake rejects immediately, not after the 30s timeout", async () => {
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true, predefinedElement: {} });
    await flush();

    const attempt = h.sent("status.handshake")[0];
    h.receive({
        responseToMessageId: attempt.messageId,
        payload: { $type: "error", errorType: "HandlerException", errorMessage: "session refused (GH-61)" },
    });
    await flush();

    assert.equal(h.fired("tiro-disconnected").length, 1,
        "an error ack is terminal — the page must learn of the refusal without waiting out the retry window");
    assert.equal(h.fired("tiro-connected").length, 0);
});

test("SDK load failure: error surfaced, bootstrap still reaches the handshake", async () => {
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true, sdkLoad: "fail" });
    await flush();

    assert.equal(h.fired("tiro-sdk-error").length, 1);
    assert.ok(h.errors.some(e => e.includes("failed to load the embedded tiro-web-sdk")));
    assert.ok(h.sent("status.handshake").length >= 1,
        "handshake must still go out so the host gets a diagnosable session, not a timeout");
});

test("no crossorigin attribute on the injected script (DenyCors mapping)", async () => {
    const el = new FormFillerStub();
    const h = await loadBridge([el], { host: true });
    await flush();

    assert.equal(h.injectedScripts.length, 1);
    assert.equal(h.injectedScripts[0].crossOrigin, undefined,
        "a crossorigin attribute would break the DenyCors virtual-host load");
});
