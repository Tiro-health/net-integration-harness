/*
 * ui.form.configureTextShortcuts — the bridge forwards the host's triggers to
 * <tiro-form-filler> (textShortcuts / resolveTextShortcut) and carries each typed trigger to
 * the host and its answer back. Matching and replacement are the web-sdk's.
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { FormFillerStub } from "./form-filler-stub.mjs";
import { loadBridge, deliver, flush, plain } from "./load-bridge.mjs";

async function bridge(fillers = [new FormFillerStub()]) {
    const h = await loadBridge(fillers, { host: true });
    await flush();
    return { ...h, fillers };
}

test("the triggers reach every form filler, as abbreviations without content", async () => {
    const h = await bridge([new FormFillerStub(), new FormFillerStub()]);

    const ack = deliver(h.window, "ui.form.configureTextShortcuts", {
        abbreviations: ["µpat", "µnka", "", "two words", null],
    });

    assert.deepEqual(plain(ack), { count: 2 });
    for (const filler of h.fillers) {
        assert.deepEqual(plain(filler.textShortcuts), [{ abbreviation: "µpat" }, { abbreviation: "µnka" }]);
        assert.equal(typeof filler.resolveTextShortcut, "function");
    }
});

test("an empty list turns text shortcuts off", async () => {
    const h = await bridge();
    deliver(h.window, "ui.form.configureTextShortcuts", { abbreviations: ["µpat"] });

    deliver(h.window, "ui.form.configureTextShortcuts", { abbreviations: [] });

    assert.deepEqual(plain(h.fillers[0].textShortcuts), []);
});

test("a typed trigger is sent to the host, and its answer resolves the form's request", async () => {
    const h = await bridge();
    deliver(h.window, "ui.form.configureTextShortcuts", { abbreviations: ["µpat"] });

    const pending = h.fillers[0].resolveTextShortcut("µpat");
    const [request] = h.sent("ui.form.textShortcutRequested");
    assert.equal(request.payload.abbreviation, "µpat");

    const ack = deliver(h.window, "ui.form.resolveTextShortcut", {
        requestId: request.payload.requestId, text: "Leonardo", html: "<b>Leonardo</b>",
    });

    assert.deepEqual(plain(ack), { accepted: true });
    assert.deepEqual(plain(await pending), { text: "Leonardo", html: "<b>Leonardo</b>" });
});

test("an answer without text resolves to null: the abbreviation stays as typed", async () => {
    const h = await bridge();
    deliver(h.window, "ui.form.configureTextShortcuts", { abbreviations: ["µnak"] });

    const pending = h.fillers[0].resolveTextShortcut("µnak");
    const [request] = h.sent("ui.form.textShortcutRequested");
    deliver(h.window, "ui.form.resolveTextShortcut", { requestId: request.payload.requestId });

    assert.equal(await pending, null);
});

test("an answer to no pending request is not accepted", async () => {
    const h = await bridge();

    const ack = deliver(h.window, "ui.form.resolveTextShortcut", { requestId: "nope", text: "x" });

    assert.deepEqual(plain(ack), { accepted: false });
});

test("answers resolve their own request, in any order", async () => {
    const h = await bridge();
    deliver(h.window, "ui.form.configureTextShortcuts", { abbreviations: ["µpat", "µdob"] });

    const pat = h.fillers[0].resolveTextShortcut("µpat");
    const dob = h.fillers[0].resolveTextShortcut("µdob");
    const [patRequest, dobRequest] = h.sent("ui.form.textShortcutRequested");
    deliver(h.window, "ui.form.resolveTextShortcut", { requestId: dobRequest.payload.requestId, text: "15/04/1452" });
    deliver(h.window, "ui.form.resolveTextShortcut", { requestId: patRequest.payload.requestId, text: "Leonardo" });

    assert.equal((await pat).text, "Leonardo");
    assert.equal((await dob).text, "15/04/1452");
});
