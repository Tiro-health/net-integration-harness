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

### Writing assertions across the vm boundary

Values the **bridge** constructs live in the sandbox realm and carry its
`Object.prototype`, so `assert.deepStrictEqual` — which `node:assert/strict` also aliases
`deepEqual` to — fails on the prototype even when every key matches, printing two
identical-looking objects. Wrap those in `plain()` from `load-bridge.mjs`:

```js
assert.deepEqual(plain(element.submitCalls[0]), { status: "in-progress" });
```

Only needed for bridge-created values. Anything the stub built runs in the host realm and
compares as normal.

## What it covers

`submit-intent.test.mjs` (**GH-50**) pins `ui.form.requestSubmit` → `submit()`. This is the
`submit({ intent })` vs `submit({ status })` bug class (#19 / PR #25) that the
`build/bridge-contract` type-check structurally **cannot** catch: calling the wrong branch
is a valid call with a valid signature, so `tsc` passes while save-draft silently finalizes
the form. Covers save-draft → `{ status: "in-progress" }`, finalize/absent/unrecognised →
bare `submit()`, and that a request before any questionnaire is displayed is a no-op.

`protocol.test.mjs` (**GH-50**) pins the acknowledgement contract: a base ack for handled
messages, `UnknownMessageTypeException` for unknown types, `HandlerException` for a
throwing handler (and exactly one response, never an error followed by an ack),
`ui.form.persist` acking as a deliberate no-op, response envelopes not being acked
themselves, and `window.tiro.cancel()`.

`form-submitted.test.mjs` (**GH-50**) pins the `tiro-submit` → `form.submitted` round trip:
request semantics rather than fire-and-forget, `tiro-submitted` only after the host acks,
`tiro-submit-error` on refusal, the `completed` status fallback never overwriting a real
status, `sanitize()` stripping nulls, and narrative generation being optional — a failing
`generateNarrative` must not cost the user their submitted form.

`configure.test.mjs` (**GH-50**) covers `sdc.configure` and launch-context edges. Its two
data-endpoint tests are a second regression guard for GH-48: the element rebuilds its
client on `dataEndpointAddress` too, so `DataEndpointAddress` is an independent route into
the same defect. Both fail against the pre-fix bridge, verified by swapping it in.

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
the **pinned** SDK bundle (`build/web-sdk/package.json`, the exact version the harness
embeds per GH-59/GH-60), with the original minified source quoted in comments beside
each behaviour. When the pin bumps, re-verify the stub against the new bundle.

That makes these tests only as accurate as the model. They validate **our** use of the
element's contract, not the element itself, and they would not have found GH-48 from
first principles — the mechanism had to be understood first. Treat a green run as
"the bridge still drives the element the way we determined it must be driven", not as
proof against the live frontend. The type-check tracks API drift; #26 is the end-to-end
answer.

If the SDK changes how it seeds or rebuilds its client, update the stub **and** quote
the new minified source in the comments, so the model stays auditable.
