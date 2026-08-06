/*
 * GH-50 item 5 — the tiro-submit -> form.submitted round trip.
 *
 * When the form-filler completes a submit it fires tiro-submit; the bridge turns that
 * into a form.submitted REQUEST (not an event — the host acknowledges it) and only then
 * fires the page's tiro-submitted hook. Everything here is bridge logic: sanitising the
 * response, the status fallback, optional narrative generation, and the failure path.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, flush, plain } from "./load-bridge.mjs";

const RESPONSE = {
    resourceType: "QuestionnaireResponse",
    status: "completed",
    item: [{ linkId: "q1", answer: [{ valueString: "yes" }] }],
};

async function bridge() {
    const element = new FormFillerStub();
    const harness = await loadBridge([element], { host: true });
    await flush();
    return { element, ...harness };
}

/** Fire tiro-submit the way the element does, then let the bridge's async handler run. */
async function submitFrom(element, response) {
    element.dispatchEvent({ type: "tiro-submit", detail: { response } });
    await flush(12);
}

/** Acknowledge the outstanding form.submitted so the bridge's sendRequest resolves. */
async function ack(harness) {
    const [message] = harness.sent("form.submitted");
    harness.receive({ responseToMessageId: message.messageId, payload: { $type: "base" } });
    await flush(12);
}

test("tiro-submit produces a form.submitted request carrying response and outcome", async () => {
    const h = await bridge();

    await submitFrom(h.element, RESPONSE);

    const messages = h.sent("form.submitted");
    assert.equal(messages.length, 1);

    const payload = plain(messages[0].payload);
    assert.deepEqual(payload.response.item, RESPONSE.item);
    assert.equal(payload.outcome.resourceType, "OperationOutcome");
    assert.equal(payload.outcome.issue[0].severity, "information");
});

test("it is a request, not a fire-and-forget event", async () => {
    // The host acknowledges form.submitted; the bridge must be awaiting that reply,
    // otherwise a failed submit is never surfaced to the page.
    //
    // Asserted against this message's own id rather than pendingRequests.size —
    // the handshake retry loop keeps its own entries in there, so the total is noise.
    const h = await bridge();

    await submitFrom(h.element, RESPONSE);

    const [message] = h.sent("form.submitted");
    assert.ok(
        h.window.SmartWebMessaging.pendingRequests.has(message.messageId),
        "form.submitted must register a pending request keyed by its messageId",
    );
});

test("tiro-submitted fires only after the host acknowledges", async () => {
    const h = await bridge();

    await submitFrom(h.element, RESPONSE);
    assert.equal(h.fired("tiro-submitted").length, 0, "not before the ack");

    await ack(h);
    assert.equal(h.fired("tiro-submitted").length, 1, "and exactly once after it");
});

test("a rejected form.submitted fires tiro-submit-error instead", async () => {
    const h = await bridge();
    await submitFrom(h.element, RESPONSE);

    const [message] = h.sent("form.submitted");
    h.receive({
        responseToMessageId: message.messageId,
        payload: { $type: "error", errorMessage: "host refused" },
    });
    await flush(12);

    assert.equal(h.fired("tiro-submitted").length, 0, "a refused submit must not report success");
    assert.equal(h.fired("tiro-submit-error").length, 1);
});

test("a missing status falls back to completed", async () => {
    const h = await bridge();

    await submitFrom(h.element, { ...RESPONSE, status: undefined });

    assert.equal(plain(h.sent("form.submitted")[0].payload).response.status, "completed");
});

test("a status the form set is preserved", async () => {
    // The fallback is defensive only — it must never overwrite a real status, or
    // save-draft would be silently promoted to a finalized response.
    const h = await bridge();

    await submitFrom(h.element, { ...RESPONSE, status: "in-progress" });

    assert.equal(plain(h.sent("form.submitted")[0].payload).response.status, "in-progress");
});

test("nulls are stripped from the response", async () => {
    // sanitize() drops nulls so the .NET FHIR deserializer doesn't choke on explicit
    // null members, which are invalid in FHIR JSON.
    const h = await bridge();

    await submitFrom(h.element, {
        ...RESPONSE,
        authored: null,
        item: [{ linkId: "q1", text: null, answer: [{ valueString: "yes" }] }],
    });

    const response = plain(h.sent("form.submitted")[0].payload).response;
    assert.ok(!("authored" in response), "null members must be removed, not sent as null");
    assert.ok(!("text" in response.item[0]), "nested nulls too");
    assert.deepEqual(response.item[0].answer, [{ valueString: "yes" }], "real values survive");
});

test("the generated narrative is attached to the response", async () => {
    const h = await bridge();
    h.element.sdcClient = { generateNarrative: async () => "<div>narrative</div>" };

    await submitFrom(h.element, RESPONSE);

    assert.equal(plain(h.sent("form.submitted")[0].payload).response.text, "<div>narrative</div>");
});

test("a narrative failure still submits", async () => {
    // Narrative generation is a nice-to-have; losing it must not cost the user their
    // completed form.
    const h = await bridge();
    h.element.sdcClient = {
        generateNarrative: async () => { throw new Error("SDC server down"); },
    };

    await submitFrom(h.element, RESPONSE);

    const messages = h.sent("form.submitted");
    assert.equal(messages.length, 1, "the submit must still reach the host");
    assert.equal(plain(messages[0].payload).response.text, undefined, "just without a narrative");
    assert.ok(
        h.warnings.some(w => w.includes("Narrative generation failed")),
        "and the failure should be visible in the console",
    );
});

test("no sdcClient means no narrative and no crash", async () => {
    const h = await bridge();
    h.element.sdcClient = undefined;

    await submitFrom(h.element, RESPONSE);

    assert.equal(h.sent("form.submitted").length, 1);
});
