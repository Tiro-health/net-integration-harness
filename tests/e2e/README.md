# End-to-end tests (GH-26)

Two layers. Neither covers the seam alone, and both run real shipped bytes.

| Layer | Runs on | Exercises | Blind to |
|---|---|---|---|
| `browser/` | ubuntu | real `<tiro-form-filler>` + real bridge + real SDC server | WebView2 specifics, the .NET host |
| `WebView2Probe/` | windows | real harness binary in real WebView2, typed FHIR round trip | real user input (no clicks) |

They complete the ladder: `build/bridge-contract` checks types, `tests/bridge` drives a
transcribed stub, these drive the real thing. Both jobs **gate when triggered** — a red run
blocks, because an advisory red on a suite that catches silently-wrong clinical behaviour would
just train people to ignore it. "When triggered" is load-bearing: the workflow has a `paths`
filter, so a pull request touching none of those paths never runs it.

The Windows gate requires the probe's *terminal* marker (`PASS: all stages`, or `PASS: stage A`
when server stages are deliberately skipped). A bare `PASS` grep also matched `PASS: stage A`, so
a run whose server stages were skipped for any reason passed the gate having asserted nothing
about save-draft, finalize or `$extract`.

What a *pull request* gates on is only the part that needs no server: the bundle, the
injection order, the handshake. The stages that talk to a live SDC server run nightly and on
demand (`E2E_SERVER_TESTS=0` / `PROBE_SKIP_SERVER_STAGES=1` on PRs), because a staging outage
must not redden a pull request that cannot have caused it — a suite that goes red for reasons
outside the author's control gets ignored just as fast as an advisory one.

## `browser/` — layer 1

A static server stands in for the WebView2 virtual hosts (serving the page plus the
*staged* web-sdk bundle) and `host-shim.mjs` stands in for the .NET host's protocol side,
so `tiro-swm-bridge.js` runs **unmodified** — injected via `addInitScript`, which gives the
same before-any-page-script guarantee as `AddScriptToExecuteOnDocumentCreatedAsync`.

The page served is the **starter template extracted from the shipped `WebAssets/index.html`** —
the markup its "Copy starter template" button hands integrators — not a copy kept here. A copy
drifts: someone improves the template people actually paste, and the suite keeps testing the old
one. Extracting means an edit reaches the suite by construction, and the extraction fails loudly
if the shipped page changes shape. Layer 2 navigates to the shipped page itself, so between them
both pages are covered.

```sh
cd tests/e2e/browser
npm ci --ignore-scripts
npx playwright install chromium-headless-shell
npm test
```

`package-lock.json` is committed and Playwright is pinned exactly: the version decides which
browser build runs, so a floating range would change what the suite tests without a review.

Stage the bundle first (`cd build/web-sdk && npm ci --ignore-scripts && node copy-bundle.mjs`),
or the SDK 404s. `SDC_ENDPOINT`, `QUESTIONNAIRE`, `ANSWER_LABEL` and `E2E_SERVER_TESTS`
override the defaults.

The default canonical is **version-pinned** (`|1.0.0`), which matters more than it looks: the
same canonical also carries a mutable `draft-1`, and staging's `Questionnaire` search ignores
the `version` parameter entirely — a bogus version still returns every revision, draft first.
So it is the SDK that has to honour the pin, and the suite asserts it did rather than assuming
it: the two revisions use disjoint `linkId`s, so the QR alone reveals which one rendered.
Without that assertion a server quietly serving the draft would still pass every other check.

### Validation replay

A green e2e that would also have passed against the original bug proves nothing, so the
suite is checked against the pre-fix bridge. `save-draft` on `94fbe8b` (which called
`submit({ intent })` instead of `submit({ status })`) returns **`completed`** — the form
finalizes, silently, with no console error — so the `in-progress` assertion fails, as it
must. To re-verify after changing the flow:

```sh
git show 94fbe8b:src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js > /tmp/prefix.js
# PAGE_OWNS_SDK is not optional here. That bridge predates GH-60 and injects nothing (its only
# injected script is the Sentry CDN), so without a page-owned <script src> the element never
# upgrades and the run dies at the render wait — a fixture timeout that LOOKS like the replay
# working. Verified: with the switch, the run fails on the assertion itself,
#   AssertionError: expected 'in-progress', actual 'completed'
PAGE_OWNS_SDK=1 BRIDGE_PATH=/tmp/prefix.js npm test
```

Under `PAGE_OWNS_SDK` the host refuses the session, so every test that needs a working one skips
itself — including the handshake test, which asserts `source === "embedded"`. One helper decides
that, so the two conditions cannot drift into a run that asserts "embedded" while the page owns
the SDK.

## `WebView2Probe/` — layer 2

A minimal WinForms host that boots `TiroFormViewerR5` and asserts the handshake arrives.
That single assertion is load-bearing: it can only succeed if the second virtual host served the
~6 MB embedded bundle and a plain `<script src>` passed the `DenyCors` mapping — neither of which
layer 1 can prove, since it serves the bundle over plain HTTP from a stand-in.

It does **not** prove the element upgraded or that the bridge ran before page scripts. Neither is
asserted, and neither is observable here: `source: "embedded"` means the script's `onload` fired,
so a bundle that parses and then throws before `customElements.define` still reports `embedded`;
and the shipped page's only script is the sample banner, which has no bridge dependency. A
handshake carrying no `client` object at all is also accepted — `EvaluateWebSdkReport(null)`
returns no failure. Layer 1 covers both on pull requests (`client.name`, `client.source`, and
`customElements.get`), which is why the two layers gate together rather than separately.

Stage A stops there and needs no server, which is why it is the part a pull request gates
on; `WebSdkLoadException` (bundle missing, or a page-owned copy colliding) fails it
explicitly. Stages B1–B3 then save a draft, finalize, and `$extract`, asserting on typed
FHIR POCOs — so they also prove Firely can deserialize what the real element emits.

**The SDC version check (GH-62) is asserted with the server stages, not with stage A.** This is
the only place in the repo it runs against a real server, so it is the only thing holding three
contracts unit tests cannot reach: that `{base}/metadata` stays locally routed rather than
tunnelled to the data endpoint, that `software.version` keeps meaning the SDC server's own
version, and that `software.name` keeps saying what it says. Anything but a `Satisfied` verdict
fails the run — `Unknown` very much included, since that is the shape of the gate going
silently disarmed. The verdict is *printed* in stage A because the viewer starts the probe inside
`SetContextAsync` and it costs nothing to show; reaching it needs the server to answer, so the
assertion belongs where a server is guaranteed. It briefly did not, and a staging outage would
have reddened pull requests that could not have caused it. The cost of that placement is real:
the check is verified nightly rather than per-PR, so a change on the server side surfaces within
a day rather than on the pull request that happens to follow it.

Only the first submit of a session is retried, and deliberately so: a submit before render is
silently dropped (see below), but a *resubmit* after render races the first request rather
than replacing it, and the page refuses a second submit on a finalized response — so retrying
the finalize would turn a merely slow one into a hard failure.

A retry can still land two responses: an attempt that was merely slow produces one of its own,
arriving after the one that was returned. No drain delay bounds that, so each stage waits for the
response whose **status** it expects rather than for whatever arrives next — otherwise a straggler
from save-draft became the finalize's result and B2 failed with a status the finalize never
produced. A stage that times out reports what it did see (`saw: completed`), which is the
silent-finalize signature, so the diagnosis stays in the message.

A UI-thread exception outside the probe's own try — a WebView2 callback, an event handler —
becomes a FAIL and an exit rather than WinForms' default modal dialog, which on a headless runner
nobody dismisses: the probe would hang to the job's 25-minute ceiling at 2x Windows billing and
write no verdict.

Windows only, needs a WebView2 runtime and a desktop session — hence `windows-latest` in
`.github/workflows/e2e.yml`, which also logs the runtime version so a failure is
diagnosable.

## Which server these run against

### The intended matrix

Three cells over one parameterised job — the only thing that varies is which SDC server the
suite points at, and what verdict the version check is expected to reach against it.

> **Not built yet.** This is the shape being moved to, recorded here because the reasoning was
> settled before the code was. Today both layers point at staging and a pull request runs no
> server-backed stages at all. The floor and dev cells arrive with the floor cell (see
> *Not done here*); until then, read the table as the plan rather than the behaviour.

| Cell | When | Server | Expected version verdict | The question it answers |
|---|---|---|---|---|
| **floor** | every PR + merge | container pinned to `SdcCompatibility.MinimumSdcVersion`, pulled from `docker-ext` | `Satisfied` | Did *this harness change* break the minimum we publish? |
| **dev** | nightly | `sdc-dev.tiro.health` | `Unknown` **and** `ReportedVersion == "dev"` | Did a server change break us — at the earliest possible moment |
| **staging** | nightly | `sdc-staging.tiro.health` | `Satisfied` | Did the release candidate break us, against the artifact customers pull |

Deliberately **not** a growing N×M matrix. The harness ships as one pinned artifact, so the
only interesting points are the bottom of the supported range and the top; there is no middle
for a cell to occupy. A fourth cell should be a decision, not drift.

**Why the dev cell asserts something different rather than asserting less.** `sdc-dev` reports
`software.version: "dev"`, which is outside the version grammar by design, so the verdict there
is always `Unknown` — demanding `Satisfied` would make that cell red forever. But relaxing the
assertion to "any verdict" would throw away what it is for. `Unknown` is not one bucket:
`FromReportedVersion("dev")` sets `ReportedVersion` to `"dev"`, while `Unavailable(...)` — an
unreachable server, a tunnelled `/metadata`, a document that cannot be attributed to the SDC
server — leaves it `null`. Asserting `Unknown` **and** `"dev"` therefore still holds every
contract the strict assertion held, except the one dev can never exercise: that the grammar and
comparison work on a real version string. Staging covers that.

**Why the floor cell can only exist on layer 1.** Layer 2 needs a Windows runner and Windows
runners cannot run Linux containers, so the pinned-server cells are browser-only. Layer 2 keeps
URL targets, which is why its version-check assertion runs nightly rather than per pull request
(see *Not done here*).

**Why there is no release-triggered cell.** Tiro-health/atticus-backend#3601 designed one — the
SDC release dispatching this suite — and it was closed as deferred. Staging deploys on every
`-rc.N` tag, so the nightly already catches a breaking release candidate within a day; the
dispatch buys ~20 hours of latency and per-release attribution, at the cost of a standing GitHub
credential in Cloud Build. Reopen it when that 20 hours actually costs something.

### The shared servers, and why never production

**Staging** (`sdc-staging.tiro.health`) is the in-code default for both layers. Two reasons, and
the second is easy to overlook:

- Staging runs *ahead* of production (it was on `v0.9.38-rc.0` while prod was `v0.9.37`), so
  a server regression surfaces here before customers meet it.
- Never production: these run nightly and write QuestionnaireResponses on every pass, so
  pointing them at prod would mean synthetic clinical data in the instance customers use,
  and CI traffic in the usage signal atticus-backend#3568 aggregates to decide what to
  support.

The floor cell above is what replaces a shared server with a pinned one on layer 1. Staging
stays a target regardless — it is the only cell that exercises the *deployment*: real config,
gateway, TLS, data volume. A container tests an image; those are different things.

There is deliberately no `latest` container cell. It would duplicate staging with worse
fidelity — an image rather than a deployment — and `latest` is a moving tag, so pinning it for
reproducibility turns it into neither latest nor the floor.

The stale rationale about atticus-backend#3568's usage signal no longer applies (GH-63 was
dropped, and §2 of that issue struck with it); the reason to stay off production is the
synthetic QuestionnaireResponses alone.

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

- **The floor cell**, and it needs one thing this suite does not have yet. The image
  (`europe-docker.pkg.dev/tiroapp-4cb17/docker-ext/form-sdk-backend`) pulls keylessly —
  atticus-backend#3599 granted `harness-e2e-ar-reader` read on `docker-ext`, and the entrypoint
  is `uvicorn ... --port ${PORT:-8080} sdc_server.main:app`, so a bare `docker run -p 8080:8080`
  serves `/fhir/r5`. What a bare container does *not* have is anywhere to get a questionnaire.
  The SDC server does not store them: the element calls `Questionnaire/$resolve`, and the server
  forwards that to `{TEMPLATE_SERVER_URL}/fhir/r5/Questionnaire/$resolve`, expecting a single
  Questionnaire resource back (`sdc/questionnaire_retriever.py`,
  `CanonicalQuestionnaireRetriever`). Both hops are read from the shipped SDK bundle and the
  server source rather than inferred. So the container needs a template server, and pointing it
  at a shared one would put that shared server back in the pull-request path — the whole thing
  the floor cell exists to remove. The floor cell and the in-repo fixture below are therefore
  one piece of work, not two.
- **A fixture questionnaire in-repo**, served to the container as its template server. Both layers
  pin the canonical to `|1.0.0` now — layer 2's default was unpinned for a while, which made the
  claim below true of half the suite — and both assert the pin held rather than assuming it, so an
  edit to the draft cannot reach the suite. A *new* version of the pinned revision still could.
- Restoring layer 2's version-check assertion to per-pull-request. It runs nightly today
  because it needs a live server, and the floor cell cannot help: pinned-server cells are
  layer 1 only. Closing this needs a server layer 2 can reach on a Windows runner, which is
  not a thing that exists today.
- Asserting the three `/metadata` contracts from layer 1 in JavaScript, against the floor
  container. That would put contract coverage back on pull requests. Framed correctly it is a
  *server contract* test rather than a second implementation of the C# check — "does the server
  still answer `/metadata` with `software.name` and `software.version`" — so it does not
  reintroduce the drift the single-source rule exists to prevent.
