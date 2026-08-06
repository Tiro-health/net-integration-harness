/*
 * GH-50 item 1 — ui.form.requestSubmit intent mapping.
 *
 * This is the bug class described in build/bridge-contract/README.md: the
 * submit({ intent }) vs submit({ status }) defect (#19 / PR #25) that shipped
 * "because the bridge<->frontend seam was never exercised".
 *
 * The static type-check structurally cannot catch it. Calling the wrong branch —
 * submit() where submit({ status: "in-progress" }) was meant — is a valid call with
 * a valid signature, so tsc is satisfied while save-draft silently FINALIZES the
 * form instead. Only a behavioral assertion on the argument distinguishes them.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush, plain } from "./load-bridge.mjs";

const CANONICAL = "http://example.org/Questionnaire/q";

/** Bridge with a questionnaire already displayed, which requestSubmit requires. */
async function displayed() {
    const element = new FormFillerStub();
    const harness = await loadBridge([element]);
    deliver(harness.window, "sdc.displayQuestionnaire", { questionnaire: CANONICAL });
    await flush();
    assert.equal(element.questionnaire, CANONICAL, "precondition: questionnaire displayed");
    return { element, ...harness };
}

test("save-draft maps to submit({ status: 'in-progress' })", async () => {
    const { element, window } = await displayed();

    deliver(window, "ui.form.requestSubmit", { intent: "save-draft" });

    assert.equal(element.submitCalls.length, 1);
    assert.deepEqual(
        plain(element.submitCalls[0]),
        { status: "in-progress" },
        "save-draft must carry the in-progress status, or the form finalizes instead of saving a draft",
    );
});

test("finalize maps to a bare submit()", async () => {
    const { element, window } = await displayed();

    deliver(window, "ui.form.requestSubmit", { intent: "finalize" });

    assert.equal(element.submitCalls.length, 1);
    assert.equal(element.submitCalls[0], undefined, "finalize must pass no options");
});

test("a missing intent finalizes", async () => {
    const { element, window } = await displayed();

    deliver(window, "ui.form.requestSubmit", {});
    deliver(window, "ui.form.requestSubmit", undefined);

    assert.deepEqual(element.submitCalls, [undefined, undefined], "absent intent defaults to finalize");
});

test("an unrecognised intent finalizes rather than throwing", async () => {
    // Documents the else branch: only "save-draft" is special-cased, anything else
    // takes the finalize path. Pins the behaviour so a future intent isn't added to
    // the protocol while silently finalizing here.
    const { element, window } = await displayed();

    deliver(window, "ui.form.requestSubmit", { intent: "no-such-intent" });

    assert.deepEqual(element.submitCalls, [undefined]);
});

test("requestSubmit before a questionnaire is displayed is a no-op", async () => {
    const element = new FormFillerStub();
    const { window } = await loadBridge([element]);

    deliver(window, "ui.form.requestSubmit", { intent: "save-draft" });

    assert.deepEqual(
        element.submitCalls,
        [],
        "submitting an undisplayed form would submit an empty response",
    );
});
