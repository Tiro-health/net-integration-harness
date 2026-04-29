# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2.

## Overview

Embed FHIR-based questionnaire forms in a WebView2 control and exchange `QuestionnaireResponse` data with them over the SMART Web Messaging protocol. The host control owns the protocol, transport, and (optional) telemetry; the embedded HTML page is purely UI — it does not need to know about SMART Web Messaging, Sentry, or WebView2 at all. The bridge JS that drives the page is bundled with the host library and auto-injected before any page script runs.

## Solution structure

```
net-integration-harness/
├── src/
│   ├── Tiro.Health.SmartWebMessaging/              # Core protocol handler (FHIR-version-agnostic)
│   ├── Tiro.Health.SmartWebMessaging.Fhir.R5/      # FHIR R5 closed bindings
│   ├── Tiro.Health.SmartWebMessaging.Fhir.R4/      # FHIR R4 closed bindings
│   ├── Tiro.Health.FormFiller.WebView2/            # WinForms UserControl + bridge JS (FHIR-agnostic)
│   ├── Tiro.Health.FormFiller.WebView2.Fhir.R5/    # Designer-friendly R5 viewer
│   ├── Tiro.Health.FormFiller.WebView2.Fhir.R4/    # Designer-friendly R4 viewer
│   └── Tiro.Health.FormFiller.WebView2.Sentry/     # Sentry-backed ITelemetrySink adapter
├── samples/
│   ├── Tiro.Health.FormFiller.WebView2.Sample/         # Single-form demo (R4)
│   └── Tiro.Health.FormFiller.WebView2.LauncherSample/ # Patient-list launcher → questionnaire dialog (R5)
└── tests/
    └── Tiro.Health.SmartWebMessaging.Tests/        # MSTest unit tests (25 tests)
```

## Projects

### `Tiro.Health.SmartWebMessaging` (core)
FHIR-version-agnostic implementation of the SMART Web Messaging protocol.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandlerBase<TResource, TQuestionnaireResponse, TOperationOutcome>` — abstract generic handler covering protocol routing, request/response correlation via `Func<SmartMessageResponse, Task>` listeners, and `CancellationToken` plumbing across the entire async surface
- **Handles**: `status.handshake`, `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, `form.submitted`, `ui.form.requestSubmit`, `ui.form.persist`, `ui.done`
- **Validation**: validates inbound `form.submitted` payloads via `Validator.ValidateObject` so subscribers never see null `Response`/`Outcome`

### `Tiro.Health.SmartWebMessaging.Fhir.R5` / `Tiro.Health.SmartWebMessaging.Fhir.R4`
Concrete bindings on top of the core library.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandler` — binds the base handler to `Resource`, `QuestionnaireResponse`, `OperationOutcome` from the corresponding `Hl7.Fhir.*` package
- **Adds**: strongly-typed `FormSubmitted` events, version-specific FHIR-resource convenience overloads on `SendSdcConfigureContextAsync` and `SendSdcDisplayQuestionnaireAsync`

### `Tiro.Health.FormFiller.WebView2`
Reusable WinForms `UserControl` that hosts a WebView2 browser and wires it to the messaging handler. FHIR-version-agnostic: derive `TiroFormViewerR4`/`R5` (or your own closed binding) to use it.

- **Targets**: `net48` (C# SDK-style, WinForms + WebView2)
- **Key type**: `TiroFormViewer<TResource, TQR, TOO>` — abstract generic UserControl
- **Features**:
  - Explicit lifecycle state machine (`TiroFormViewerState`: Initializing → Ready → ContextSet → Submitted → Disposed)
  - Async API with `CancellationToken` end-to-end; in-flight operations cancel cleanly on disposal
  - Pluggable `IEmbeddedBrowser` seam for testability (default: `WebView2EmbeddedBrowser`)
  - Pluggable `ITelemetrySink` seam (default: `NullTelemetrySink`); see telemetry section below
  - Embeds `WebAssets/tiro-swm-bridge.js` and auto-injects it into every page via WebView2's `AddScriptToExecuteOnDocumentCreatedAsync` — page is UI-only
  - Optional consumer-supplied `WebContentFolder` for hosting your own `index.html`; the shipped one is a redirect placeholder

### `Tiro.Health.FormFiller.WebView2.Fhir.R5` / `Tiro.Health.FormFiller.WebView2.Fhir.R4`
Designer-friendly closed bindings of `TiroFormViewer<,,>`.

- **Targets**: `net48`
- **Key type**: `TiroFormViewerR5` / `TiroFormViewerR4` (sealed) — drop-in WinForms control
- **Defaults**: telemetry → `SentryTelemetrySink` (Tiro DSN), so existing consumers get observability for free

### `Tiro.Health.FormFiller.WebView2.Sentry`
Sentry-backed `ITelemetrySink` adapter. Optional: only depend on this if you want the Sentry behaviour.

- **Targets**: `net48`
- **Key type**: `SentryTelemetrySink` — owns two DSNs (one for the .NET host process, one injected into the embedded page) plus environment and release. Ctor overloads let consumers override either DSN, the Sentry options, or the entire SDK init.
- Auto-detects release as `Tiro.Health.FormFiller.WebView2@<version>+<commit>` from the FormFiller assembly's `AssemblyInformationalVersion` (so traces deep-link to source via Sentry's release pipeline if you upload symbols)

### `Tiro.Health.FormFiller.WebView2.Sample` / `LauncherSample`
WinForms demos.

- `Sample` — single-form demo bound to FHIR **R4**
- `LauncherSample` — patient-list launcher that opens the questionnaire as a dialog, demonstrates running multiple form sessions in one process; bound to FHIR **R5**
- Both: `.NET 4.8` (VB.NET, old-style project format)

### `Tiro.Health.SmartWebMessaging.Tests`
- **Target**: `net8.0`
- **Framework**: MSTest + Moq
- **Coverage**: 25 tests covering protocol routing, request/response correlation, payload validation (including `form.submitted` `[Required]` enforcement), and event firing

## Usage

### 1. Drop the form viewer onto a WinForms form

In the Visual Studio designer, place a `TiroFormViewerR5` (or `TiroFormViewerR4`) on your form. From code, set context once the form is shown:

```vb
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class QuestionnaireForm

    Private Async Sub QuestionnaireForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf OnFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf OnCloseApplication

        Dim patient As New Patient() With {
            .Id = "test-123",
            .Name = New List(Of HumanName) From { New HumanName() With { .Family = "da Vinci", .Given = New List(Of String) From { "Leonardo" } } },
            .BirthDate = "1452-04-15",
            .Gender = AdministrativeGender.Male
        }

        Await TiroFormViewer.SetContextAsync(
            questionnaireCanonicalUrl:="http://example.org/fhir/Questionnaire/my-form",
            patient:=patient)
    End Sub

    Private Sub OnFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        ' e.Response is the completed QuestionnaireResponse; e.Outcome carries any validation issues.
        Me.Close()
    End Sub

    Private Sub OnCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        ' User cancelled / hit "ui.done"
        Me.Close()
    End Sub

End Class
```

`SetContextAsync` blocks (asynchronously) until the embedded page completes its handshake and acknowledges the `sdc.displayQuestionnaire` message. Pass a `CancellationToken` if the caller might abandon the operation; the viewer also cancels in-flight async work on `Dispose`.

If the form-filling user closes the host window without submitting, you can ask the page to attempt a final submission:

```vb
Private Async Sub QuestionnaireForm_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
    If Not _isFormSubmitted Then
        e.Cancel = True
        Await TiroFormViewer.SendFormRequestSubmitAsync()
    End If
End Sub
```

### 2. The embedded page

The host injects a JS bridge into every page before any page script runs. Your `index.html` therefore stays UI-only — no Sentry CDN tag, no SMART Web Messaging module, no WebView2 transport setup. A working sample lives in `samples/Tiro.Health.FormFiller.WebView2.Sample/WebContent/index.html` (~60 lines, mostly CSS).

Two seams the page interacts with:

1. **`<tiro-form-filler>`** (from `tiro-web-sdk.iife.js`) — auto-wired by the bridge: questionnaires arrive via the `questionnaire` attribute, user submissions come back via the `tiro-submit` event, and the bridge takes care of marshalling them onto the protocol.
2. **`window.tiro.cancel()`** — call from a Cancel button to send `ui.done` to the host.

The bridge dispatches `CustomEvent`s on `document` for status hooks: `tiro-connected`, `tiro-disconnected`, `tiro-submitted`, `tiro-submit-error`, `tiro-cancelled`. Listen if you want a status bar; ignore if you don't.

For advanced flows that don't fit the auto-wired form-filler model, the lower-level API is still exposed at `window.SmartWebMessaging.{sendRequest, sendEvent, on}`.

### 3. Use the handler directly (without the WinForms control)

The C# / netstandard2.0 path, for hosts that aren't WebView2-based:

```csharp
using Tiro.Health.SmartWebMessaging.Fhir.R5;

var handler = new SmartMessageHandler();
handler.SendMessage = json => YourTransport.PostAsync(json);  // your transport returns Task<string>

handler.HandshakeReceived += async (_, _) =>
{
    await handler.SendSdcDisplayQuestionnaireAsync(
        questionnaireCanonicalUrl: "http://example.org/fhir/Questionnaire/my-form",
        patient: patient);
};

handler.FormSubmitted += (_, e) =>
{
    Console.WriteLine(e.Response.ToJson());
};

// Wire your transport's inbound channel:
yourTransport.MessageReceived += json => handler.HandleMessage(json);
```

## Telemetry

The core `Tiro.Health.FormFiller.WebView2` package has **no** telemetry dependency. Telemetry is plugged in via `ITelemetrySink`:

```csharp
public interface ITelemetrySink : IDisposable
{
    ITelemetrySession BeginSession(string sessionId);
    void CaptureException(Exception ex);
    void Flush(TimeSpan timeout);
}
```

The default in the FHIR-version closed bindings (`TiroFormViewerR5`/`R4`) is `SentryTelemetrySink` from the `Tiro.Health.FormFiller.WebView2.Sentry` package. It produces:

- **One Sentry transaction per round-trip message** (e.g. `sdc.displayQuestionnaire`, `form.submitted`) — actual request/response latency, not just the `PostMessage` cost
- **One unified trace per form session** spanning both .NET and JS Sentry projects (the host injects its `traceId` into the embedded page; the JS Sentry SDK continues that trace)
- **`form.session.id` tag** on every transaction for cross-project correlation
- **Lifecycle breadcrumbs** for construction / handshake / dispose
- **Outcome-aware status** on the `form.submitted` transaction (Sentry `Ok` on success, `InvalidArgument` on validation failures)
- **Release tag** auto-derived from the FormFiller assembly's `AssemblyInformationalVersion` (`Tiro.Health.FormFiller.WebView2@<semver>+<commit>`)

To **opt out** of telemetry entirely, override `CreateTelemetrySink()` in your own `TiroFormViewer<,,>` subclass and return `NullTelemetrySink.Instance` — your closed binding never references the Sentry package.

To **redirect to your own Sentry project(s)**, construct a `SentryTelemetrySink(dsn, embeddedDsn, environment, release)` and pass it via the `TiroFormViewer<,,>` DI ctor. The host owns both DSNs (one for the .NET process, one injected into the embedded page) — the page itself never hardcodes a DSN.

## Building

### C# projects (core libraries, FormFiller, Sentry adapter, tests)

```bash
dotnet build net-integration-harness.sln
dotnet test
```

### VB.NET .NET 4.8 samples

The old-style `.vbproj` sample projects need Visual Studio's MSBuild — `dotnet build` can't grok them:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    net-integration-harness.sln /restore /nologo /verbosity:minimal
```

A full solution build via VS MSBuild restores both, builds C# libs first, then the VB samples that consume them via `<PackageReference>`.

### Local NuGet cache caveat

Since the libraries publish at version `1.0.0` and the samples consume them via `PackageReference`, the local cache at `~/.nuget/packages/tiro.health.formfiller.webview2*` can serve stale bytes after API changes. Either bump versions, or purge the affected entries plus the sample's `obj/` and rebuild.

## Architecture notes

### Generic type binding
`SmartMessageHandlerBase<TResource, TQR, TOO>` and `TiroFormViewer<TResource, TQR, TOO>` keep the protocol and UI control independent of any FHIR version. The R5/R4 modules each provide concrete sealed subclasses that bind to their `Hl7.Fhir.*` types, so designer-instantiation and consumer code use a non-generic name (`SmartMessageHandler`, `TiroFormViewerR5`, etc.).

### Polymorphic JSON deserialization
`System.Text.Json`'s `[JsonDerivedType]` attribute does not support open generic types. The base handler installs a `PayloadTypeInfoResolver<TResource, TQuestionnaireResponse>` that registers concrete closed-generic payload types (e.g. `SdcDisplayQuestionnaire<Resource, QuestionnaireResponse>`) onto whatever `JsonSerializerOptions` the consumer (or default) supplies.

### Lifecycle state machine
`TiroFormViewerState` is an explicit enum (Initializing, Ready, ContextSet, Submitted, Disposed) backed by `Interlocked` operations on an `int`. Public methods guard against invalid states (e.g. `SetContextAsync` after `Submitted` throws `InvalidOperationException`; any operation after `Dispose` throws `ObjectDisposedException`).

### Per-message Sentry transactions
The `Sentry`-backed sink starts one Sentry transaction per outbound message (round-trip — finishing on response receipt) and one per inbound notification, all sharing the trace id of the viewer's session. Lifecycle events (init, handshake, dispose) are breadcrumbs, not transactions, so Sentry's Performance dashboard shows meaningful per-operation latency rather than the noise of a long-lived "form session" transaction.

### Cross-process trace propagation
The host's traceId is injected into the embedded page in two ways: (1) as `<meta name="sentry-trace">` set by the bridge before `Sentry.init`, so the JS pageload transaction inherits the trace; (2) as `_meta.sentry.trace` on every outbound SMART Web Messaging envelope (typed via `MessageMeta` on `SmartMessageBase`), so JS-side spans during inbound handling continue the trace. The JS side echoes the trace context back on outbound messages too, completing the bidirectional propagation.

### Bridge injection
The JS that owns the page side of the protocol (`tiro-swm-bridge.js`) ships embedded in `Tiro.Health.FormFiller.WebView2` and is injected via WebView2's `AddScriptToExecuteOnDocumentCreatedAsync` so it runs before any page script. Mirrors the pattern used in `tiro-health/java-integration-harness` (form-filler-swing). The page is UI-only.
