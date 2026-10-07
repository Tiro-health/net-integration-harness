/*
 * Regression tests for GH-48 — launch context dropped when the host's SDC endpoint
 * differs from the tiro-web-sdk's built-in default.
 *
 * <tiro-form-filler> rebuilds its SDC client inside willUpdate() whenever
 * sdcEndpointAddress changes, seeding the replacement from _pendingLaunchContext
 * alone. A launch context applied in the SAME Lit update as the endpoint change
 * lands on the outgoing client and is discarded, so $populate goes out with no
 * `context` parameters and every %patient / %encounter expression resolves empty.
 *
 * The bug is invisible on https://sdc.tiro.health/fhir/r5 because that is the
 * SDK's own default: writing it back is not a change, so no rebuild happens and
 * the launch context survives. It only bites integrators who point the viewer at
 * their own server — which the README tells them to do.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush } from "./load-bridge.mjs";

const PATIENT = {
    resourceType: "Patient",
    name: [{ family: "da Vinci", given: ["Leonardo"], text: "Leonardo da Vinci" }],
    birthDate: "1452-04-15",
};
const SPECIMEN = { resourceType: "Specimen", id: "specimen-1" };
const CANONICAL = "http://templates.tiro.health/templates/44ed83d0ee324811a170dd9b4098bb3a";

const DISPLAY_PAYLOAD = {
    questionnaire: CANONICAL,
    context: {
        launchContext: [
            { name: "patient", contentResource: PATIENT },
            { name: "specimen", contentResource: SPECIMEN },
        ],
    },
};

/** Drive a full configure + displayQuestionnaire exchange against a fresh bridge. */
async function run(sdcServer) {
    const element = new FormFillerStub();
    const { window } = await loadBridge([element]);

    // The element mounts and builds its first client before the host configures it.
    await flush();

    if (sdcServer) deliver(window, "sdc.configure", { configuration: { sdcServer } });
    deliver(window, "sdc.displayQuestionnaire", DISPLAY_PAYLOAD);

    await flush();
    return element;
}

test("launch context survives a custom SDC endpoint (GH-48)", async () => {
    const element = await run("https://sdc-dev.tiro.health/fhir/r5");

    assert.equal(
        element.sdcEndpointAddress,
        "https://sdc-dev.tiro.health/fhir/r5",
        "the configured endpoint should reach the element",
    );
    assert.ok(
        element.launchContext,
        "launchContext must not be undefined — $populate would omit every context parameter",
    );
    assert.deepEqual(
        element.launchContext.patient,
        PATIENT,
        "%patient resolves from the patient launch context entry",
    );
    assert.deepEqual(element.launchContext.specimen, SPECIMEN);
});

test("launch context survives the SDK's default SDC endpoint", async () => {
    // Regression guard for the path that already worked, so a fix for the custom
    // endpoint cannot silently break the default one.
    const element = await run("https://sdc.tiro.health/fhir/r5");

    assert.ok(element.launchContext, "launchContext must be present on the default endpoint too");
    assert.deepEqual(element.launchContext.patient, PATIENT);
});

test("launch context survives when no sdc.configure precedes the questionnaire", async () => {
    const element = await run(null);

    assert.ok(element.launchContext, "launchContext must be present without any endpoint override");
    assert.deepEqual(element.launchContext.patient, PATIENT);
});

test("several entries under one name reach the form as a collection", async () => {
    // SDC 4.0: a launchContext with multiplesAllowed holds several resources under one
    // name. Collapsing them to the last entry would leave %specimen.where(...) only one
    // specimen to choose from, with no error anywhere.
    const SPECIMEN_B = { resourceType: "Specimen", id: "specimen-2" };
    const SPECIMEN_C = { resourceType: "Specimen", id: "specimen-3" };
    const element = new FormFillerStub();
    const { window } = await loadBridge([element]);
    await flush();
    deliver(window, "sdc.displayQuestionnaire", {
        questionnaire: CANONICAL,
        context: {
            launchContext: [
                { name: "patient", contentResource: PATIENT },
                { name: "specimen", contentResource: SPECIMEN },
                { name: "specimen", contentResource: SPECIMEN_B },
                { name: "specimen", contentResource: SPECIMEN_C },
            ],
        },
    });
    await flush();

    assert.deepEqual(element.launchContext.specimen, [SPECIMEN, SPECIMEN_B, SPECIMEN_C], "all three, in the order sent");
    assert.deepEqual(element.launchContext.patient, PATIENT, "a name sent once stays a single resource");
});

test("questionnaire is applied, and only after the launch context is in place", async () => {
    const element = await run("https://sdc-dev.tiro.health/fhir/r5");

    assert.equal(element.questionnaire, CANONICAL, "the questionnaire must still be applied");

    const log = element.setAttributeLog;
    assert.ok(
        log.indexOf("launch-context") < log.indexOf("questionnaire"),
        `launch-context must be applied before questionnaire, got: ${log.join(" -> ")}`,
    );
});
