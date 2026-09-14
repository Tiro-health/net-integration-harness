/*
 * sdc.displayQuestionnaire carrying the Questionnaire itself, rather than a canonical
 * URL the SDC server resolves.
 *
 * The bridge's job here is one branch: a string goes on the `questionnaire` attribute
 * as-is, anything else is JSON-stringified first. <tiro-form-filler>'s attribute
 * converter parses a value starting with `{` or `[` and passes anything else through
 * as a canonical. Getting the branch wrong is invisible to a type-check — a stringified
 * object would be read back as a canonical URL, and the form would render empty.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush } from "./load-bridge.mjs";

const CANONICAL = "http://templates.tiro.health/templates/44ed83d0ee324811a170dd9b4098bb3a|1.2.7";

const INLINE = {
    resourceType: "Questionnaire",
    id: "private-intake",
    status: "active",
    title: "Intake",
    item: [{ linkId: "q1", type: "string", text: "Chief complaint" }],
};

async function display(questionnaire) {
    const element = new FormFillerStub();
    const h = await loadBridge([element]);
    await flush();
    deliver(h.window, "sdc.displayQuestionnaire", { questionnaire });
    await flush();
    return { element, ...h };
}

test("an inline Questionnaire is applied as JSON", async () => {
    const { element } = await display(INLINE);

    const applied = element.attributes.get("questionnaire");
    assert.ok(applied.startsWith("{"), "the element parses a value starting with { as an object");

    const parsed = JSON.parse(applied);
    assert.equal(parsed.resourceType, "Questionnaire");
    assert.equal(parsed.id, "private-intake");
    assert.equal(parsed.item[0].linkId, "q1", "the items have to survive the round trip");
});

test("a canonical URL is still applied verbatim", async () => {
    const { element } = await display(CANONICAL);

    assert.equal(
        element.attributes.get("questionnaire"), CANONICAL,
        "a string must not be JSON-stringified, or it arrives quoted and resolves to nothing",
    );
});

test("an inline Questionnaire is acked like any other display", async () => {
    const { responses } = await display(INLINE);

    const acks = responses();
    assert.equal(acks.length, 1, "the host waits on this ack to reach ContextSet");
    assert.notEqual(acks[0].payload?.$type, "error");
});

test("launch context still precedes an inline questionnaire", async () => {
    // Same ordering requirement as the canonical path: the element rebuilds its SDC
    // client when context changes, and a questionnaire applied first is discarded.
    const element = new FormFillerStub();
    const h = await loadBridge([element]);
    await flush();

    deliver(h.window, "sdc.displayQuestionnaire", {
        questionnaire: INLINE,
        context: { launchContext: [{ name: "patient", contentResource: { resourceType: "Patient", id: "p1" } }] },
    });
    await flush();

    const log = element.setAttributeLog;
    assert.ok(
        log.indexOf("launch-context") < log.indexOf("questionnaire"),
        `launch-context must be applied before questionnaire, got: ${log.join(" -> ")}`,
    );
});
