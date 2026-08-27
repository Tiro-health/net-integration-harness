# web-sdk pin (GH-59 / GH-60)

This directory pins the **exact** `@tiro-health/web-sdk` version that
`Tiro.Health.FormFiller.WebView2` embeds and serves to the page. The harness
ships the bundle it was validated against — the SDK version is not a choice
integrators or customers make (see the decision record in GH-64).

- `package.json` — the pin: an exact version, no range.
- `copy-bundle.mjs` — stages the bundle + generated `web-sdk.version.json` into
  `src/Tiro.Health.FormFiller.WebView2/WebAssets/` (gitignored there, embedded
  as resources at build time).

## The pinned version is part of the URL

The bridge loads the bundle from `https://tiro-sdk.example/tiro-web-sdk.<version>.iife.js`,
built from the staged manifest and injected by the host as `window.__tiroSdkUrl`. The
version is in the file name because WebView2 caches by URL and virtual-host responses carry
no cache headers: at a constant path, an upgraded harness could keep running the previous
release's bundle — exactly the bridge↔element skew embedding exists to prevent.

That is also why the host does **not** compare the version the page reports at handshake
against the embedded one. A stale bundle cannot load, and the virtual host reads from local
disk rather than over a network, so the realistic substitution paths are gone. Nor would an
equality assert be an integrity control against the remaining one — a tampered bundle in
`%TEMP%` self-reports whatever version it likes.

What refuses a session is the handshake's `source` field — `collision` (the page loaded its
own copy) or `error` (ours failed to load) — which needs no cooperation from the SDK. An
equality assert would only add a way to refuse a *working* session, e.g. if a future SDK
renamed or dropped its static version field. The reported version is kept purely as
diagnostics (`TiroFormViewer.PageWebSdkVersion`).

## The bundle is committed

`src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-web-sdk.iife.js` and its
`web-sdk.version.json` are **tracked in this repo**, so `git clone && build` works with no
token, no npm install and no staging step — for a new contributor, a fresh CI runner, or you
on a new machine.

That is a reversal. They used to be gitignored and fetched from GitHub Packages, which needs a
`read:packages` token. The token protected nothing: the same bytes are served unauthenticated
from `cdn.tiro.health/sdk/v<version>/tiro-web-sdk.iife.js`, ship inside the NuGet package on
nuget.org, and this repo is public. What it did do was make a missing bundle the first of six
build errors — the other five being dependent projects compiling against a stale DLL and
reporting an old API surface as though the source were wrong.

The cost is ~6 MB of git history per pin bump, a few times a year.

## Bumping the pin

`copy-bundle.mjs` still exists, and this is the one time you run it:

```sh
# one-time, if you have never authenticated to GitHub Packages
gh auth refresh -h github.com -s read:packages
npm config set //npm.pkg.github.com/:_authToken "$(gh auth token)"
```

```sh
# edit package.json to the new version, then
cd build/web-sdk
npm ci
node copy-bundle.mjs
git add -A src/Tiro.Health.FormFiller.WebView2/WebAssets build/web-sdk
```

**Commit the result.** Bumping `package.json` alone fails the build: the csproj compares the
pin against the committed `web-sdk.version.json` and refuses a mismatch, because a stale bundle
is self-consistent and nothing downstream would catch it.
