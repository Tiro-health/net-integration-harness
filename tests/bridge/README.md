# Bridge behavioral tests

Runs the **actual shipped** `src/Tiro.Health.FormFiller.WebView2/WebAssets/tiro-swm-bridge.js`
against a stub `<tiro-form-filler>` and asserts on what the element ends up holding.

Complements the two other checks on this seam:

| Check | Location | Catches |
|---|---|---|
| Static type-check | `build/bridge-contract` | bridge calls the element with a shape its types reject |
| **Behavioral (this)** | `tests/bridge` | bridge calls the element in the wrong *order* / *update cycle* |
| Playwright smoke | tracked in #26 | anything only the real element and a real browser reveal |

The bridge file is read from its real path and executed in a `node:vm` context — the
bytes tested are the bytes shipped, never a copy.

## Running

Node 20+ only. No dependencies, no network, no jsdom, no npm install:

```sh
cd tests/bridge
node --test          # or: npm test
```

## What it covers

`dirty-state.test.mjs` pins **GH-46**: `<tiro-form-filler>`'s `tiro-dirty-change` event is
forwarded to the host as a fire-and-forget `ui.form.dirtyChanged` message. It asserts the
message type and payload shape, that both the dirty *and* cleared transitions arrive (a
one-way-only forward would latch `IsDirty` on stale state), that it registers no pending
request, and that displaying a questionnaire doesn't clobber the listener.

These use the optional host transport — `loadBridge(elements, { host: true })` installs a
stub `window.chrome.webview`, so outbound envelopes are captured via `sent(messageType)`
and host→page messages can be injected with `receive(envelope)`. With a transport present
the bridge enters its handshake retry loop; the sandbox's `setTimeout` is unref'd so those
timers can't hold the test process open.

`launch-context.test.mjs` is the regression suite for **GH-48**: launch context was
dropped whenever the host's `SdcEndpointAddress` differed from the tiro-web-sdk's
built-in default, so `$populate` went out with no `context` parameters and every
`%patient` / `%encounter` expression resolved empty.

The element rebuilds its SDC client inside `willUpdate()` when `sdcEndpointAddress`
changes, seeding the replacement from `_pendingLaunchContext` alone. A launch context
applied in the same Lit update as an endpoint change lands on the outgoing client and
is discarded. The suite pins both the broken path (custom endpoint) and the path that
always worked (the SDK default, where writing back the identical value is not a change
and nothing rebuilds), so a fix for one cannot silently break the other.

## Scope and limits

`form-filler-stub.mjs` models the element's semantics — Lit's update batching, the
`launchContext` getter/setter, and the `willUpdate` client rebuild — transcribed from
the shipped SDK bundle, with the original minified source quoted in comments beside
each behaviour.

That makes these tests only as accurate as the model. They validate **our** use of the
element's contract, not the element itself, and they would not have found GH-48 from
first principles — the mechanism had to be understood first. Treat a green run as
"the bridge still drives the element the way we determined it must be driven", not as
proof against the live frontend. The type-check tracks API drift; #26 is the end-to-end
answer.

If the SDK changes how it seeds or rebuilds its client, update the stub **and** quote
the new minified source in the comments, so the model stays auditable.
