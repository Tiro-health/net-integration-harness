/*
 * ui.form.insertContent — host-driven typing into the focused field.
 *
 * The host owns UI the page can't see (a labelled snippet menu in the EHR shell), and
 * this message is how that UI gets text into the form. It deliberately writes NO
 * QuestionnaireResponse: the text goes in at the caret through document.execCommand
 * ("insertText"), the one insertion Chromium routes through beforeinput/input as if it
 * had been typed — which is what makes a React-controlled field keep it. Assigning
 * .value instead looks right until the next render reverts it, with the answer never
 * reaching the response. That distinction is invisible to a type-check and is the bug
 * class these tests exist for.
 *
 * The other half is focus. The host-side click that triggers an insert takes OS focus
 * out of the WebView2, so by the time the message lands document.activeElement may be
 * nothing at all — hence the tracked last-focused field, exercised below.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush } from "./load-bridge.mjs";

/**
 * A text field, modelled the way the bridge inspects one: nodeType/tagName, the type
 * attribute, and the readOnly/disabled/isConnected flags. `value` lives behind a
 * prototype accessor because the execCommand fallback writes through the PROTOTYPE's
 * setter (the React-instance-setter workaround), so an own data property would let a
 * broken fallback pass.
 */
const fieldProto = {
    focus() { this.focusCalls++; },
    setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; },
    dispatchEvent(event) { this.events.push(event.type); return true; },
    get value() { return this._value; },
    set value(v) { this._value = v; },
};

function field({ tagName = "INPUT", type = "text", value = "", readOnly = false, disabled = false } = {}) {
    const el = Object.create(fieldProto);
    Object.assign(el, {
        nodeType: 1,
        tagName,
        isContentEditable: false,
        isConnected: true,
        readOnly,
        disabled,
        _value: value,
        selectionStart: value.length,
        selectionEnd: value.length,
        focusCalls: 0,
        events: [],
        getAttribute: name => (name === "type" ? type : null),
    });
    return el;
}

/** A contenteditable, which the SDK's rich-text answers render as. */
function contentEditable() {
    const el = Object.create(fieldProto);
    Object.assign(el, {
        nodeType: 1, tagName: "DIV", isContentEditable: true, isConnected: true,
        focusCalls: 0, events: [],
    });
    return el;
}

async function bridge(opts) {
    const element = new FormFillerStub();
    const harness = await loadBridge([element], opts);
    await flush();
    return { element, ...harness };
}

test("text is inserted at the caret of the focused field", async () => {
    const h = await bridge();
    const input = field();
    h.focus(input);

    const result = deliver(h.window, "ui.form.insertContent", { text: "no acute distress" });

    assert.deepEqual(h.execCommands, [
        { name: "insertText", showUi: false, value: "no acute distress" },
    ], "insertion must go through execCommand, not through .value");
    assert.equal(result.inserted, true);
    assert.equal(result.mode, "text");
    assert.equal(input.focusCalls, 0, "the already-focused field must not be re-focused");
});

test("insertion falls back to the last-focused field once focus has left the page", async () => {
    // The scenario every host-side snippet menu produces: clicking the menu moves OS
    // focus out of the WebView2, so activeElement is gone by the time the message lands.
    const h = await bridge();
    const input = field();
    h.focus(input);
    h.blur(input, null);

    const result = deliver(h.window, "ui.form.insertContent", { text: "afebrile" });

    assert.equal(result.inserted, true);
    assert.equal(result.mode, "text");
    assert.equal(input.focusCalls, 1, "the field must be re-focused before inserting");
    assert.equal(h.execCommands.length, 1);
});

test("a field inside the form-filler's shadow root is found", async () => {
    // The SDK renders its fields in a shadow tree, where document.activeElement stops at
    // the host element. Without the descent the host element would be the target and no
    // insert would happen at all.
    const h = await bridge();
    const input = field();
    const shadowHost = { nodeType: 1, tagName: "TIRO-FORM-FILLER", shadowRoot: { activeElement: input } };
    h.document.activeElement = shadowHost;

    const result = deliver(h.window, "ui.form.insertContent", { text: "10 mm" });

    assert.equal(result.inserted, true);
    assert.equal(result.mode, "text");
    assert.equal(h.execCommands[0].value, "10 mm");
});

test("a contenteditable answer is a valid target", async () => {
    const h = await bridge();
    const editor = contentEditable();
    h.focus(editor);

    assert.equal(deliver(h.window, "ui.form.insertContent", { text: "dictated" }).inserted, true);
    assert.equal(h.execCommands.length, 1);
});

test("with nothing focused the insert is refused, not guessed at", async () => {
    const h = await bridge();

    const result = deliver(h.window, "ui.form.insertContent", { text: "conclusion" });

    assert.equal(result.inserted, false, "the host needs this to say 'click a field first'");
    assert.deepEqual(h.execCommands, []);
    assert.ok(
        h.warnings.some(w => w.includes("no focused text field")),
        "a refused insert must leave a trace in the console",
    );
});

test("fields that don't take typed text are not targets", async () => {
    // insertText into a checkbox or a date picker either no-ops or produces a value the
    // field's grammar rejects — and a password field is none of a host menu's business.
    for (const el of [
        field({ type: "checkbox" }),
        field({ type: "date" }),
        field({ type: "password" }),
        field({ readOnly: true }),
        field({ disabled: true }),
        field({ tagName: "TEXTAREA", readOnly: true }),
    ]) {
        const h = await bridge();
        h.focus(el);

        assert.equal(
            deliver(h.window, "ui.form.insertContent", { text: "x" }).inserted, false,
            `${el.tagName}/${el.getAttribute("type")} must be refused`,
        );
        assert.deepEqual(h.execCommands, []);
    }
});

test("a textarea is a target", async () => {
    const h = await bridge();
    h.focus(field({ tagName: "TEXTAREA" }));

    assert.equal(deliver(h.window, "ui.form.insertContent", { text: "x" }).inserted, true);
});

test("an empty payload is refused without touching the field", async () => {
    const h = await bridge();
    h.focus(field());

    assert.equal(deliver(h.window, "ui.form.insertContent", {}).inserted, false);
    assert.equal(deliver(h.window, "ui.form.insertContent", { text: "" }).inserted, false);
    assert.equal(deliver(h.window, "ui.form.insertContent", undefined).inserted, false);
    assert.deepEqual(h.execCommands, []);
});

test("when execCommand refuses, the value is spliced through the prototype setter", async () => {
    // Last resort. It must still splice at the selection and fire a bubbling `input`
    // event: React only notices a value written through the prototype's setter, and only
    // re-reads the field on an input event.
    const h = await bridge();
    h.failExecCommand();
    const input = field({ value: "BP 120/80" });
    input.selectionStart = 3;
    input.selectionEnd = 3;
    h.focus(input);

    const result = deliver(h.window, "ui.form.insertContent", { text: "at rest " });

    assert.equal(result.inserted, true);
    assert.equal(result.mode, "text");
    assert.equal(input.value, "BP at rest 120/80", "the text must land at the caret, not at the end");
    assert.deepEqual(input.events, ["input"], "React re-reads the field on input, and only then");
    assert.equal(input.selectionStart, 11, "the caret must follow the inserted text");
});

test("the outcome rides back to the host on the ack", async () => {
    // The .NET side reads `inserted` off the response payload's extension fields, so it
    // can prompt the user instead of failing silently. A plain object returned by a
    // handler is merged into the ack — the mechanism this depends on.
    const h = await bridge({ host: true });
    h.focus(field());

    h.receive({ messageId: "m1", messageType: "ui.form.insertContent", payload: { text: "hi" } });
    await flush();

    const ack = h.responses().find(m => m.responseToMessageId === "m1");
    assert.ok(ack, "the host awaits this ack");
    assert.equal(ack.payload.$type, "base", "the discriminator must survive the merge");
    assert.equal(ack.payload.inserted, true);
});

test("a refused insert still acks, rather than erroring", async () => {
    // Nothing failed: the clinician simply hasn't clicked into a field. An error ack
    // would raise PageError on the host and land in Sentry for a normal situation.
    const h = await bridge({ host: true });

    h.receive({ messageId: "m1", messageType: "ui.form.insertContent", payload: { text: "hi" } });
    await flush();

    const ack = h.responses().find(m => m.responseToMessageId === "m1");
    assert.equal(ack.payload.$type, "base");
    assert.equal(ack.payload.inserted, false);
});

test("the page is told what happened", async () => {
    const h = await bridge();
    h.focus(field());

    deliver(h.window, "ui.form.insertContent", { text: "note" });

    const fired = h.fired("tiro-content-inserted");
    assert.equal(fired.length, 1, "pages drive status UI off this hook");
    assert.equal(fired[0].detail.text, "note");
    assert.equal(fired[0].detail.inserted, true);
});

/*
 * The rich path. `html` on the payload is offered to the field as a synthesized paste, which is
 * how the SDK's rich-text answers accept formatted content — they own a paste pipeline that
 * reads text/html. The bridge cannot see inside that pipeline, so it reads dispatchEvent's
 * return value: an editor that handles a paste calls preventDefault, which reports false.
 *
 * That is the whole contract, and the reason `mode` exists. A field that ignores the paste must
 * fall back to plain text rather than silently dropping the content, and the host has to be
 * able to tell the two apart — "formatted" from "plain, because this field can't hold more".
 */

/** A field whose paste handler consumes the event, as a rich-text editor does. */
function richField() {
    const el = field();
    el.dispatchEvent = function (event) {
        this.events.push(event.type);
        if (event.type === "paste") {
            this.pastedHtml = event.clipboardData.getData("text/html");
            this.pastedText = event.clipboardData.getData("text/plain");
            return false;   // i.e. preventDefault() was called
        }
        return true;
    };
    return el;
}

test("html is offered as a paste, and reported as mode 'html' when the field takes it", async () => {
    const h = await bridge();
    const el = richField();
    h.focus(el);

    const result = deliver(h.window, "ui.form.insertContent", {
        text: "Assessment. No further imaging indicated.",
        html: "<p><b>Assessment.</b> No further imaging <i>indicated</i>.</p>",
    });

    assert.equal(result.inserted, true);
    assert.equal(result.mode, "html", "the formatting survived, and the host is told so");
    assert.equal(el.pastedHtml, "<p><b>Assessment.</b> No further imaging <i>indicated</i>.</p>");
    assert.equal(el.pastedText, "Assessment. No further imaging indicated.",
        "the plain rendition rides along, for an editor that prefers text");
    assert.deepEqual(h.execCommands, [], "a consumed paste must not also insert the text");
});

test("a field that ignores the paste falls back to plain text", async () => {
    // A plain <input>, or a rich editor that isn't listening. Dropping the content because the
    // preferred representation wasn't accepted would be the worst outcome of the three.
    const h = await bridge();
    const el = field();   // its dispatchEvent returns true — nothing cancelled
    h.focus(el);

    const result = deliver(h.window, "ui.form.insertContent", {
        text: "Assessment. No further imaging indicated.",
        html: "<p><b>Assessment.</b></p>",
    });

    assert.equal(result.inserted, true);
    assert.equal(result.mode, "text", "the host must be able to tell this from a rich insert");
    assert.deepEqual(h.execCommands, [
        { name: "insertText", showUi: false, value: "Assessment. No further imaging indicated." },
    ]);
});

test("no html means the plain path, without attempting a paste", async () => {
    const h = await bridge();
    const el = richField();
    h.focus(el);

    const result = deliver(h.window, "ui.form.insertContent", { text: "afebrile" });

    assert.equal(result.mode, "text");
    assert.ok(!el.events.includes("paste"), "nothing to paste, so no paste should be attempted");
    assert.equal(h.execCommands.length, 1);
});

test("with nothing focused, neither path runs", async () => {
    const h = await bridge();

    const result = deliver(h.window, "ui.form.insertContent", {
        text: "conclusion", html: "<p>conclusion</p>",
    });

    assert.deepEqual({ inserted: result.inserted, mode: result.mode },
        { inserted: false, mode: "none" });
    assert.deepEqual(h.execCommands, []);
});
