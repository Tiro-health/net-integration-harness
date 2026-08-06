/*
 * GH-50 item 2 — SMART Web Messaging acknowledgement contract.
 *
 * Every inbound host request gets exactly one response envelope back, correlated by
 * responseToMessageId. The .NET side matches on that id, so a missing or misshapen
 * ack leaves the host awaiting a reply that never comes until its 30s timeout.
 *
 * Pure bridge logic — no element behaviour involved — so these assertions don't
 * depend on the stub modelling anything about tiro-web-sdk.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, flush } from "./load-bridge.mjs";

async function bridge() {
    const element = new FormFillerStub();
    const harness = await loadBridge([element], { host: true });
    await flush();
    return { element, ...harness };
}

/** The response the bridge posted for a given inbound messageId. */
const responseTo = (harness, messageId) =>
    harness.responses().find(m => m.responseToMessageId === messageId);

test("a handled message is acknowledged with a base response", async () => {
    const h = await bridge();

    h.receive({ messageId: "m1", messageType: "ui.form.persist", payload: {} });
    await flush();

    const ack = responseTo(h, "m1");
    assert.ok(ack, "ui.form.persist must be acknowledged");
    assert.equal(ack.payload.$type, "base");
    assert.equal(ack.additionalResponsesExpected, false);
});

test("ui.form.persist is a deliberate no-op, not an unknown type", async () => {
    // The bridge registers an empty handler purely so this acks instead of erroring.
    // Without it the host would see UnknownMessageTypeException for a protocol message
    // it is entitled to send.
    const h = await bridge();

    h.receive({ messageId: "m1", messageType: "ui.form.persist", payload: {} });
    await flush();

    assert.equal(responseTo(h, "m1").payload.$type, "base");
});

test("an unknown message type gets a typed error response", async () => {
    const h = await bridge();

    h.receive({ messageId: "m2", messageType: "nonsense.message", payload: {} });
    await flush();

    const response = responseTo(h, "m2");
    assert.ok(response, "an unknown type must still get a response, not silence");
    assert.equal(response.payload.$type, "error");
    assert.equal(response.payload.errorType, "UnknownMessageTypeException");
    assert.match(response.payload.errorMessage, /nonsense\.message/);
});

test("a throwing handler reports HandlerException and does not also ack", async () => {
    const h = await bridge();

    // sdc.configureContext spreads the payload into the stored context; a null payload
    // makes the handler throw. Any throwing handler exercises the same path.
    h.window.SmartWebMessaging.listeners["boom.test"] = () => {
        throw new Error("handler exploded");
    };
    h.receive({ messageId: "m3", messageType: "boom.test", payload: {} });
    await flush();

    const responses = h.responses().filter(m => m.responseToMessageId === "m3");
    assert.equal(responses.length, 1, "exactly one response — an error must not be followed by a base ack");
    assert.equal(responses[0].payload.$type, "error");
    assert.equal(responses[0].payload.errorType, "HandlerException");
    assert.equal(responses[0].payload.errorMessage, "handler exploded");
});

test("responses are not themselves acknowledged", async () => {
    // An inbound envelope carrying responseToMessageId resolves a pending request; it
    // must not be treated as a request needing its own reply, or the two sides ping-pong.
    const h = await bridge();
    const before = h.responses().length;

    h.receive({ messageId: "m4", responseToMessageId: "unknown-id", payload: { $type: "base" } });
    await flush();

    assert.equal(h.responses().length, before, "a response envelope must not generate a response");
});

test("window.tiro.cancel sends ui.done and fires tiro-cancelled", async () => {
    const h = await bridge();

    h.window.tiro.cancel();
    await flush();

    assert.equal(h.sent("ui.done").length, 1, "cancel must notify the host");
    assert.equal(h.fired("tiro-cancelled").length, 1, "and give the page its status hook");
});
