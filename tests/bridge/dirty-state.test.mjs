/*
 * GH-46 — the bridge forwards <tiro-form-filler>'s dirty-state transitions to the
 * host as fire-and-forget `ui.form.dirtyChanged` events.
 *
 * The frontend side shipped in tiro-web-sdk 0.3.1-dev.1. Verified against that
 * published bundle:
 *
 *   get isDirty(){ return this.wrapperRef.current?.isDirty() ?? !1 }
 *   syncDirtyState(){ const A = this.isDirty;
 *                     A !== this._lastDirtyState &&
 *                       (this._lastDirtyState = A, dispatch(this, "tiro-dirty-change", { isDirty: A })) }
 *
 * Note the element already de-duplicates: it only dispatches on a *transition*, so
 * the bridge deliberately does no filtering of its own. These tests pin the
 * forwarding contract (message type, payload shape, fire-and-forget), not the
 * element's transition logic.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, flush } from "./load-bridge.mjs";

/** Dispatch a tiro-dirty-change the way the element does. */
const dirtyChange = (element, isDirty) =>
    element.dispatchEvent({ type: "tiro-dirty-change", detail: { isDirty } });

async function bridgeWithHost() {
    const element = new FormFillerStub();
    const harness = await loadBridge([element], { host: true });
    await flush();
    return { element, ...harness };
}

test("tiro-dirty-change is forwarded as ui.form.dirtyChanged", async () => {
    const { element, sent } = await bridgeWithHost();

    dirtyChange(element, true);

    const messages = sent("ui.form.dirtyChanged");
    assert.equal(messages.length, 1, "exactly one dirtyChanged event should be posted");
    assert.equal(messages[0].payload.isDirty, true);
});

test("the cleared transition is forwarded too", async () => {
    const { element, sent } = await bridgeWithHost();

    dirtyChange(element, true);
    dirtyChange(element, false);

    const messages = sent("ui.form.dirtyChanged");
    assert.deepEqual(
        messages.map(m => m.payload.isDirty),
        [true, false],
        "both transitions must reach the host, or IsDirty latches on stale state",
    );
});

test("dirtyChanged is fire-and-forget, not a request awaiting a response", async () => {
    // sendEvent, not sendRequest: the host observes rather than acknowledges, mirroring
    // window.tiro.cancel()'s ui.done. A sendRequest would register a pending request and
    // arm a 30s rejection timer for a response that never comes.
    const { element, sent, window } = await bridgeWithHost();

    const pendingBefore = window.SmartWebMessaging.pendingRequests.size;
    dirtyChange(element, true);

    assert.equal(
        window.SmartWebMessaging.pendingRequests.size,
        pendingBefore,
        "no pending request should be registered for a fire-and-forget event",
    );

    const [message] = sent("ui.form.dirtyChanged");
    assert.ok(message.messageId, "envelope still carries a messageId");
    assert.equal(message.messagingHandle, "smart-web-messaging");
    assert.equal(message.responseToMessageId, undefined, "not a response envelope");
});

test("wiring survives a questionnaire display (no listener clobbering)", async () => {
    // wireFormFiller registers the dirty listener once, at wire time; displaying a
    // questionnaire must not detach or duplicate it.
    const { element, sent, window } = await bridgeWithHost();

    window.SmartWebMessaging.listeners["sdc.displayQuestionnaire"]({
        questionnaire: "http://example.org/Questionnaire/q",
        context: { launchContext: [{ name: "patient", contentResource: { resourceType: "Patient" } }] },
    });
    await flush();

    dirtyChange(element, true);

    assert.equal(sent("ui.form.dirtyChanged").length, 1, "listener fires exactly once after display");
});
