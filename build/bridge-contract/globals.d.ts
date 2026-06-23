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
}

declare var Sentry: any;
