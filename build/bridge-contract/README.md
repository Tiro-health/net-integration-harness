# Bridge contract test (static type-check)

Type-checks the **actual shipped** `src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js`
against the published TypeScript types of `@tiro-health/web-sdk` — the `<tiro-form-filler>` web component
the bridge drives. It exists to catch **drift between the bridge and the frontend element**, e.g. the
`submit({ intent })`-vs-`submit({ status })` bug (#19 / PR #25) that no test caught because the
bridge↔frontend seam was never exercised.

The bridge file is `include`d by relative path (never copied), so the bytes checked are the bytes shipped.

## What it asserts

The bridge's calls into the element must match the frontend's API. Concretely, `tsc --noEmit` fails if the
bridge calls `formFiller.submit(...)` (or reads `questionnaire` / `sdcClient`, etc.) with a shape the
installed `@tiro-health/web-sdk` doesn't accept.

## Version target: the pin (GH-59)

On PRs, pushes, and the release gate, CI installs the **exact version pinned in
`build/web-sdk/package.json`** — the version the harness embeds and ships (GH-60). Bridge and
element are validated as the pair that actually ships; checking anything else would validate a
version integrators never run.

> Historical note: this check originally tracked the floating `latest` dist-tag, because integrators
> loaded `cdn.tiro.health/sdk/latest` from their own `index.html`. That rationale expired with the
> embedding architecture (GH-64) — there is no fielded `latest` population anymore. During that era
> the check was legitimately red for a stretch (bridge called `submit({ status })`, a 0.3.0 API,
> while `latest` was still 0.2.3 — i.e. save-draft genuinely didn't work against the default page's
> SDK). That failure class is now structurally impossible: the pair ships together.

The **nightly** run still checks `@latest`, as an advisory heads-up only: a red nightly means the
*next pin bump* will need bridge work — nothing shipped is affected.

## Running locally

The package is on **GitHub Packages**, so npm needs a token with `read:packages`. Using the GitHub CLI:

```sh
gh auth refresh -h github.com -s read:packages    # one-time, adds the scope
cd build/bridge-contract
export NODE_AUTH_TOKEN=$(gh auth token)
npm ci --ignore-scripts
VER=$(node -p "require('../web-sdk/package.json').dependencies['@tiro-health/web-sdk']")
npm install --no-save --ignore-scripts "@tiro-health/web-sdk@$VER"  # what CI gates on; @latest to preview the next bump
npm run typecheck
```

In CI the ephemeral `GITHUB_TOKEN` (with `permissions: packages: read`) is used — no stored secret, no WIF.
The package must grant the harness repo Actions read access (GitHub Packages → package → *Manage Actions access*).

## Follow-up

A heavier **behavioral** smoke test (Playwright, the embedded element against a dockerized SDC
server) is tracked in #26.
