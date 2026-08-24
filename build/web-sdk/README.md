# web-sdk pin (GH-59 / GH-60)

This directory pins the **exact** `@tiro-health/web-sdk` version that
`Tiro.Health.FormFiller.WebView2` embeds and serves to the page. The harness
ships the bundle it was validated against — the SDK version is not a choice
integrators or customers make (see the decision record in GH-64).

- `package.json` — the pin (exact version, no range) plus `tiro.expectedElementVersion`,
  which arms the host-side version assert (GH-61). **Leave it `null`.** The served
  URL carries the SDK version, so a cached bundle from a previous release cannot
  be loaded in the first place; and the virtual host serves from local disk, with
  no network, proxy or CDN that could substitute other bytes. What refuses a
  session is the handshake's `source` field (`collision` / `error`), which is
  entirely ours and needs no SDK support. Setting `expectedElementVersion` adds a
  way to refuse a working session in exchange for detecting something already
  prevented — a bad trade here. The element's reported version is kept for
  diagnostics (`TiroFormViewer.PageWebSdkVersion`, and naming the foreign version
  in a collision message).
- `copy-bundle.mjs` — stages the bundle + generated `web-sdk.version.json` into
  `src/Tiro.Health.FormFiller.WebView2/WebAssets/` (gitignored there, embedded
  as resources at build time).

## Staging the bundle (required before any build)

The package lives on **GitHub Packages** (private), so npm needs a token with
`read:packages`. Put it in your **user-level** `~/.npmrc` once — the `.npmrc`
files in this repo deliberately carry no credentials (see the note in
`.npmrc`):

```sh
gh auth refresh -h github.com -s read:packages                        # one-time, adds the scope
npm config set //npm.pkg.github.com/:_authToken "$(gh auth token)"    # one-time, writes ~/.npmrc
```

Then, after every pin change:

```sh
cd build/web-sdk
npm ci --ignore-scripts
node copy-bundle.mjs
```

Building `Tiro.Health.FormFiller.WebView2` **fails hard** when the bundle is
missing *or* staged from a different version than the pin — re-run
`copy-bundle.mjs` after every pin change. Stale staging is self-consistent
(bundle and manifest agree with each other), so nothing downstream can catch
it. Under the no-opt-out embedding design a package without the right bundle is
a broken control, so there is no silent skip.

CI stages via `.github/actions/stage-web-sdk` using the ephemeral `GITHUB_TOKEN`
(`permissions: packages: read`); the package must grant this repo Actions read
access (GitHub Packages → package → *Manage Actions access*).

## Bumping the pin

Dependabot proposes bumps as PRs (`.github/dependabot.yml`; requires the
`DEPENDABOT_GITHUB_PACKAGES_TOKEN` Dependabot secret — a PAT with
`read:packages` — because Dependabot cannot use the workflows' ephemeral
token). A bump PR is gated by:

- the `bridge-contract` type-check, which runs against **this pin** (not `latest`),
- the bridge behavioral suite (`tests/bridge`),
- the e2e smoke once GH-26 lands.

The nightly `@latest` run in `bridge-contract.yml` is an advisory heads-up for
the *next* bump — a red nightly means the next bump needs bridge work, not that
anything shipped is broken.
