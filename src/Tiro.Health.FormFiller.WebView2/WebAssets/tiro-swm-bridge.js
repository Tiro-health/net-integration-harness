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
 *   - document CustomEvents (status hooks)  — tiro-connected, tiro-submitted,
 *                                              tiro-submit-error, tiro-cancelled,
 *                                              tiro-disconnected, tiro-sdk-error,
 *                                              tiro-sdk-collision
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

        retryHandshake(retryIntervalMs = 1000, timeoutMs = 30000) {
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

                const attempt = () => {
                    if (resolved) return;
                    const messageId = this.generateMessageId();
                    attemptIds.push(messageId);
                    this.pendingRequests.set(messageId, { resolve: onSuccess, reject: () => {} });
                    this.sendMessage({
                        messageId,
                        messagingHandle: this.messagingHandle,
                        messageType: "status.handshake",
                        payload: {},
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
                try {
                    handler(message.payload);
                } catch (err) {
                    console.error("[SWM] handler error for " + message.messageType, err);
                    this.sendResponse(message.messageId, {
                        $type: "error",
                        errorMessage: err.message,
                        errorType: "HandlerException",
                    });
                    return;
                }
                this.sendResponse(message.messageId, { $type: "base" });
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
    // 5. Sentry boot from window.__tiroSentryConfig
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
    // 6. Embedded web-sdk injection (GH-60)
    // ============================================================

    // The embedded, validated @tiro-health/web-sdk served by the host (GH-60).
    // Must match TiroFormViewer.SdkVirtualHostName — hardcoded both sides.
    const SDK_URL = "https://tiro-sdk.example/tiro-web-sdk.iife.js";

    function bootSdk() {
        // Foreign definition = the page still carries its own SDK script tag.
        // Don't inject a second copy (double customElements.define throws); wire
        // the foreign element so the handshake and its version report still happen.
        if (typeof customElements !== "undefined" && customElements.get("tiro-form-filler")) {
            console.error(
                "[bridge] <tiro-form-filler> is already defined by a script the page loaded itself. " +
                "Remove the tiro-web-sdk <script> tag from your index.html — the harness embeds and " +
                "serves its own validated copy (GH-60).");
            fire("tiro-sdk-collision");
            return Promise.resolve(false);
        }
        return new Promise(resolve => {
            const script = document.createElement("script");
            script.src = SDK_URL;
            // No crossorigin attribute: the virtual-host mapping is DenyCors, which a
            // plain no-cors script load passes and a CORS-mode load would not.
            script.onload = () => resolve(true);
            script.onerror = () => {
                console.error("[bridge] failed to load the embedded tiro-web-sdk from " + SDK_URL +
                    " — the form cannot render. Is the page hosted by the .NET harness?");
                fire("tiro-sdk-error");
                resolve(false);
            };
            document.head.appendChild(script);
        });
    }

    // ============================================================
    // 7. Bootstrap on DOMContentLoaded
    // ============================================================

    function bootstrap() {
        // SDK loads before wiring, so elements are upgraded when wireFormFiller runs.
        Promise.all([bootSentry(), bootSdk()]).then(() => {
            wireAllFormFillers();

            const transportOk = SmartWebMessaging.init();
            if (!transportOk) {
                console.warn("[bridge] no host transport — standalone mode");
                return;
            }
            // queueMicrotask so any same-tick page-side wiring is in place before the
            // first message dispatch reaches the bridge.
            queueMicrotask(() => {
                SmartWebMessaging.retryHandshake().then(
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
