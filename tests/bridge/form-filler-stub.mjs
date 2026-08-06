/*
 * Stub <tiro-form-filler> modelling the observable semantics of the real
 * @tiro-health/web-sdk element, transcribed from the shipped bundle
 * (identical in v0.2.1 and v0.3.1-dev.0):
 *
 *   constructor(){ ... this.sdcEndpointAddress = "https://sdc.tiro.health/fhir/r5"
 *                      this._sdcClient = void 0 ... }
 *
 *   willUpdate(e){
 *     super.willUpdate(e),
 *     (e.has("sdcEndpointAddress") || e.has("dataEndpointAddress")) && this.sdcEndpointAddress &&
 *       (this._sdcClient = new LMt({ baseUrl: this.sdcEndpointAddress,
 *                                    launchContext: this._pendingLaunchContext,
 *                                    dataEndpoint: this.dataEndpointAddress }),
 *        this._pendingLaunchContext && (this._pendingLaunchContext = void 0))
 *   }
 *
 *   get launchContext(){ return this._sdcClient?.launchContext ?? this._pendingLaunchContext }
 *   set launchContext(e){ this._sdcClient ? this._sdcClient.launchContext = e
 *                                         : this._pendingLaunchContext = e; this.requestUpdate() }
 *
 *   attributeChangedCallback(e,t,n){ ... "launch-context" === e &&
 *       (this.launchContext = n ? GMt.fromAttribute?.(n, null) : void 0) }
 *   GMt = { fromAttribute: e => qMt(e) ?? {} }        // qMt = JSON.parse, undefined on failure
 *
 * The two behaviours that matter, and the reason this stub exists rather than a
 * bare spy: Lit coalesces attribute writes into ONE update (so willUpdate sees
 * every property changed in the same task), and the client rebuild seeds the new
 * client from _pendingLaunchContext ALONE. A launch context applied in the same
 * batch as an endpoint change therefore lands on the outgoing client and is lost.
 *
 * What the populate call ultimately reads is `element.launchContext`, so that
 * getter is what the tests assert on.
 */

export class FormFillerStub {
    constructor() {
        // Reactive-property backing fields.
        this.sdcEndpointAddress = "https://sdc.tiro.health/fhir/r5"; // SDK's built-in default
        this.dataEndpointAddress = undefined;
        this.questionnaire = undefined;
        this.readOnly = false;

        this._sdcClient = undefined;
        this._pendingLaunchContext = undefined;

        this.attributes = new Map();
        this.setAttributeLog = [];
        this._listeners = new Map();

        // Arguments of each submit() call, in order. `undefined` entries record a
        // no-argument call, which is what a finalize must produce — the distinction
        // between submit() and submit({...}) is the whole point of the intent mapping.
        this.submitCalls = [];

        // Set by tests that exercise narrative generation. The real element exposes the
        // SDC client it built; the bridge reads sdcClient.generateNarrative off it.
        this.sdcClient = undefined;

        // Lit batching state.
        this._changed = new Set();
        this._updatePending = false;
        this._updateComplete = Promise.resolve(true);

        // Lit treats constructor-assigned reactive properties as changed for the
        // first update, which is what makes the element build a client (from an
        // empty _pendingLaunchContext) before the host ever configures it.
        this._markChanged("sdcEndpointAddress");
    }

    // --- Lit-ish reactive update machinery -------------------------------

    _markChanged(prop) {
        this._changed.add(prop);
        this.requestUpdate();
    }

    requestUpdate() {
        if (this._updatePending) return this._updateComplete;
        this._updatePending = true;
        this._updateComplete = Promise.resolve().then(() => {
            const changed = this._changed;
            this._changed = new Set();
            this._updatePending = false;
            this.willUpdate(changed);
            return true;
        });
        return this._updateComplete;
    }

    get updateComplete() {
        return this._updateComplete;
    }

    willUpdate(changed) {
        if ((changed.has("sdcEndpointAddress") || changed.has("dataEndpointAddress")) && this.sdcEndpointAddress) {
            this._sdcClient = {
                baseUrl: this.sdcEndpointAddress,
                dataEndpoint: this.dataEndpointAddress,
                launchContext: this._pendingLaunchContext,
            };
            if (this._pendingLaunchContext) this._pendingLaunchContext = undefined;
        }
    }

    // --- launchContext accessors (verbatim semantics) --------------------

    get launchContext() {
        return this._sdcClient?.launchContext ?? this._pendingLaunchContext;
    }

    set launchContext(value) {
        if (this._sdcClient) this._sdcClient.launchContext = value;
        else this._pendingLaunchContext = value;
        this.requestUpdate();
    }

    // --- DOM surface used by the bridge ----------------------------------

    setAttribute(name, value) {
        this.attributes.set(name, String(value));
        this.setAttributeLog.push(name);

        switch (name) {
            case "sdc-endpoint-address": {
                const next = String(value);
                // Lit's default hasChanged is !==; writing the same value is a no-op,
                // which is precisely why the SDK's default endpoint never rebuilds.
                if (next !== this.sdcEndpointAddress) {
                    this.sdcEndpointAddress = next;
                    this._markChanged("sdcEndpointAddress");
                }
                break;
            }
            case "data-endpoint-address": {
                const next = String(value);
                if (next !== this.dataEndpointAddress) {
                    this.dataEndpointAddress = next;
                    this._markChanged("dataEndpointAddress");
                }
                break;
            }
            case "launch-context": {
                let parsed;
                try { parsed = JSON.parse(String(value)); } catch { parsed = undefined; }
                this.launchContext = parsed ?? {};
                break;
            }
            case "questionnaire":
                this.questionnaire = String(value);
                this._markChanged("questionnaire");
                break;
            default:
                break;
        }
    }

    getAttribute(name) {
        return this.attributes.has(name) ? this.attributes.get(name) : null;
    }

    toggleAttribute(name, force) {
        const on = force === undefined ? !this.attributes.has(name) : !!force;
        if (on) this.attributes.set(name, "");
        else this.attributes.delete(name);
        if (name === "read-only") this.readOnly = on;
        return on;
    }

    /**
     * Real signature is submit(options?) where options carries the target status.
     * Records the raw argument so a test can tell submit() from submit(undefined).
     */
    submit(options) {
        this.submitCalls.push(options);
    }

    addEventListener(type, handler) {
        if (!this._listeners.has(type)) this._listeners.set(type, []);
        this._listeners.get(type).push(handler);
    }

    dispatchEvent(event) {
        (this._listeners.get(event.type) || []).forEach(h => h(event));
        return true;
    }
}
