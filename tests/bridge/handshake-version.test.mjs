/*
 * GH-61: the handshake payload reports the element's build-time version and how
 * the SDK was loaded ("embedded" | "collision" | "error"), so the host can assert
 * the pairing and refuse foreign/broken sessions. null version = none to report.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { loadBridge, flush, plain } from "./load-bridge.mjs";
import { FormFillerStub } from "./form-filler-stub.mjs";

const handshakePayload = h => plain(h.sent("status.handshake")[0].payload);

test("reports the element's static version in the handshake payload", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkVersion: "0.4.0" });
    await flush();

    assert.deepEqual(handshakePayload(h),
        { client: { name: "tiro-web-sdk", version: "0.4.0", source: "embedded" } });
});

test("reports version null when the SDK exposes none", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true });
    await flush();

    assert.deepEqual(handshakePayload(h),
        { client: { name: "tiro-web-sdk", version: null, source: "embedded" } });
});

test("reports source 'error' when the SDK failed to load", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkLoad: "fail" });
    await flush();

    assert.deepEqual(handshakePayload(h),
        { client: { name: "tiro-web-sdk", version: null, source: "error" } });
});

test("reports a foreign element's version with source 'collision'", async () => {
    const h = await loadBridge([new FormFillerStub()], {
        host: true,
        predefinedElement: { version: "0.2.1" },
    });
    await flush();

    assert.deepEqual(handshakePayload(h),
        { client: { name: "tiro-web-sdk", version: "0.2.1", source: "collision" } });
});

test("every handshake retry carries the same payload", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkVersion: "0.4.0" });
    await flush();

    const first = handshakePayload(h);
    for (const msg of h.sent("status.handshake")) {
        assert.deepEqual(plain(msg.payload), first);
    }
});
