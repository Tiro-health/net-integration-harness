/*
 * GH-50 items 3, 4 and 6 — sdc.configure handling and launch-context edge cases.
 *
 * Item 3 matters most: the element rebuilds its SDC client on EITHER endpoint
 * property —
 *
 *   (changed.has("sdcEndpointAddress") || changed.has("dataEndpointAddress")) && ...
 *
 * — so DataEndpointAddress is a second, previously untested route into the GH-48
 * defect. The fix (deferring launch-context past updateComplete) covers both, but
 * only the SDC path was pinned.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush } from "./load-bridge.mjs";

const PATIENT = { resourceType: "Patient", id: "p1" };
const CANONICAL = "http://example.org/Questionnaire/q";

const withLaunchContext = launchContext => ({
    questionnaire: CANONICAL,
    context: { launchContext },
});

/** Run a configure + display exchange and hand back the element. */
async function run(configuration, payload = withLaunchContext([{ name: "patient", contentResource: PATIENT }])) {
    const element = new FormFillerStub();
    const { window } = await loadBridge([element]);
    await flush();
    if (configuration) deliver(window, "sdc.configure", configuration);
    deliver(window, "sdc.displayQuestionnaire", payload);
    await flush();
    return element;
}

test("a data-endpoint change does not drop the launch context (GH-48, second route)", async () => {
    const element = await run({ dataServer: "https://data.example/fhir" });

    assert.equal(element.dataEndpointAddress, "https://data.example/fhir");
    assert.ok(element.launchContext, "the data-endpoint rebuild must not discard launch context");
    assert.deepEqual(element.launchContext.patient, PATIENT);
});

test("both endpoints changing at once still preserves the launch context", async () => {
    const element = await run({
        configuration: { sdcServer: "https://sdc-dev.example/fhir/r5" },
        dataServer: "https://data.example/fhir",
    });

    assert.equal(element.sdcEndpointAddress, "https://sdc-dev.example/fhir/r5");
    assert.equal(element.dataEndpointAddress, "https://data.example/fhir");
    assert.deepEqual(element.launchContext.patient, PATIENT);
});

test("readOnly is applied as an attribute, before the questionnaire", async () => {
    // Must be an attribute, not a property: the SDK script is deferred, so the element
    // may not be upgraded yet, and pre-upgrade property writes get shadowed by Lit's
    // accessors. Attributes survive upgrade.
    const element = await run({ configuration: { readOnly: true } });

    assert.equal(element.getAttribute("read-only"), "", "read-only must be set as an attribute");
    assert.ok(
        element.setAttributeLog.indexOf("questionnaire") >= 0,
        "precondition: questionnaire was applied",
    );
    assert.equal(element.readOnly, true);
});

test("readOnly is never set when the host omits it", async () => {
    // Only ever set, never cleared — false is already the page-side default, so an
    // absent flag must leave the element editable.
    const element = await run({ configuration: { sdcServer: "https://sdc-dev.example/fhir/r5" } });

    assert.equal(element.getAttribute("read-only"), null);
    assert.equal(element.readOnly, false);
});

test("launch-context entries without a contentResource are skipped", async () => {
    // A reference-only entry can't be inlined into $populate; including it as undefined
    // would send a context parameter with no content.
    const element = await run(null, withLaunchContext([
        { name: "patient", contentResource: PATIENT },
        { name: "encounter", contentReference: { reference: "Encounter/1" } },
    ]));

    assert.deepEqual(Object.keys(element.launchContext), ["patient"]);
});

test("an entirely empty launch context leaves the attribute unset", async () => {
    // Writing "{}" would look like a supplied-but-empty context. The element's own
    // default is already an empty map, so the attribute must simply not appear.
    const element = await run(null, withLaunchContext([
        { name: "encounter", contentReference: { reference: "Encounter/1" } },
    ]));

    assert.equal(element.getAttribute("launch-context"), null);
    assert.ok(
        !element.setAttributeLog.includes("launch-context"),
        "launch-context must never be written when there is nothing to send",
    );
});

test("a display with no context at all still renders the questionnaire", async () => {
    const element = await run(null, { questionnaire: CANONICAL });

    assert.equal(element.questionnaire, CANONICAL);
    assert.equal(element.getAttribute("launch-context"), null);
});

test("an initial response is applied before the questionnaire", async () => {
    // initial-response has to be readable when the element initialises on the
    // questionnaire flip, or the form paints empty and then reloads.
    const element = await run(null, {
        questionnaire: CANONICAL,
        questionnaireResponse: { resourceType: "QuestionnaireResponse", status: "in-progress" },
    });

    const log = element.setAttributeLog;
    assert.ok(log.includes("initial-response"), "initial-response must be applied");
    assert.ok(
        log.indexOf("initial-response") < log.indexOf("questionnaire"),
        `initial-response must precede questionnaire, got: ${log.join(" -> ")}`,
    );
});

test("sdc.configureContext carries launch context into the next questionnaire", async () => {
    const element = new FormFillerStub();
    const { window } = await loadBridge([element]);
    await flush();

    deliver(window, "sdc.configureContext", {
        launchContext: [{ name: "patient", contentResource: PATIENT }],
    });
    deliver(window, "sdc.displayQuestionnaire", { questionnaire: CANONICAL });
    await flush();

    assert.deepEqual(
        element.launchContext.patient,
        PATIENT,
        "context configured ahead of time must apply to the questionnaire that follows",
    );
});
