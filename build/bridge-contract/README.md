# Bridge contract test (static type-check)

Type-checks the **actual shipped** `src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js`
against the published TypeScript types of `@tiro-health/web-sdk` — the `<tiro-form-filler>` web component
the bridge drives. It exists to catch **drift between the bridge and the external frontend**, e.g. the
`submit({ intent })`-vs-`submit({ status })` bug (#19 / PR #25) that no test caught because the
bridge↔frontend seam was never exercised.

The bridge file is `include`d by relative path (never copied), so the bytes checked are the bytes shipped.

## What it asserts

The bridge's calls into the element must match the frontend's API. Concretely, `tsc --noEmit` fails if the
bridge calls `formFiller.submit(...)` (or reads `questionnaire` / `sdcClient`, etc.) with a shape the
installed `@tiro-health/web-sdk` doesn't accept.

## Version target: floating `latest` (intentionally)

CI installs `@tiro-health/web-sdk@latest` fresh on every run — **not** a pinned version. The harness is
version-agnostic (integrators load the floating `cdn.tiro.health/sdk/latest` channel), so the check tracks
whatever `latest` currently is. A red run with no harness change means **the frontend's `latest` moved the
contract** — that's the alarm, not a flake.

## Status: green

`0.3.0` is now the `latest` dist-tag, so the check passes and the job is no longer allowed to fail.

It was red for a stretch, and that red was correct rather than a flake: the bridge's `save-draft` path calls
`submit({ status: "in-progress" })`, an option added in **web-sdk `0.3.0`**, while stable `latest` was still
`0.2.3` — whose `submit()` takes no arguments. The check was reporting, accurately, that `save-draft` did not
function against the frontend the harness loads by default. **Save-draft still requires
`@tiro-health/web-sdk` >= 0.3.0**, which matters for integrators pinning an older SDK in their own
`index.html`.

A red run from here on means the frontend's `latest` moved the contract out from under the shipped bridge —
investigate rather than retry.

## Running locally

The package is on **GitHub Packages**, so npm needs a token with `read:packages`. Using the GitHub CLI:

```sh
gh auth refresh -h github.com -s read:packages    # one-time, adds the scope
cd build/bridge-contract
export NODE_AUTH_TOKEN=$(gh auth token)
npm ci
npm install --no-save @tiro-health/web-sdk@latest  # what CI checks; @next to preview an upcoming release
npm run typecheck
```

In CI the ephemeral `GITHUB_TOKEN` (with `permissions: packages: read`) is used — no stored secret, no WIF.
The package must grant the harness repo Actions read access (GitHub Packages → package → *Manage Actions access*).

## Follow-up

A heavier **behavioral** smoke test (Playwright, real element from the CDN) is tracked separately in #26.
