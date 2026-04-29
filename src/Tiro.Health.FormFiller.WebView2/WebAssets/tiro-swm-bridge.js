/*
 * Tiro Form Filler — SMART Web Messaging bridge
 *
 * Injected by the .NET host into every embedded page before any page script
 * runs (via WebView2's AddScriptToExecuteOnDocumentCreatedAsync). The page
 * itself stays UI-only: it doesn't load Sentry, doesn't speak SMART Web
 * Messaging, doesn't know about WebView2 transport.
 *
 * What the bridge exposes to the page:
 *   - window.tiro.cancel()                  — fires ui.done (user closed without submit)
 *   - <tiro-form-filler> auto-wired         — bridge sets questionnaire on display,
 *                                              forwards user submissions to host
 *   - document CustomEvents (status hooks)  — tiro-connected, tiro-submitted,
 *                                              tiro-submit-error, tiro-cancelled,
 *                                              tiro-disconnected
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

    function wireFormFiller(formFiller) {
        // Render the questionnaire when the host says so.
        SmartWebMessaging.on("sdc.displayQuestionnaire", payload => {
            const { questionnaire, questionnaireResponse, context } = payload || {};
            if (context) SmartWebMessaging.context = { ...SmartWebMessaging.context, ...context };
            if (!questionnaire) return;

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
        });

        // Store launch context so it can be applied to the next questionnaire.
        SmartWebMessaging.on("sdc.configureContext", payload => {
            SmartWebMessaging.context = { ...SmartWebMessaging.context, ...payload };
        });

        // Host-initiated submit: trigger the form-filler's own submit flow. The form-filler
        // validates and either fires tiro-submit (which we forward below) or tiro-error.
        SmartWebMessaging.on("ui.form.requestSubmit", () => {
            if (formFiller.questionnaire) formFiller.submit();
        });

        // No-op handlers for protocol messages we don't act on (so they get a base ack
        // instead of an UnknownMessageTypeException).
        SmartWebMessaging.on("sdc.configure", () => { /* no-op */ });
        SmartWebMessaging.on("ui.form.persist", () => { /* no-op */ });

        // User submitted via the form-filler (button click or programmatic submit) →
        // build the form.submitted message and send it to the host. Page never sees this.
        formFiller.addEventListener("tiro-submit", async e => {
            let response = sanitize(e.detail.response);
            response.status = "completed";
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
    }

    function wireAllFormFillers() {
        document.querySelectorAll("tiro-form-filler").forEach(wireFormFiller);
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
    // 6. Bootstrap on DOMContentLoaded
    // ============================================================

    function bootstrap() {
        bootSentry().then(() => {
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
