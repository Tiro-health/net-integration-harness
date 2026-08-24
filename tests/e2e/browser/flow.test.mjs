/*
 * GH-26 layer 1: the real <tiro-form-filler> driven by the real bridge against a real
 * SDC server, in a real browser.
 *
 * What this covers that nothing else does: tests/bridge drives a hand-transcribed stub,
 * and build/bridge-contract only type-checks. Here the bytes we ship (the staged
 * web-sdk bundle + tiro-swm-bridge.js) run against a live server, so save-draft actually
 * has to come back in-progress rather than merely calling a method named submit.
 *
 * The static server stands in for the WebView2 virtual hosts and host-shim.mjs for the
 * .NET host's protocol side, so the bridge runs unmodified.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync, existsSync } from "node:fs";
import { join, extname, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import { HOST_SHIM } from "./host-shim.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, "..", "..", "..");
const BRIDGE_PATH = process.env.BRIDGE_PATH
  ?? join(REPO, "src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js");
const BUNDLE_PATH = join(REPO, "src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-web-sdk.iife.js");
// Staging, never the production demo instance: see tests/e2e/README.md.
const SDC_ENDPOINT = process.env.SDC_ENDPOINT ?? "https://sdc-staging.tiro.health/fhir/r5";
// Version-pinned: the same canonical also carries a mutable draft-1, and an edit to that
// draft must not be able to change what this test renders. The pin matters more than it
// looks — staging's Questionnaire search ignores the version parameter (a bogus version
// still returns every revision, draft first), so it is the SDK that has to honour it. That
// it does is asserted below, not assumed.
const DEFAULT_QUESTIONNAIRE =
  "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699|1.0.0";
const QUESTIONNAIRE = process.env.QUESTIONNAIRE ?? DEFAULT_QUESTIONNAIRE;
// A chip label in the default questionnaire (CHA2DS2-VASc, in Dutch: the middle age band).
// The dash is U+2013 EN DASH, as authored in the template — a hyphen will not match.
// Override together with QUESTIONNAIRE.
const ANSWER_LABEL = process.env.ANSWER_LABEL ?? "65–74";
// Everything past the handshake needs a live SDC server. PR runs skip those tests so a
// staging outage cannot redden an unrelated pull request, while the nightly and manual runs
// exercise the whole flow. See .github/workflows/e2e.yml.
// The pinned revision and the draft behind the same canonical happen to use disjoint
// linkIds, which is what makes "which revision actually rendered" observable from the QR
// alone. Only meaningful for the default template.
const PINNED_LINKID_PREFIX = QUESTIONNAIRE === DEFAULT_QUESTIONNAIRE ? "019cf82c-" : null;
const SERVER_TESTS = process.env.E2E_SERVER_TESTS !== "0";
const NEEDS_SERVER = { skip: SERVER_TESTS ? false : "needs a live SDC server" };

const MIME = { ".html": "text/html", ".js": "application/javascript" };

function startServer() {
  return new Promise(resolve => {
    const server = createServer((req, res) => {
      const path = req.url.split("?")[0];
      const file = path === "/tiro-web-sdk.iife.js"
        ? BUNDLE_PATH
        : join(HERE, "public", path === "/" ? "index.html" : path);
      if (!existsSync(file)) { res.writeHead(404).end(); return; }
      res.writeHead(200, { "content-type": MIME[extname(file)] ?? "application/octet-stream" });
      res.end(readFileSync(file));
    });
    server.listen(0, "127.0.0.1", () => resolve({ server, base: `http://127.0.0.1:${server.address().port}` }));
  });
}

/** Boots a page with the host shim + real bridge installed before any page script. */
async function launch() {
  const { server, base } = await startServer();
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const consoleErrors = [];
  page.on("console", m => { if (m.type() === "error") consoleErrors.push(m.text()); });
  page.on("pageerror", e => consoleErrors.push(String(e)));

  // window.__tiroSdkUrl is how the real host tells the bridge where the bundle lives; the
  // bridge has no fallback URL of its own (GH-71) and refuses to load without it. Set here,
  // pointing at the stand-in host, exactly as TiroFormViewer sets it before injecting.
  await page.addInitScript(`window.__tiroSdkUrl = ${JSON.stringify(base + "/tiro-web-sdk.iife.js")};`);
  await page.addInitScript(HOST_SHIM);
  await page.addInitScript({ content: readFileSync(BRIDGE_PATH, "utf8") });
  await page.goto(base, { waitUntil: "load" });
  await page.waitForFunction("window.__host?.handshakes.length > 0", { timeout: 60000 });

  return {
    page,
    consoleErrors,
    async displayQuestionnaire() {
      await page.evaluate(e => window.__hostSend("sdc.configure", { configuration: { sdcServer: e } }), SDC_ENDPOINT);
      await page.evaluate(q => window.__hostSend("sdc.displayQuestionnaire", {
        questionnaire: q,
        context: {
          launchContext: [
            { name: "patient", contentResource: { resourceType: "Patient", id: "pat-1", gender: "female" } },
          ],
        },
      }), QUESTIONNAIRE);
      await page.waitForFunction(() => {
        const root = document.querySelector("tiro-form-filler")?.shadowRoot;
        return !!root && root.querySelectorAll("input, button, textarea, [role=button]").length > 0;
      }, { timeout: 120000 });
    },
    /**
     * Clicks an answer chip by its label. Playwright's CSS selectors pierce the element's
     * open shadow root, so no manual shadowRoot traversal is needed — but the underlying
     * <input> is hidden behind a styled button, so the button is what must be clicked.
     */
    async chooseAnswer(label) {
      await page.locator("tiro-form-filler button", { hasText: label }).first().click();
    },
    dirtyChanges() {
      return page.evaluate("window.__host.dirtyChanges");
    },
    async requestSubmit(intent) {
      const before = await page.evaluate("window.__host.submitted.length");
      await page.evaluate(i => window.__hostSend("ui.form.requestSubmit", i ? { intent: i } : {}), intent);
      await page.waitForFunction(n => window.__host.submitted.length > n, before, { timeout: 120000 });
      return page.evaluate(n => window.__host.submitted[n], before);
    },
    async close() { await browser.close(); server.close(); },
  };
}

test("the bridge injects the embedded SDK and reports its identity at handshake", async () => {
  const h = await launch();
  try {
    const [handshake] = await h.page.evaluate("window.__host.handshakes");
    assert.equal(handshake.client.name, "tiro-web-sdk");
    // "embedded" proves the bundle came from the host, not a page-owned script tag.
    assert.equal(handshake.client.source, "embedded");
    // null until the pin reaches an SDK exposing a static version (atticus-frontend#2927).
    assert.ok(handshake.client.version === null || typeof handshake.client.version === "string");
    assert.ok(await h.page.evaluate("!!customElements.get('tiro-form-filler')"));
  } finally {
    await h.close();
  }
});

/** Every answered item in a QR, depth-first. */
function answeredItems(item = []) {
  return item.flatMap(i => [
    ...(i.answer ? [{ linkId: i.linkId, answer: i.answer }] : []),
    ...answeredItems(i.item ?? []),
  ]);
}

test("a user's answer reaches the host, dirties the form, and drives calculation", NEEDS_SERVER, async () => {
  const h = await launch();
  try {
    await h.displayQuestionnaire();
    assert.deepEqual(await h.dirtyChanges(), [], "a populated form is not dirty until edited");

    // A real click on a real widget — the one interaction no other test performs.
    await h.chooseAnswer(ANSWER_LABEL);
    await h.page.waitForFunction("window.__host.dirtyChanges.length > 0", { timeout: 60000 });

    // GH-46: tiro-dirty-change -> ui.form.dirtyChanged -> host, from a real edit.
    assert.deepEqual(await h.dirtyChanges(), [{ isDirty: true }]);

    const submitted = await h.requestSubmit(undefined);
    const answers = answeredItems(submitted.response.item);

    // The version pin was honoured, not merely requested: these linkIds exist only in
    // 1.0.0. Without this, a server that quietly served draft-1 would still pass every
    // assertion below, and the suite would be testing a template someone can edit.
    if (PINNED_LINKID_PREFIX) {
      assert.ok(
        answers.length > 0 && answers.every(a => a.linkId.startsWith(PINNED_LINKID_PREFIX)),
        `answers came from another revision than the pinned one: ${JSON.stringify(answers.map(a => a.linkId))}`);
    }

    // The chosen coding survived the round trip to the host...
    assert.ok(
      answers.some(a => a.answer.some(v => v.valueCoding?.display === ANSWER_LABEL)),
      `no answer carrying "${ANSWER_LABEL}": ${JSON.stringify(answers).slice(0, 300)}`);
    // ...and the questionnaire's calculatedExpression ran off it, producing a score.
    // Asserted as "some positive number" rather than an exact value, since the template
    // is live (see README).
    assert.ok(
      answers.some(a => a.answer.some(v => typeof v.valueDecimal === "number" && v.valueDecimal > 0)),
      `no calculated numeric answer: ${JSON.stringify(answers).slice(0, 300)}`);
  } finally {
    await h.close();
  }
});

test("save-draft saves a draft and finalize completes — against the live element", NEEDS_SERVER, async () => {
  const h = await launch();
  try {
    await h.displayQuestionnaire();

    const draft = await h.requestSubmit("save-draft");
    // The QR echoes the canonical it was filled from, so this is where a dropped version
    // pin would show up — the difference between rendering 1.0.0 and rendering whatever
    // draft-1 happens to say today.
    assert.equal(draft.response.questionnaire, QUESTIONNAIRE);
    // The bug this whole workstream exists for: an SDK that ignores the option finalizes
    // here instead, silently. Verified against the pre-fix bridge, which returns
    // "completed" for this call.
    assert.equal(draft.response.status, "in-progress");

    const finalized = await h.requestSubmit(undefined);
    assert.equal(finalized.response.status, "completed");
    assert.deepEqual(h.consoleErrors, []);
  } finally {
    await h.close();
  }
});
