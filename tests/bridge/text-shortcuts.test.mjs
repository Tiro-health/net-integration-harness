/*
 * ui.form.configureTextShortcuts — typed abbreviations expanding into host snippets.
 * Covers the whole-word match, the replaced selection, what triggers it, and that no
 * content leaves the page.
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
function contentEditable(text, { consumesPaste = false } = {}) {
    const range = { setStart(node, offset) { this.node = node; this.start = offset; }, setEnd(node, offset) { this.end = offset; } };
    const textNode = { nodeType: 3, data: text, ownerDocument: { createRange: () => range } };
    const selection = {
        rangeCount: 1, isCollapsed: true, added: null,
        anchorNode: textNode, focusNode: textNode, anchorOffset: text.length, focusOffset: text.length,
        removeAllRanges() {},
        addRange(r) {
            this.added = r;
            Object.assign(this, { anchorNode: r.node, focusNode: r.node, anchorOffset: r.start, focusOffset: r.end, isCollapsed: false });
        },
    };
    const el = {
        nodeType: 1, tagName: "DIV", isContentEditable: true, isConnected: true, events: [],
        contains: node => node === textNode,
        getRootNode: () => ({ getSelection: () => selection }),
        focus() {},
        dispatchEvent(event) { this.events.push(event); return !consumesPaste; },
    };
    return { el, selection, range };
}

const SHORTCUTS = [
    { abbreviation: "µnka", text: "No known drug allergies." },
    { abbreviation: "µfu", text: "Follow-up in 6 weeks.", html: "<p>Follow-up in <b>6 weeks</b>.</p>" },
];

async function bridge(shortcuts = SHORTCUTS) {
    const h = await loadBridge([new FormFillerStub()]);
    await flush();
    if (shortcuts) deliver(h.window, "ui.form.configureTextShortcuts", { shortcuts });
    return h;
}

/** The expansion runs one task after the space. */
const settle = (ms = 5) => new Promise(resolve => setTimeout(resolve, ms));

/** Rich-text fields paste after the selection settles: on selectionchange, or after 50 ms. */
const settleRich = () => settle(80);

test("an abbreviation followed by a space is replaced, keeping the space", async () => {
    const h = await bridge();
    const input = field({ value: "Patient µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.deepEqual(input.selections.at(-1), [8, 13], "the abbreviation and the space are selected");
    assert.deepEqual(h.execCommands.map(c => [c.name, c.value]), [["insertText", "No known drug allergies. "]]);
});

test("the replacement lands in the value when execCommand refuses", async () => {
    const h = await bridge();
    const input = field({ value: "Patient µnka " });
    h.focus(input);
    h.failExecCommand();

    h.type(input);
    await settle();

    assert.equal(input.value, "Patient No known drug allergies. ");
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

    assert.deepEqual(input.selections.at(-1), [0, 5]);
    assert.equal(h.execCommands.length, 1);
});

test("an invisible placeholder before the abbreviation counts as a boundary", async () => {
    // A rich editor may keep a zero-width space in an empty block.
    const h = await bridge();
    const { el, range } = contentEditable("\u200Bµnka\u00a0");
    h.focus(el);

    h.type(el);
    await settleRich();

    assert.deepEqual([range.start, range.end], [1, 6], "the placeholder stays");
    assert.deepEqual(h.execCommands.map(c => c.value), ["No known drug allergies. "]);
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
    });
    assert.deepEqual(plain(ack), { count: 1 });

    const input = field({ value: "µa " });
    h.focus(input);
    h.type(input);
    await settle();
    assert.equal(h.execCommands.at(-1).value, "second ");
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

test("in a rich-text field the HTML goes in as a paste, then the space", async () => {
    const h = await bridge();
    const { el, selection, range } = contentEditable("Plan: µfu ", { consumesPaste: true });
    h.focus(el);

    h.type(el);
    await settleRich();

    assert.equal(selection.added, range);
    assert.deepEqual([range.start, range.end], [6, 10], "the abbreviation and the stored space");
    const paste = el.events.find(e => e.type === "paste");
    assert.equal(paste.clipboardData.getData("text/html"), "<p>Follow-up in <b>6 weeks</b>.</p>");
    assert.deepEqual(h.execCommands.map(c => c.value), [" "]);
});

test("in a rich-text field a plain snippet also goes in as a paste", async () => {
    const h = await bridge();
    const { el, range } = contentEditable("µnka\u00a0", { consumesPaste: true });
    h.focus(el);

    h.type(el);
    await settleRich();

    assert.deepEqual([range.start, range.end], [0, 5]);
    const paste = el.events.find(e => e.type === "paste");
    assert.equal(paste.clipboardData.getData("text/plain"), "No known drug allergies.");
    assert.equal(paste.clipboardData.getData("text/html"), "", "no HTML for a plain snippet");
    assert.deepEqual(h.execCommands.map(c => c.value), [" "]);
    assert.equal(h.fired("tiro-text-shortcut-expanded")[0].detail.mode, "text");
});

test("the space after a pasted snippet waits for the editor, and doesn't re-trigger", async () => {
    const h = await bridge();
    const { el } = contentEditable("µconc\u00a0", { consumesPaste: true });
    deliver(h.window, "ui.form.configureTextShortcuts", {
        shortcuts: [{ abbreviation: "µconc", text: "See µconc", html: "<p>See µconc</p>" }],
    });
    // Our own space must not expand the abbreviation the snippet ends with.
    h.document.execCommand = (name, ui, value) => {
        h.execCommands.push({ name, value });
        h.type(el, value);
        return true;
    };
    h.focus(el);

    h.type(el);
    assert.equal(h.execCommands.length, 0, "nothing yet: the expansion is deferred");
    await settleRich();
    await settleRich();

    assert.deepEqual(h.execCommands.map(c => c.value), [" "], "one space, no second expansion");
});

test("a rich-text field that declines the paste gets the plain text", async () => {
    const h = await bridge();
    const { el } = contentEditable("µfu ");
    h.focus(el);

    h.type(el);
    await settleRich();

    assert.deepEqual(h.execCommands.map(c => c.value), ["Follow-up in 6 weeks. "]);
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

// ---- Resolved on demand: abbreviations without content -----------------------------------

async function resolvingBridge() {
    const h = await loadBridge([new FormFillerStub()], { host: true });
    await flush();
    deliver(h.window, "ui.form.configureTextShortcuts", {
        shortcuts: [{ abbreviation: "µnka", text: "No known drug allergies." }],
        abbreviations: ["µpat", "µnka"],
    });
    return h;
}

test("a resolvable abbreviation asks the host, and only for that word", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "Patient µpat " });
    h.focus(input);

    h.type(input);
    await settle();

    const [request] = h.sent("ui.form.textShortcutRequested");
    assert.equal(plain(request.payload).abbreviation, "µpat");
    assert.ok(request.payload.requestId);
    assert.equal(h.execCommands.length, 0, "nothing replaced until the host answers");

    const other = field({ value: "Patient " });
    h.focus(other);
    h.type(other);
    await settle();
    assert.equal(h.sent("ui.form.textShortcutRequested").length, 1, "ordinary words never leave the page");
});

test("the host's answer replaces the abbreviation", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "Patient µpat " });
    h.focus(input);
    h.type(input);
    await settle();
    const { requestId } = h.sent("ui.form.textShortcutRequested")[0].payload;

    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo da Vinci" });

    assert.deepEqual(plain(ack), { expanded: true });
    assert.deepEqual(input.selections.at(-1), [8, 13]);
    assert.deepEqual(h.execCommands.map(c => c.value), ["Leonardo da Vinci "]);
});

test("an answer that arrives after the user kept typing is dropped", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µpat " });
    h.focus(input);
    h.type(input);
    await settle();
    const { requestId } = h.sent("ui.form.textShortcutRequested")[0].payload;

    input.value = "µpat more";
    input.selectionStart = input.selectionEnd = input.value.length;
    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo da Vinci" });

    assert.deepEqual(plain(ack), { expanded: false });
    assert.equal(h.execCommands.length, 0);
});

test("no text in the answer, or an unknown request, changes nothing", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µpat " });
    h.focus(input);
    h.type(input);
    await settle();
    const { requestId } = h.sent("ui.form.textShortcutRequested")[0].payload;

    assert.deepEqual(plain(deliver(h.window, "ui.form.resolveTextShortcut", { requestId: "nope", text: "x" })), { expanded: false });
    assert.deepEqual(plain(deliver(h.window, "ui.form.resolveTextShortcut", { requestId })), { expanded: false });
    assert.equal(h.execCommands.length, 0);
});

test("preloaded content wins over resolving", async () => {
    const h = await resolvingBridge();
    const input = field({ value: "µnka " });
    h.focus(input);

    h.type(input);
    await settle();

    assert.equal(h.sent("ui.form.textShortcutRequested").length, 0);
    assert.deepEqual(h.execCommands.map(c => c.value), ["No known drug allergies. "]);
});

test("a resolved snippet in a rich-text field goes in as a paste", async () => {
    const h = await resolvingBridge();
    const { el, range } = contentEditable("µpat ", { consumesPaste: true });
    h.focus(el);
    h.type(el);
    await settleRich();
    const { requestId } = h.sent("ui.form.textShortcutRequested")[0].payload;

    deliver(h.window, "ui.form.resolveTextShortcut", { requestId, text: "Leonardo", html: "<b>Leonardo</b>" });
    await settleRich();

    assert.deepEqual([range.start, range.end], [0, 5]);
    assert.equal(el.events.find(e => e.type === "paste").clipboardData.getData("text/html"), "<b>Leonardo</b>");
    assert.deepEqual(h.execCommands.map(c => c.value), [" "]);
});

test("in a rich-text field the paste waits until the editor has seen the new selection", async () => {
    const h = await bridge();
    const { el } = contentEditable("µnka\u00a0", { consumesPaste: true });
    h.focus(el);
    h.type(el);
    await settle();

    assert.equal(el.events.filter(e => e.type === "paste").length, 0, "not before selectionchange");
    h.selectionChange();
    await settle();

    assert.equal(el.events.filter(e => e.type === "paste").length, 1);
});

test("no paste when the selection moved before it settled", async () => {
    const h = await bridge();
    const { el, selection } = contentEditable("µnka\u00a0", { consumesPaste: true });
    h.focus(el);
    h.type(el);
    await settle();

    Object.assign(selection, { anchorOffset: 5, focusOffset: 5, isCollapsed: true });
    h.selectionChange();
    await settle();

    assert.equal(el.events.filter(e => e.type === "paste").length, 0);
    assert.equal(h.execCommands.length, 0);
});
