/*
 * ui.form.configureTextShortcuts — typed abbreviations expanding into host snippets.
 * Covers matching, what triggers it, the replaced range and caret in text controls, what is
 * sent to a rich-text field, on-demand resolution, and that no content leaves the page.
 * Behaviour against a real Lexical editor: tests/e2e/browser/text-shortcuts.test.mjs.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush, plain } from "./load-bridge.mjs";

const fieldProto = {
    focus() {},
    setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; this.selections.push([start, end]); },
    dispatchEvent(event) { this.events.push(event.type); return true; },
    get value() { return this._value; },
    set value(v) { this._value = v; },
};

/** A text field with the caret at the end of `value`. */
function field({ tagName = "INPUT", type = "text", value = "" } = {}) {
    const el = Object.create(fieldProto);
    Object.assign(el, {
        nodeType: 1, tagName, isContentEditable: false, isConnected: true, readOnly: false, disabled: false,
        _value: value, selectionStart: value.length, selectionEnd: value.length,
        selections: [], events: [],
        getAttribute: name => (name === "type" ? type : null),
    });
    return el;
}

/** A contenteditable with the caret at the end of one text node. */
function contentEditable(text, { handlesBeforeInput = true } = {}) {
    const range = { setStart(node, offset) { this.node = node; this.start = offset; }, setEnd(node, offset) { this.end = offset; } };
    const textNode = { nodeType: 3, data: text, isConnected: true, ownerDocument: { createRange: () => range } };
    const selection = {
        rangeCount: 1, isCollapsed: true, focusNode: textNode, focusOffset: text.length, added: null,
        removeAllRanges() {}, addRange(r) { this.added = r; },
    };
    const el = {
        nodeType: 1, tagName: "DIV", isContentEditable: true, isConnected: true, events: [],
        contains: node => node === textNode,
        getRootNode: () => ({ getSelection: () => selection }),
        focus() {},
        // An editor that handles the beforeinput cancels it; dispatchEvent then returns false.
        dispatchEvent(event) { this.events.push(event); return !handlesBeforeInput; },
    };
    return { el, selection, range, textNode };
}

const SHORTCUTS = [
    { abbreviation: "µnka", text: "No known drug allergies." },
    { abbreviation: "µfu", text: "Follow-up in 6 weeks.", html: "<p>Follow-up in <b>6 weeks</b>.</p>" },
];

async function bridge(shortcuts = SHORTCUTS, opts) {
    const h = await loadBridge([new FormFillerStub()], opts);
    await flush();
    if (shortcuts) deliver(h.window, "ui.form.configureTextShortcuts", { shortcuts });
    return h;
}

/** The expansion runs one task after the space. */
const settle = () => new Promise(resolve => setTimeout(resolve, 5));

const NKA = "No known drug allergies.";

// ---- Text controls ---------------------------------------------------------------------

test("the abbreviation is replaced and the caret moves past the snippet", async () => {
    const h = await bridge();
    const input = field({ value: "Patient µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.deepEqual(input.selections, [[8, 12], [8 + NKA.length + 1, 8 + NKA.length + 1]],
        "the word only (the user's space stays), then the caret after the space");
    assert.deepEqual(h.execCommands.map(c => [c.name, c.value]), [["insertText", NKA]]);
});

test("the replacement lands in the value when execCommand refuses", async () => {
    const h = await bridge();
    const input = field({ value: "Patient µnka " });
    h.focus(input);
    h.failExecCommand();

    h.type(input);
    await settle();

    assert.equal(input.value, `Patient ${NKA} `);
});

test("only a whole word matches", async () => {
    const h = await bridge();
    const input = field({ value: "xµnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.equal(h.execCommands.length, 0);
});

test("matching is case-sensitive and exact", async () => {
    const h = await bridge();
    for (const value of ["µNKA ", "µnk ", "µnkab "]) {
        const input = field({ value });
        h.focus(input);
        h.type(input);
    }
    await settle();

    assert.equal(h.execCommands.length, 0);
});

test("an abbreviation at the start of the field matches", async () => {
    const h = await bridge();
    const input = field({ tagName: "TEXTAREA", value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.deepEqual(input.selections[0], [0, 4]);
    assert.equal(h.execCommands.length, 1);
});

test("only a typed space triggers it", async () => {
    const h = await bridge();
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input, "a");
    h.type(input, " ", { isComposing: true });
    h.type(input, " ", { inputType: "insertFromPaste" });
    await settle();

    assert.equal(h.execCommands.length, 0);
});

test("fields that don't take free text are left alone", async () => {
    const h = await bridge();
    const input = field({ type: "number", value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.equal(h.execCommands.length, 0);
});

test("nothing happens when the field lost focus before the expansion ran", async () => {
    const h = await bridge();
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input);
    h.blur(input, field());
    await settle();

    assert.equal(h.execCommands.length, 0);
});

test("without shortcuts, typing is untouched", async () => {
    const h = await bridge(null);
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.equal(h.execCommands.length, 0);
});

// ---- The list --------------------------------------------------------------------------

test("the list is acknowledged with its size, skipping unusable entries; later entries win", async () => {
    const h = await bridge(null);

    const ack = deliver(h.window, "ui.form.configureTextShortcuts", {
        shortcuts: [
            { abbreviation: "µa", text: "first" },
            { abbreviation: "µa", text: "second" },
            { abbreviation: "", text: "no abbreviation" },
            { abbreviation: "two words", text: "whitespace" },
            { abbreviation: "µb", text: "" },
            null,
        ],
        abbreviations: ["µr", "µa", "bad word", ""],
    });
    assert.deepEqual(plain(ack), { count: 2 }, "µa (preloaded) and µr (resolvable)");

    const input = field({ value: "µa " });
    h.focus(input);
    h.type(input);
    await settle();
    assert.equal(h.execCommands.at(-1).value, "second");
});

test("a new list replaces the old one, and an empty list turns expansion off", async () => {
    const h = await bridge();
    deliver(h.window, "ui.form.configureTextShortcuts", { shortcuts: [] });
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.equal(h.execCommands.length, 0);
});

// ---- Rich-text fields ------------------------------------------------------------------

test("a rich-text field gets the word selected and a beforeinput carrying the content", async () => {
    const h = await bridge();
    const { el, range, selection } = contentEditable("Plan: µfu ");
    h.focus(el);

    h.type(el);
    await settle();

    assert.equal(selection.added, range);
    assert.deepEqual([range.start, range.end], [6, 9], "the word only; the user's space stays");
    const [event] = el.events;
    assert.equal(event.type, "beforeinput");
    assert.equal(event.inputType, "insertReplacementText");
    assert.equal(event.dataTransfer.getData("text/html"), "<p>Follow-up in <b>6 weeks</b>.</p>");
    assert.equal(event.dataTransfer.getData("text/plain"), "Follow-up in 6 weeks.");
    assert.equal(h.execCommands.length, 0, "the editor inserted it");
    assert.equal(h.fired("tiro-text-shortcut-expanded")[0].detail.mode, "html");
});

test("a plain snippet carries no HTML", async () => {
    const h = await bridge();
    const { el } = contentEditable("µnka ");
    h.focus(el);

    h.type(el);
    await settle();

    assert.equal(el.events[0].dataTransfer.getData("text/html"), "");
    assert.equal(h.fired("tiro-text-shortcut-expanded")[0].detail.mode, "text");
});

test("an editor that ignores the beforeinput gets the plain text inserted", async () => {
    const h = await bridge();
    const { el } = contentEditable("µfu ", { handlesBeforeInput: false });
    h.focus(el);

    h.type(el);
    await settle();

    assert.deepEqual(h.execCommands.map(c => c.value), ["Follow-up in 6 weeks."]);
});

test("an invisible placeholder before the abbreviation counts as a boundary", async () => {
    // A rich editor may keep a zero-width space in an empty block.
    const h = await bridge();
    const { el, range } = contentEditable("​µnka ");
    h.focus(el);

    h.type(el);
    await settle();

    assert.deepEqual([range.start, range.end], [1, 5]);
});

test("the page is told an expansion happened, without its content", async () => {
    const h = await bridge();
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    const [event] = h.fired("tiro-text-shortcut-expanded");
    assert.equal(event.detail.mode, "text");
    assert.equal(event.detail.target, input);
    assert.ok(!("text" in event.detail) && !("abbreviation" in event.detail));
});

// ---- Resolved on demand ----------------------------------------------------------------

async function resolvingBridge() {
    const h = await bridge(null, { host: true });
    deliver(h.window, "ui.form.configureTextShortcuts", {
        shortcuts: [{ abbreviation: "µnka", text: NKA }],
        abbreviations: ["µpat", "µnka"],
    });
    return h;
}

/** Types the space after `value` and returns the request the page sent. */
async function request(h, input) {
    h.focus(input);
    h.type(input);
    await settle();
    return h.sent("ui.form.textShortcutRequested").at(-1)?.payload;
}

test("a resolvable abbreviation asks the host, and only for that word", async () => {
    const h = await resolvingBridge();

    const payload = await request(h, field({ value: "Patient µpat " }));
    assert.equal(plain(payload).abbreviation, "µpat");
    assert.ok(payload.requestId);
    assert.equal(h.execCommands.length, 0, "nothing replaced until the host answers");

    await request(h, field({ value: "Patient " }));
    assert.equal(h.sent("ui.form.textShortcutRequested").length, 1, "ordinary words never leave the page");
});

test("the host's answer replaces the abbreviation", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "Patient µpat " });
    const { requestId } = await request(h, input);

    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo da Vinci" });

    assert.deepEqual(plain(ack), { expanded: true });
    assert.deepEqual(input.selections[0], [8, 12]);
    assert.deepEqual(h.execCommands.map(c => c.value), ["Leonardo da Vinci"]);
});

test("a late answer still lands after the user typed on, and the caret follows", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µpat " });
    const { requestId } = await request(h, input);

    input.value = "µpat is old";
    input.selectionStart = input.selectionEnd = input.value.length;
    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo" });

    assert.deepEqual(plain(ack), { expanded: true });
    assert.deepEqual(input.selections.slice(-2), [[0, 4], [15, 15]], "the word, then the caret shifted by 8 - 4");
});

test("an answer for an abbreviation that was edited meanwhile is dropped", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µpat " });
    const { requestId } = await request(h, input);

    input.value = "µpa ";
    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo" });

    assert.deepEqual(plain(ack), { expanded: false });
    assert.equal(h.execCommands.length, 0);
});

test("an answer after the user left the field is dropped", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µpat " });
    const { requestId } = await request(h, input);

    h.blur(input, field());
    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo" });

    assert.deepEqual(plain(ack), { expanded: false });
});

test("no text in the answer, or an unknown request, changes nothing", async () => {
    const h = await resolvingBridge();
    const { requestId } = await request(h, field({ value: "µpat " }));

    assert.deepEqual(plain(deliver(h.window, "ui.form.resolveTextShortcut", { requestId: "nope", text: "x" })), { expanded: false });
    assert.deepEqual(plain(deliver(h.window, "ui.form.resolveTextShortcut", { requestId })), { expanded: false });
    assert.equal(h.execCommands.length, 0);
});

test("preloaded content wins over resolving", async () => {
    const h = await resolvingBridge();

    await request(h, field({ value: "µnka " }));

    assert.equal(h.sent("ui.form.textShortcutRequested").length, 0);
    assert.deepEqual(h.execCommands.map(c => c.value), [NKA]);
});
