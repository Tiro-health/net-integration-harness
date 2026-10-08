/*
 * Text shortcuts against a real Lexical editor (the editor the web-sdk uses for rich text),
 * typed with real keystrokes. The bridge's stub tests can't model Lexical's own copy of the
 * selection, which is where expansion broke in manual testing. Lexical is pinned in
 * package.json; no SDC server is needed.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { chromium } from "playwright";

const HERE = dirname(fileURLToPath(import.meta.url));
const BRIDGE = readFileSync(
  join(HERE, "../../../src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js"), "utf8");

const EDITOR_SOURCE = `
import { createEditor, $getRoot } from "lexical";
import { registerRichText } from "@lexical/rich-text";
import { registerHistory, createEmptyHistoryState } from "@lexical/history";
const el = document.getElementById("ed");
const editor = createEditor({ namespace: "e2e", onError: e => { throw e; } });
editor.setRootElement(el);
registerRichText(editor);
registerHistory(editor, createEmptyHistoryState(), 300);
window.editorText = () => editor.getEditorState().read(() => $getRoot().getTextContent());
`;

const wait = ms => new Promise(r => setTimeout(r, ms));
let browser, editorJs;

test.before(async () => {
  const out = await build({
    stdin: { contents: EDITOR_SOURCE, resolveDir: HERE }, bundle: true, format: "iife", write: false,
    define: { "process.env.NODE_ENV": '"development"' }, logLevel: "warning",
  });
  editorJs = out.outputFiles[0].text;
  browser = await chromium.launch();
});
test.after(() => browser?.close());

/** A page with the editor and the bridge, its shortcut requests captured instead of sent. */
async function open(config) {
  const page = await browser.newPage();
  // No SDK needed: the bridge installs text shortcuts whether or not the SDK loads.
  await page.addInitScript(() => { window.__tiroSdkUrl = "data:text/javascript,void 0"; });
  await page.addInitScript(BRIDGE);
  await page.route("https://e2e.test/**", route => route.fulfill({
    contentType: "text/html",
    body: `<!doctype html><meta charset="utf-8"><div id="ed" contenteditable="true"></div><script>${editorJs}</script>`,
  }));
  await page.goto("https://e2e.test/");
  await page.waitForFunction(() => window.SmartWebMessaging?.listeners["ui.form.configureTextShortcuts"]);
  await page.evaluate(config => {
    window.requests = [];
    window.SmartWebMessaging.sendEvent = (type, payload) => {
      if (type === "ui.form.textShortcutRequested") window.requests.push(payload);
    };
    window.SmartWebMessaging.listeners["ui.form.configureTextShortcuts"](config);
  }, config);
  await page.click("#ed");
  return {
    type: text => page.keyboard.type(text, { delay: 25 }),
    press: key => page.keyboard.press(key),
    text: () => page.evaluate(() => window.editorText()),
    html: () => page.evaluate(() => document.getElementById("ed").innerHTML),
    /** The host's answer to the i-th request. */
    answer: (i, text, html) => page.evaluate(([i, text, html]) =>
      window.SmartWebMessaging.listeners["ui.form.resolveTextShortcut"](
        { requestId: window.requests[i].requestId, text, html }), [i, text, html]),
    close: () => page.close(),
  };
}

test("preloaded: expands while the user types straight on", async () => {
  const p = await open({ shortcuts: [{ abbreviation: "µnka", text: "No known drug allergies." }] });
  await p.type("Patient µnka ok");
  await wait(100);
  assert.equal(await p.text(), "Patient No known drug allergies. ok");
  await p.close();
});

test("preloaded: twice in a row from an empty field, with HTML", async () => {
  const p = await open({ shortcuts: [{ abbreviation: "µconc", text: "Conclusion.", html: "<p><b>Conclusion.</b></p>" }] });
  await p.type("µconc µconc end");
  await wait(100);
  assert.equal(await p.text(), "Conclusion. Conclusion. end");
  await p.close();
});

test("resolved: a late answer lands behind the caret, keeping HTML and the caret", async () => {
  const p = await open({ abbreviations: ["µpat"] });
  await p.type("µpat is old");
  await p.answer(0, "Leonardo da Vinci", "<b>Leonardo da Vinci</b>");
  await p.type(" today");
  await wait(100);
  assert.equal(await p.text(), "Leonardo da Vinci is old today");
  assert.match(await p.html(), /<strong[^>]*>Leonardo da Vinci<\/strong>/);
  await p.close();
});

test("resolved: two late answers, out of order", async () => {
  const p = await open({ abbreviations: ["µpat", "µdob"] });
  await p.type("µpat born µdob in Vinci");
  await p.answer(1, "15/04/1452");
  await p.answer(0, "Leonardo");
  await p.type("!");
  await wait(100);
  assert.equal(await p.text(), "Leonardo born 15/04/1452 in Vinci!");
  await p.close();
});

test("resolved: an abbreviation edited before the answer is left alone", async () => {
  const p = await open({ abbreviations: ["µpat"] });
  await p.type("µpat x");
  await wait(50);
  // Delete the "t". Waits between keys: Lexical can drop rapid synthetic arrow keys.
  for (const key of ["ArrowLeft", "ArrowLeft", "Backspace"]) { await p.press(key); await wait(50); }
  assert.equal(await p.text(), "µpa x");
  const ack = await p.answer(0, "Leonardo");
  await wait(100);
  assert.equal(ack.expanded, false);
  assert.equal(await p.text(), "µpa x");
  await p.close();
});

test("one Ctrl+Z restores the abbreviation", async () => {
  const p = await open({ abbreviations: ["µpat"] });
  await p.type("µpat ");
  await wait(400); // a separate history entry from the typing
  await p.answer(0, "Leonardo");
  await wait(100);
  assert.equal(await p.text(), "Leonardo ");
  await p.press(process.platform === "darwin" ? "Meta+z" : "Control+z");
  await wait(100);
  assert.equal(await p.text(), "µpat ");
  await p.close();
});
