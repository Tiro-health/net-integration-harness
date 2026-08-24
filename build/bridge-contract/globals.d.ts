// Ambient globals the bridge relies on that aren't in the standard DOM lib.
// Declared loosely (any) on purpose: the bridge<->host transport and the Sentry
// plumbing are out of scope for this contract check, which exists solely to verify
// the bridge's calls against the <tiro-form-filler> (@tiro-health/web-sdk) API.
// Keeping these `any` lets `tsc --checkJs` focus its errors on the element contract.

interface Window {
  chrome?: { webview?: any };
  tiro?: any;
  SmartWebMessaging?: any;
  __tiroSentryConfig?: any;
  /** Injected by the .NET host before the bridge runs; carries the versioned SDK URL. */
  __tiroSdkUrl?: string;
}

declare var Sentry: any;

// LitElement base-class members the bridge depends on. The published web-sdk .d.ts
// imports its base class from `lit`, which a type-only consumer doesn't install, so
// these are absent from the exported TiroFormFiller type — the same gap that forces
// the `& HTMLElement` intersection. Declared here rather than taking a dependency on
// `lit`, so the bridge's use of them is still checked. See tiro-swm-bridge.js §4.
interface LitElementLike {
  /**
   * Resolves once the element's pending update cycle has completed. The bridge awaits
   * this between changing endpoint attributes and applying the launch context, because
   * <tiro-form-filler> rebuilds its SDC client during that update and seeds the
   * replacement only from state set before it. See GH-48.
   */
  readonly updateComplete: Promise<boolean>;
}
