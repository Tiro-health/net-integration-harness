/*
 * GH-60: the bridge injects the embedded tiro-web-sdk itself. The page carries no
 * SDK script tag; a leftover one (foreign element already defined) must be reported
 * and must NOT trigger a second injection.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { loadBridge, flush } from "./load-bridge.mjs";
import { FormFillerStub } from "./form-filler-stub.mjs";

const SDK_URL = "https://tiro-sdk.example/tiro-web-sdk.iife.js";

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
