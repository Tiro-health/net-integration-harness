/*
 * Tiro Form Filler — SMART Web Messaging bridge
 *
 * Injected by the .NET host into every embedded page before any page script
 * runs (via WebView2's AddScriptToExecuteOnDocumentCreatedAsync). The page
 * itself stays UI-only: it doesn't load Sentry, doesn't speak SMART Web
 * Messaging, doesn't know about WebView2 transport.
 *
 * What the bridge exposes to the page:
 *   - tiro-web-sdk auto-injected            — the bridge loads the embedded, validated
 *                                              @tiro-health/web-sdk bundle from the host
 *                                              (GH-60); the page has NO SDK script tag
 *   - window.tiro.cancel()                  — fires ui.done (user closed without submit)
 *   - <tiro-form-filler> auto-wired         — bridge sets questionnaire on display,
 *                                              forwards user submissions to host
 *   - host text insertion                   — ui.form.insertText types host-supplied text
 *                                              into the focused field at the caret
 *   - document CustomEvents (status hooks)  — tiro-connected, tiro-submitted,
 *                                              tiro-submit-error, tiro-cancelled,
 *                                              tiro-disconnected, tiro-sdk-error,
 *                                              tiro-sdk-collision, tiro-text-inserted
 *   - window.SmartWebMessaging              — lower-level API for advanced consumers
 *                                              (sendRequest/sendEvent/on); the documented
 *                                              path is the hooks above.
 */
(function () {
    "use strict";

    // ============================================================
    // 1. SmartWebMessaging — protocol module (private internals)
    // ============================================================

    const SmartWebMessaging = {
        messagingHandle: "smart-web-messaging",
        pendingRequests: new Map(),
        listeners: {},
        context: null,

        generateMessageId() { return crypto.randomUUID(); },
        isWebView2() { return !!(window.chrome && window.chrome.webview); },

        on(messageType, handler) { this.listeners[messageType] = handler; },

        sendMessage(message) {
            const doSend = () => {
                // Attach the current Sentry trace context to the outbound envelope so the
                // .NET host can inspect _meta.sentry on inbound messages and keep both
                // sides in the same trace. Best-effort.
                try {
                    if (typeof Sentry !== "undefined" && typeof Sentry.getTraceData === "function") {
                        const td = Sentry.getTraceData();
                        const trace = td && td["sentry-trace"];
                        if (trace) {
                            message._meta = message._meta || {};
                            message._meta.sentry = { trace };
                            if (td["baggage"]) message._meta.sentry.baggage = td["baggage"];
                        }
                    }
                } catch (e) { /* ignore */ }

                if (this.isWebView2()) {
                    window.chrome.webview.postMessage(message);
                } else {
                    console.warn("[SWM] No host available");
                }
            };
            if (typeof Sentry !== "undefined" && typeof Sentry.startSpan === "function") {
                Sentry.startSpan({ op: "swm.send", name: message.messageType || "response" }, span => {
                    span.setAttribute("swm.messageId", message.messageId);
                    span.setAttribute("swm.messageType", message.messageType || null);
                    span.setAttribute("swm.isResponse", !!message.responseToMessageId);
                    doSend();
                });
            } else {
                doSend();
            }
        },

        sendRequest(messageType, payload = {}) {
            return new Promise((resolve, reject) => {
                const messageId = this.generateMessageId();
                this.pendingRequests.set(messageId, { resolve, reject });
                this.sendMessage({ messageId, messagingHandle: this.messagingHandle, messageType, payload });
                setTimeout(() => {
                    if (this.pendingRequests.has(messageId)) {
                        this.pendingRequests.delete(messageId);
                        reject(new Error(`Request timeout: ${messageType}`));
                    }
                }, 30000);
            });
        },

        sendEvent(messageType, payload = {}) {
            this.sendMessage({
                messageId: this.generateMessageId(),
                messagingHandle: this.messagingHandle,
                messageType,
                payload,
            });
        },

        sendResponse(responseToMessageId, payload) {
            this.sendMessage({
                messageId: this.generateMessageId(),
                responseToMessageId,
                additionalResponsesExpected: false,
                payload,
            });
        },

        retryHandshake(payload = {}, retryIntervalMs = 1000, timeoutMs = 30000) {
            return new Promise((resolve, reject) => {
                const start = Date.now();
                const attemptIds = [];
                let resolved = false;

                const onSuccess = payload => {
                    if (resolved) return;
                    resolved = true;
                    attemptIds.forEach(id => this.pendingRequests.delete(id));
                    resolve(payload);
                };

                // An error ack means the host REFUSED the session (GH-61) — terminal,
                // so stop retrying and report immediately instead of timing out.
                const onError = err => {
                    if (resolved) return;
                    resolved = true;
                    attemptIds.forEach(id => this.pendingRequests.delete(id));
                    reject(err);
                };

                const attempt = () => {
                    if (resolved) return;
                    const messageId = this.generateMessageId();
                    attemptIds.push(messageId);
                    this.pendingRequests.set(messageId, { resolve: onSuccess, reject: onError });
                    this.sendMessage({
                        messageId,
                        messagingHandle: this.messagingHandle,
                        messageType: "status.handshake",
                        payload,
                    });
                    setTimeout(() => {
                        if (!resolved && (Date.now() - start) < timeoutMs) attempt();
                    }, retryIntervalMs);
                };

                setTimeout(() => {
                    if (!resolved) {
                        attemptIds.forEach(id => this.pendingRequests.delete(id));
                        reject(new Error("Handshake timeout"));
                    }
                }, timeoutMs);

                attempt();
            });
        },

        handleMessage(message) {
            const doHandle = () => {
                if (message.responseToMessageId) {
                    const pending = this.pendingRequests.get(message.responseToMessageId);
                    if (pending) {
                        if (message.payload && message.payload.$type === "error")
                            pending.reject(new Error(message.payload.errorMessage));
                        else
                            pending.resolve(message.payload);
                        if (!message.additionalResponsesExpected)
                            this.pendingRequests.delete(message.responseToMessageId);
                    }
                    return;
                }
                if (message.messageType) this.handleHostMessage(message);
            };
            const withReceiveSpan = () => {
                if (typeof Sentry !== "undefined" && typeof Sentry.startSpan === "function") {
                    Sentry.startSpan({ op: "swm.receive", name: message.messageType || "response" }, span => {
                        span.setAttribute("swm.messageId", message.messageId || message.responseToMessageId);
                        span.setAttribute("swm.messageType", message.messageType || null);
                        span.setAttribute("swm.isResponse", !!message.responseToMessageId);
                        doHandle();
                    });
                } else {
                    doHandle();
                }
            };
            // Continue the trace from any inbound _meta.sentry.trace.
            const incomingTrace = message && message._meta && message._meta.sentry && message._meta.sentry.trace;
            const incomingBaggage = message && message._meta && message._meta.sentry && message._meta.sentry.baggage;
            if (incomingTrace && typeof Sentry !== "undefined" && typeof Sentry.continueTrace === "function") {
                Sentry.continueTrace({ sentryTrace: incomingTrace, baggage: incomingBaggage }, withReceiveSpan);
            } else {
                withReceiveSpan();
            }
        },

        handleHostMessage(message) {
            const handler = this.listeners[message.messageType];
            if (handler) {
                let extras;
                try {
                    // A handler that returns a plain object has its fields merged into the ack,
                    // which is how the host learns an outcome the message itself can't carry —
                    // ui.form.insertText answering `{ inserted: false }` because the clinician
                    // wasn't standing in a field. Not an error (nothing failed), so it must not
                    // take the error branch; the .NET side reads it off the response payload's
                    // extension fields. Handlers that return nothing ack exactly as before.
                    extras = handler(message.payload);
                } catch (err) {
                    console.error("[SWM] handler error for " + message.messageType, err);
                    this.sendResponse(message.messageId, {
                        $type: "error",
                        errorMessage: err.message,
                        errorType: "HandlerException",
                    });
                    return;
                }
                // $type is written FIRST and re-asserted last: System.Text.Json wants the
                // polymorphic discriminator ahead of the payload's own fields, and Object.assign
                // overwrites a value without moving the key, so a handler can't displace it.
                const ack = { $type: "base" };
                if (extras && typeof extras === "object") Object.assign(ack, extras, { $type: "base" });
                this.sendResponse(message.messageId, ack);
            } else {
                this.sendResponse(message.messageId, {
                    $type: "error",
                    errorMessage: `Unknown message type: ${message.messageType}`,
                    errorType: "UnknownMessageTypeException",
                });
            }
        },

        init() {
            if (this.isWebView2()) {
                window.chrome.webview.addEventListener("message", e => this.handleMessage(e.data));
                return true;
            }
            return false;
        },
    };

    window.SmartWebMessaging = SmartWebMessaging;

    // ============================================================
    // 2. window.tiro — minimal page-facing host API
    // ============================================================

    window.tiro = {
        cancel() {
            SmartWebMessaging.sendEvent("ui.done", {});
            document.dispatchEvent(new CustomEvent("tiro-cancelled"));
        },
    };

    // ============================================================
    // 3. Internal helpers
    // ============================================================

    function sanitize(value) {
        if (value === null) return undefined;
        if (typeof value !== "object") return value;
        if (Array.isArray(value)) return value.map(sanitize).filter(v => v !== undefined);
        const out = {};
        for (const [k, v] of Object.entries(value)) {
            const s = sanitize(v);
            if (s !== undefined) out[k] = s;
        }
        return out;
    }

    function fire(name, detail) {
        document.dispatchEvent(new CustomEvent(name, detail !== undefined ? { detail } : undefined));
    }

    // ============================================================
    // 4. Auto-wire <tiro-form-filler>
    // ============================================================

    // The handle is intersected with HTMLElement because the published web-sdk .d.ts
    // imports its base class (LitElement) from `lit`, which a type-only consumer doesn't
    // install — without the intersection the element would lose setAttribute/addEventListener.
    // LitElementLike (build/bridge-contract/globals.d.ts) restores the base-class members
    // the bridge uses — currently updateComplete — for the same reason.
    // The element-specific members we care about (submit, questionnaire, sdcClient) are
    // declared directly on TiroFormFiller, so the submit() contract is still checked.
    /** @param {import("@tiro-health/web-sdk").TiroFormFiller & HTMLElement & LitElementLike} formFiller */
    function wireFormFiller(formFiller) {
        // Endpoint config is driven by the protocol's sdc.configure message (per the SDC
        // SMART Web Messaging dialect — see github.com/brianpos/sdc-smart-web-messaging).
        // The host sends sdc.configure after handshake; we stash the payload here and
        // apply the contained server addresses to the form-filler element's attributes
        // immediately before flipping the `questionnaire` attribute on, since tiro-web-sdk
        // reads its endpoint attributes once at init time (= when `questionnaire` is set).
        let pendingFormFillerConfig = null;

        SmartWebMessaging.on("sdc.configure", payload => {
            pendingFormFillerConfig = payload || null;
        });

        // Render the questionnaire when the host says so.
        SmartWebMessaging.on("sdc.displayQuestionnaire", payload => {
            const { questionnaire, questionnaireResponse, context } = payload || {};
            if (context) SmartWebMessaging.context = { ...SmartWebMessaging.context, ...context };
            if (!questionnaire) return;

            // Apply the most recent sdc.configure payload to the form-filler's endpoint
            // attributes before init. Field mapping: the SDC server isn't a terminology
            // server, so we carry it on `payload.configuration.sdcServer` (the protocol's
            // renderer-specific extension point) rather than overloading `terminologyServer`.
            // `payload.dataServer` maps cleanly to `data-endpoint-address`.
            if (pendingFormFillerConfig) {
                const configuration = pendingFormFillerConfig.configuration;
                const sdcServer = configuration && configuration.sdcServer;
                const dataServer = pendingFormFillerConfig.dataServer;
                if (sdcServer) formFiller.setAttribute("sdc-endpoint-address", sdcServer);
                if (dataServer) formFiller.setAttribute("data-endpoint-address", dataServer);

                // `configuration.readOnly` renders the form view-only. Applied here, before
                // the `questionnaire` attribute flips on below, so a read-only launch never
                // paints an editable form first. Set as an ATTRIBUTE, not via the element's
                // `readOnly` property: the tiro-web-sdk script is `defer`red, so the custom
                // element may not be upgraded yet at this point — attributes survive upgrade,
                // pre-upgrade property assignments get shadowed by Lit's accessors. Only ever
                // set (never cleared): the host omits the field when false, and the page-side
                // default is already false.
                if (configuration && configuration.readOnly) formFiller.toggleAttribute("read-only", true);
            }

            // Everything below must land in a LATER update than the endpoint attributes
            // above. tiro-web-sdk rebuilds its SDC client inside willUpdate() whenever
            // sdcEndpointAddress changes, and seeds the replacement from
            // _pendingLaunchContext alone:
            //
            //   willUpdate(changed) {
            //     (changed.has("sdcEndpointAddress") || changed.has("dataEndpointAddress")) &&
            //       (this._sdcClient = new SdcClient({ baseUrl: this.sdcEndpointAddress,
            //                                          launchContext: this._pendingLaunchContext, ... }))
            //   }
            //   set launchContext(v) { this._sdcClient ? this._sdcClient.launchContext = v
            //                                          : this._pendingLaunchContext = v }
            //
            // The element already owns a client by the time we run — its constructor
            // defaults sdcEndpointAddress to the Tiro demo server — so a launch context set
            // in the SAME batch as an endpoint change is written to the OUTGOING client and
            // discarded by the rebuild. $populate then goes out with no context parameters
            // and every %patient / %encounter expression resolves empty. Reordering within
            // the batch cannot help; the rebuild always wins. Awaiting updateComplete lets it
            // settle so the launch context lands on the client that survives.
            //
            // Only reproduces when the host's endpoint differs from the SDK's default:
            // writing back the identical default is not a Lit change, so nothing rebuilds.
            // See GH-48.
            Promise.resolve(formFiller.updateComplete)
                .then(() => {
                    if (SmartWebMessaging.context && Array.isArray(SmartWebMessaging.context.launchContext)) {
                        const launchContext = {};
                        SmartWebMessaging.context.launchContext.forEach(item => {
                            if (item.name && item.contentResource) launchContext[item.name] = item.contentResource;
                        });
                        if (Object.keys(launchContext).length > 0)
                            formFiller.setAttribute("launch-context", JSON.stringify(launchContext));
                    }

                    if (questionnaireResponse)
                        formFiller.setAttribute("initial-response", JSON.stringify(questionnaireResponse));

                    formFiller.setAttribute(
                        "questionnaire",
                        typeof questionnaire === "string" ? questionnaire : JSON.stringify(questionnaire)
                    );
                })
                .catch(err => console.error("[bridge] failed to display questionnaire:", err));
        });

        // Store launch context so it can be applied to the next questionnaire.
        SmartWebMessaging.on("sdc.configureContext", payload => {
            SmartWebMessaging.context = { ...SmartWebMessaging.context, ...payload };
        });

        // Host-initiated submit: trigger the form-filler's own submit flow. The form-filler
        // validates and either fires tiro-submit (which we forward below) or tiro-error.
        // The optional intent ("finalize" | "save-draft") maps to the form-filler's target
        // status. The form still owns the completed → amended promotion (via originate
        // provenance) and the required-field validation skip for in-progress drafts.
        SmartWebMessaging.on("ui.form.requestSubmit", payload => {
            if (!formFiller.questionnaire) return;
            const intent = payload && payload.intent;
            if (intent === "save-draft") {
                formFiller.submit({ status: "in-progress" });
            } else {
                formFiller.submit();
            }
        });

        // No-op handler for the protocol message we don't act on (so it gets a base ack
        // instead of an UnknownMessageTypeException).
        SmartWebMessaging.on("ui.form.persist", () => { /* no-op */ });

        // User submitted via the form-filler (button click or programmatic submit) →
        // build the form.submitted message and send it to the host. Page never sees this.
        formFiller.addEventListener("tiro-submit", /** @param {CustomEvent} e */ async e => {
            let response = sanitize(e.detail.response);
            // The form component owns the resulting status (completed / amended / in-progress).
            // Keep a defensive fallback only if it somehow arrives unset.
            if (!response.status) response.status = "completed";
            try {
                if (formFiller.sdcClient && typeof formFiller.sdcClient.generateNarrative === "function") {
                    response.text = await formFiller.sdcClient.generateNarrative(response);
                }
            } catch (err) {
                console.warn("[bridge] Narrative generation failed:", err);
            }
            const outcome = {
                resourceType: "OperationOutcome",
                issue: [{
                    severity: "information",
                    code: "informational",
                    diagnostics: "Form submitted successfully",
                }],
            };
            try {
                await SmartWebMessaging.sendRequest("form.submitted", { response, outcome });
                fire("tiro-submitted", { response });
            } catch (err) {
                console.error("[bridge] form.submitted failed:", err);
                fire("tiro-submit-error", { error: err });
            }
        });

        // Forward the form-filler's dirty-state transitions to the host. Fire-and-forget
        // (sendEvent, not sendRequest) — the host doesn't need to acknowledge it, only
        // observe it, same as window.tiro.cancel()'s ui.done.
        formFiller.addEventListener("tiro-dirty-change", /** @param {CustomEvent} e */ e => {
            SmartWebMessaging.sendEvent("ui.form.dirtyChanged", { isDirty: e.detail.isDirty });
        });
    }

    function wireAllFormFillers() {
        document.querySelectorAll("tiro-form-filler").forEach(el =>
            wireFormFiller(/** @type {import("@tiro-health/web-sdk").TiroFormFiller & HTMLElement & LitElementLike} */ (el)));
    }

    // ============================================================
    // 5. Host-driven text insertion (ui.form.insertText)
    // ============================================================

    // The host owns UI the page can't see — a snippet list, a labelled clipboard, a
    // macro menu in the EHR. `ui.form.insertText` lets that UI type into the field the
    // clinician is standing in, at the caret, WITHOUT touching the QuestionnaireResponse:
    // the text goes in through the same input events a keystroke produces, so the
    // renderer stays the only writer of answers and validation, dirty-state, provenance
    // and the form's own undo all keep working. Nothing here knows what a linkId is.
    //
    // Types are deliberately loose (`any`) in this section: it handles whatever field the
    // page or the SDK happens to render, which is outside the frontend contract that
    // build/bridge-contract checks. Casting each access would add noise without checking
    // anything real.

    /**
     * The page's last-focused text field. Kept because the host-side click that triggers
     * an insert moves OS focus out of the WebView2 — by the time the message arrives,
     * `document.activeElement` may be the body.
     * @type {any}
     */
    let lastEditable = null;

    /**
     * Caret/selection inside that field, snapshotted on the way out. Only needed for
     * contenteditable: Chromium restores an <input>/<textarea>'s selection itself when the
     * element regains focus, but a contenteditable comes back with the caret collapsed to
     * the start, which would drop host text at the top of a paragraph the clinician was
     * typing at the end of.
     * @type {any}
     */
    let lastRange = null;

    // <input> types that accept typed text and are sane targets for a host snippet.
    // password is excluded on purpose (a host menu has no business filling one), as are
    // number/date/color/checkbox and friends, whose value grammar an arbitrary snippet
    // would violate — insertText into them either no-ops or produces an invalid value.
    const INSERTABLE_INPUT_TYPES = ["text", "search", "url", "tel", "email"];

    /** @param {any} node */
    function isTextEditable(node) {
        if (!node || node.nodeType !== 1) return false;
        if (node.isContentEditable) return true;
        const tag = node.tagName;
        if (tag === "TEXTAREA") return !node.readOnly && !node.disabled;
        if (tag !== "INPUT") return false;
        const type = (node.getAttribute("type") || "text").toLowerCase();
        return INSERTABLE_INPUT_TYPES.indexOf(type) !== -1 && !node.readOnly && !node.disabled;
    }

    /**
     * The focused element, descending through shadow roots. The form's fields live inside
     * <tiro-form-filler>'s shadow tree, where `document.activeElement` stops at the host
     * element.
     * @returns {any}
     */
    function deepActiveElement() {
        let el = /** @type {any} */ (document.activeElement);
        while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
        return el;
    }

    /**
     * The selection that belongs to `el`, or null. Reads it off the element's own root:
     * a selection inside a shadow tree is not visible on `document.getSelection()` in
     * Chromium — `shadowRoot.getSelection()` is.
     * @param {any} el
     * @returns {any}
     */
    function selectionRangeIn(el) {
        try {
            const root = typeof el.getRootNode === "function" ? el.getRootNode() : document;
            const selection = typeof root.getSelection === "function"
                ? root.getSelection()
                : (typeof document.getSelection === "function" ? document.getSelection() : null);
            if (!selection || selection.rangeCount === 0) return null;
            const range = selection.getRangeAt(0);
            return el.contains(range.commonAncestorContainer) ? range.cloneRange() : null;
        } catch (err) {
            return null;
        }
    }

    /** @param {any} el @param {any} range */
    function restoreRange(el, range) {
        try {
            if (!el.contains(range.commonAncestorContainer)) return;
            const root = typeof el.getRootNode === "function" ? el.getRootNode() : document;
            const selection = typeof root.getSelection === "function"
                ? root.getSelection()
                : (typeof document.getSelection === "function" ? document.getSelection() : null);
            if (!selection) return;
            selection.removeAllRanges();
            selection.addRange(range);
        } catch (err) {
            // Caret stays wherever the browser put it — text still lands in the right field.
        }
    }

    /**
     * Last resort when execCommand is unavailable or refuses. Splices the value and
     * dispatches a bubbling `input` event, going through the PROTOTYPE's value setter:
     * React installs its own setter on the instance and only notices a change made
     * through the prototype's. contenteditable has no equivalent, so it isn't attempted.
     * @param {any} el
     * @param {string} text
     */
    function spliceValue(el, text) {
        if (el.isContentEditable) return false;
        if (typeof el.value !== "string") return false;
        const start = typeof el.selectionStart === "number" ? el.selectionStart : el.value.length;
        const end = typeof el.selectionEnd === "number" ? el.selectionEnd : start;
        const next = el.value.slice(0, start) + text + el.value.slice(end);
        const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), "value");
        if (descriptor && descriptor.set) descriptor.set.call(el, next);
        else el.value = next;
        const caret = start + text.length;
        try { el.setSelectionRange(caret, caret); } catch (err) { /* not all inputs support it */ }
        el.dispatchEvent(new Event("input", { bubbles: true }));
        return true;
    }

    /**
     * Inserts `text` at the caret of the focused (or last-focused) text field.
     * @param {string} text
     * @returns {any} the field it landed in, or null when there was none
     */
    function insertTextAtCaret(text) {
        const active = deepActiveElement();
        const target = isTextEditable(active)
            ? active
            : (lastEditable && lastEditable.isConnected ? lastEditable : null);
        if (!target) return null;

        if (target !== active) {
            // The host-side click took OS focus out of the WebView2. The .NET side gives
            // focus back to the browser control; this puts it back on the field, so the
            // clinician's next keystroke continues where the snippet ended.
            try { target.focus({ preventScroll: true }); } catch (err) { target.focus(); }
            if (lastRange) restoreRange(target, lastRange);
        }

        // execCommand is deprecated, and still the only insertion Chromium routes through
        // beforeinput/input as if it had been typed — which is what makes a React-controlled
        // field (every field the SDK renders) keep the text. Assigning .value or textContent
        // updates the DOM and is reverted on the next render, with the text never reaching
        // the QuestionnaireResponse: the answer appears, then vanishes on the next keystroke.
        let inserted = false;
        try {
            inserted = document.execCommand("insertText", false, text);
        } catch (err) {
            inserted = false;
        }
        if (!inserted) inserted = spliceValue(target, text);

        lastRange = null;
        return inserted ? target : null;
    }

    function installTextInsertion() {
        // composedPath()[0] rather than e.target: a focus event crossing a shadow boundary
        // is retargeted to the host element (<tiro-form-filler>), and the path's first entry
        // is the field the user actually clicked into.
        const originOf = event => {
            const path = typeof event.composedPath === "function" ? event.composedPath() : null;
            return path && path.length ? path[0] : event.target;
        };

        document.addEventListener("focusin", event => {
            const el = originOf(event);
            if (!isTextEditable(el)) return;
            lastEditable = el;
            lastRange = null;
        }, true);

        // Snapshot the caret while the field still has it (contenteditable only, see above).
        document.addEventListener("focusout", event => {
            const el = originOf(event);
            if (!el || el !== lastEditable || !el.isContentEditable) return;
            lastRange = selectionRangeIn(el);
        }, true);

        SmartWebMessaging.on("ui.form.insertText", payload => {
            const text = payload && payload.text;
            if (typeof text !== "string" || text.length === 0) {
                console.warn("[bridge] ui.form.insertText carried no text");
                return { inserted: false };
            }
            const target = insertTextAtCaret(text);
            if (!target) {
                // Not an error: the host UI is reachable at any time, including before the
                // clinician has clicked into anything. Ack with the outcome so the host can
                // say "click a field first" instead of failing silently.
                console.warn("[bridge] ui.form.insertText: no focused text field to insert into");
            }
            fire("tiro-text-inserted", { text, inserted: !!target, target });
            return { inserted: !!target };
        });
    }

    // ============================================================
    // 6. Sentry boot from window.__tiroSentryConfig
    // ============================================================

    function bootSentry() {
        const cfg = window.__tiroSentryConfig;
        if (!cfg || !cfg.dsn) return Promise.resolve();

        // Set sentry-trace + baggage meta tags BEFORE Sentry.init so
        // browserTracingIntegration picks them up for the pageload transaction.
        if (cfg.sentryTrace) {
            const m = document.createElement("meta");
            m.name = "sentry-trace";
            m.content = cfg.sentryTrace;
            document.head.appendChild(m);
        }
        if (cfg.baggage) {
            const b = document.createElement("meta");
            b.name = "baggage";
            b.content = cfg.baggage;
            document.head.appendChild(b);
        }

        const tryInit = () => {
            if (typeof Sentry === "undefined" || typeof Sentry.init !== "function") return false;
            Sentry.init({
                dsn: cfg.dsn,
                environment: cfg.environment || undefined,
                release: cfg.release || undefined,
                tracesSampleRate: 1.0,
                integrations: [Sentry.browserTracingIntegration()],
            });
            return true;
        };

        if (tryInit()) return Promise.resolve();

        // Sentry SDK not loaded yet — inject it. Page doesn't need a <script> tag.
        const sdkUrl = cfg.sdkUrl || "https://browser.sentry-cdn.com/10.33.0/bundle.tracing.min.js";
        return new Promise(resolve => {
            const script = document.createElement("script");
            script.src = sdkUrl;
            script.crossOrigin = "anonymous";
            script.onload = () => { tryInit(); resolve(); };
            script.onerror = () => { console.warn("[bridge] Sentry CDN failed to load"); resolve(); };
            document.head.appendChild(script);
        });
    }

    // ============================================================
    // 7. Embedded web-sdk injection (GH-60)
    // ============================================================

    // The embedded, validated @tiro-health/web-sdk served by the host (GH-60). The host
    // injects the URL as window.__tiroSdkUrl before this script runs, because the file name
    // carries the SDK version for cache-busting and a static asset cannot know it. There is
    // no default: the only URL that ever works is the one the host publishes, so a missing
    // injection is a load failure, reported as such rather than sent to a 404 whose message
    // would blame the page's hosting.
    const SDK_URL = window.__tiroSdkUrl;

    // Resolves with the SDK source reported at handshake: "embedded" | "collision"
    // | "error". The host refuses the session on the latter two (GH-61).
    function bootSdk() {
        // Foreign definition or a page-level SDK script tag = the page still loads
        // its own SDK. Don't inject a second copy (double customElements.define
        // throws); wire what's there so the handshake report reaches the host.
        const foreignElement = typeof customElements !== "undefined" && customElements.get("tiro-form-filler");
        const foreignTag = typeof document.querySelector === "function"
            && document.querySelector('script[src*="tiro-web-sdk"]');
        if (foreignElement || foreignTag) {
            console.error(
                "[bridge] the page loads its own tiro-web-sdk copy. " +
                "Remove the tiro-web-sdk <script> tag from your index.html — the harness embeds and " +
                "serves its own validated copy (GH-60).");
            fire("tiro-sdk-collision");
            return Promise.resolve("collision");
        }
        if (!SDK_URL) {
            console.error("[bridge] window.__tiroSdkUrl was not injected, so there is no SDK to "
                + "load. The .NET host injects it before this script; a page-only harness must "
                + "set it too.");
            fire("tiro-sdk-error");
            return Promise.resolve("error");
        }

        return new Promise(resolve => {
            const script = document.createElement("script");
            script.src = SDK_URL;
            // No crossorigin attribute: the virtual-host mapping is DenyCors, which a
            // plain no-cors script load passes and a CORS-mode load would not.
            script.onload = () => resolve("embedded");
            script.onerror = () => {
                console.error("[bridge] failed to load the embedded tiro-web-sdk from " + SDK_URL +
                    " — the form cannot render. Is the page hosted by the .NET harness?");
                fire("tiro-sdk-error");
                resolve("error");
            };
            document.head.appendChild(script);
        });
    }

    // ============================================================
    // 8. Bootstrap on DOMContentLoaded
    // ============================================================

    function bootstrap() {
        // SDK loads before wiring, so elements are upgraded when wireFormFiller runs.
        Promise.all([bootSentry(), bootSdk()]).then(([, sdkSource]) => {
            wireAllFormFillers();
            // Document-wide and element-agnostic, so it's installed once rather than per
            // <tiro-form-filler>: the host may want to type into a field the page itself
            // renders (a free-text box beside the form) just as much as into the form's.
            installTextInsertion();

            const transportOk = SmartWebMessaging.init();
            if (!transportOk) {
                console.warn("[bridge] no host transport — standalone mode");
                return;
            }
            // queueMicrotask so any same-tick page-side wiring is in place before the
            // first message dispatch reaches the bridge.
            queueMicrotask(() => {
                // GH-61: report the element's build-time version (static on the class);
                // null = SDK predates the version field, failed to load, or is foreign.
                // Typed loosely until the SDK declares `static version` (atticus-frontend#2927).
                const cls = /** @type {{ version?: unknown } | undefined} */ (
                    typeof customElements !== "undefined"
                        ? customElements.get("tiro-form-filler")
                        : undefined);
                const client = {
                    name: "tiro-web-sdk",
                    version: cls && typeof cls.version === "string" ? cls.version : null,
                    source: sdkSource,
                };
                SmartWebMessaging.retryHandshake({ client }).then(
                    () => fire("tiro-connected"),
                    err => fire("tiro-disconnected", { error: err })
                );
            });
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", bootstrap);
    } else {
        bootstrap();
    }
})();
