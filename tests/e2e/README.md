# End-to-end tests (GH-26)

Two layers. Neither covers the seam alone, and both run real shipped bytes.

| Layer | Runs on | Exercises | Blind to |
|---|---|---|---|
| `browser/` | ubuntu | real `<tiro-form-filler>` + real bridge + real SDC server | WebView2 specifics, the .NET host |
| `WebView2Probe/` | windows | real harness binary in real WebView2 | form content (no server needed) |

They complete the ladder: `build/bridge-contract` checks types, `tests/bridge` drives a
transcribed stub, these drive the real thing. Both jobs **gate** — a red run blocks, because
an advisory red on a suite that catches silently-wrong clinical behaviour would just train
people to ignore it.

## `browser/` — layer 1

A static server stands in for the WebView2 virtual hosts (serving the page plus the
*staged* web-sdk bundle) and `host-shim.mjs` stands in for the .NET host's protocol side,
so `tiro-swm-bridge.js` runs **unmodified** — injected via `addInitScript`, which gives the
same before-any-page-script guarantee as `AddScriptToExecuteOnDocumentCreatedAsync`.

```sh
cd tests/e2e/browser
npm install --ignore-scripts
npx playwright install chromium-headless-shell
npm test
```

Stage the bundle first (`cd build/web-sdk && npm ci --ignore-scripts && node copy-bundle.mjs`),
or the SDK 404s. `SDC_ENDPOINT`, `QUESTIONNAIRE` and `ANSWER_LABEL` override the defaults.

### Validation replay

A green e2e that would also have passed against the original bug proves nothing, so the
suite is checked against the pre-fix bridge. `save-draft` on `94fbe8b` (which called
`submit({ intent })` instead of `submit({ status })`) returns **`completed`** — the form
finalizes, silently, with no console error — so the `in-progress` assertion fails, as it
must. To re-verify after changing the flow:

```sh
git show 94fbe8b:src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js > /tmp/prefix.js
# that bridge predates GH-60, so the page must load the SDK itself for this replay
BRIDGE_PATH=/tmp/prefix.js npm test    # the save-draft test must FAIL
```

## `WebView2Probe/` — layer 2

A minimal WinForms host that boots `TiroFormViewerR5` and asserts the handshake arrives.
That single assertion is load-bearing: it can only succeed if the second virtual host
served the ~6 MB embedded bundle, a plain `<script src>` passed the `DenyCors` mapping, the
element upgraded, and the bridge ran before page scripts — none of which layer 1 can prove.

Deliberately server-independent: all of that happens before a questionnaire is requested,
so the probe passes a canonical that resolves nowhere and ignores the resulting failure.
`WebSdkLoadException` (bundle missing or a page-owned copy colliding) and
`WebSdkVersionMismatchException` fail the probe explicitly.

Windows only, needs a WebView2 runtime and a desktop session — hence `windows-latest` in
`.github/workflows/e2e.yml`, which also logs the runtime version so a failure is
diagnosable.

## Which server these run against

**Staging** (`sdc-staging.tiro.health`), both layers, set in the workflow and as the
in-code default. Two reasons, and the second is easy to overlook:

- Staging runs *ahead* of production (it was on `v0.9.38-rc.0` while prod was `v0.9.37`), so
  a server regression surfaces here before customers meet it.
- Never production: these run nightly, and their `SdcClient` traffic carries the
  `Tiro.Health.FormSdk.Client/<version>` header (GH-63). Against prod, CI would show up as a
  phantom deployment in the very harness-version telemetry atticus-backend#3568 aggregates
  to decide what to support.

The `{MinimumSdcVersion, latest}` matrix below is what eventually replaces a shared server
with pinned ones on layer 1; staging stays the right target for the Windows job, which
cannot run Linux containers.

## Sharp edges these tests exposed

- **A submit requested before the form has rendered is silently dropped.** The bridge's
  `ui.form.requestSubmit` handler returns early when `formFiller.questionnaire` is unset —
  no error, no response, the host just never hears back. Compounding it, `SetContextAsync`
  returns on the page's *ack* of `sdc.displayQuestionnaire`, not on render, so a host that
  treats its completion as "the form is up" can submit into the void. The probe polls to
  work around it; an integrator whose Submit button is clicked early gets silence.
- **A saved draft used to end the session** (fixed here): any `form.submitted` advanced the
  viewer to `Submitted`, so the documented save-draft-then-keep-filling flow threw on the
  next send. Found by extending layer 2 to actually submit.

## Not done here

- The `{MinimumSdcVersion, latest}` server matrix. It belongs on layer 1, where the public
  image (`europe-docker.pkg.dev/tiroapp-4cb17/docker-ext/form-sdk-backend`) can be pinned
  per cell; layer 2 can't run Linux containers on a Windows runner. The server needs no
  database, so `uvicorn` is a fallback if pulling the image is awkward.
- Pinning the questionnaire. The default canonical is a live template, so an edit to it can
  turn this red; a fixture questionnaire in-repo would remove that coupling.
- Running these in atticus-backend CI too, which is the direction that catches a *server*
  change breaking the fielded bridge.
