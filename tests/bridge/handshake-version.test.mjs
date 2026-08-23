/*
 * GH-61: the handshake payload reports the element's build-time version so the
 * host can assert it equals the embedded bundle. null = no version to report.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { loadBridge, flush, plain } from "./load-bridge.mjs";
import { FormFillerStub } from "./form-filler-stub.mjs";

const handshakePayload = h => plain(h.sent("status.handshake")[0].payload);

test("reports the element's static version in the handshake payload", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkVersion: "0.4.0" });
    await flush();

    assert.deepEqual(handshakePayload(h), { client: { name: "tiro-web-sdk", version: "0.4.0" } });
});

test("reports version null when the SDK exposes none", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true });
    await flush();

    assert.deepEqual(handshakePayload(h), { client: { name: "tiro-web-sdk", version: null } });
});

test("reports version null when the SDK failed to load", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkLoad: "fail" });
    await flush();

    assert.deepEqual(handshakePayload(h), { client: { name: "tiro-web-sdk", version: null } });
});

test("reports a foreign element's version so the host can flag the mismatch", async () => {
    const h = await loadBridge([new FormFillerStub()], {
        host: true,
        predefinedElement: { version: "0.2.1" },
    });
    await flush();

    assert.deepEqual(handshakePayload(h), { client: { name: "tiro-web-sdk", version: "0.2.1" } });
});

test("every handshake retry carries the same payload", async () => {
    const h = await loadBridge([new FormFillerStub()], { host: true, sdkVersion: "0.4.0" });
    await flush();

    const first = handshakePayload(h);
    // Acknowledge nothing; just confirm the payload the retry loop reuses is stable.
    for (const msg of h.sent("status.handshake")) {
        assert.deepEqual(plain(msg.payload), first);
    }
});
