# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2.

Embed FHIR-based questionnaire forms in a WebView2 control and exchange `QuestionnaireResponse` data with them over the SMART Web Messaging protocol. The host control owns the protocol, transport, and (optional) telemetry; the embedded HTML page is purely UI — it does not need to know about SMART Web Messaging, Sentry, or WebView2 at all. The bridge JS that drives the page is bundled with the host library and auto-injected before any page script runs.

## Getting started

These libraries ship as NuGet packages and are typically consumed from a WinForms app on .NET Framework 4.8.

### 1. Add the NuGet source

The packages live on the harness's feed (or, for local development, in `artifacts/packages/` after `dotnet pack`). Add a `nuget.config` next to your `.sln`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="net-integration-harness" value="<path-or-url>" />
  </packageSources>
</configuration>
```

### 2. Reference the packages

For an R5 consumer (swap `.Fhir.R5` → `.Fhir.R4` and `Hl7.Fhir.R5` → `Hl7.Fhir.R4` for R4):

```xml
<ItemGroup>
  <PackageReference Include="Hl7.Fhir.Base" Version="5.13.2" />
  <PackageReference Include="Hl7.Fhir.R5" Version="5.13.2" />
  <PackageReference Include="Tiro.Health.SmartWebMessaging" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.SmartWebMessaging.Fhir.R5" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2.Fhir.R5" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2.Sentry" Version="1.0.0" />
</ItemGroup>
```

To opt out of Sentry telemetry, drop the `.Sentry` package and override `CreateTelemetrySink()` in your own `TiroFormViewer<,,>` subclass — see [Telemetry](#telemetry).

Old-style `.vbproj` quirks worth knowing:

- Set `<RestoreProjectStyle>PackageReference</RestoreProjectStyle>` in the `PropertyGroup`.
- Set `<RuntimeIdentifiers>win</RuntimeIdentifiers>` because WebView2 and Sentry ship native binaries.

### 3. Enable auto-generated binding redirects

The `net48` packages pull modern `System.*` assemblies (`System.Text.Json` 9.x, `System.Memory`, `System.ComponentModel.Annotations`, etc.) whose versions don't match what's in the GAC, so binding redirects are mandatory. Don't hand-maintain them — let MSBuild emit them:

```xml
<PropertyGroup>
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
```

`App.config` can then be a stub:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
```

MSBuild walks the closure each build and writes redirects into `<YourApp>.exe.config`. No manual upkeep, no drift on package upgrades.

> Library DLLs' own `app.config` files are ignored by the .NET Framework binding loader — only the executable's `.exe.config` is honored. The redirects have to come from the consuming project.

### 4. Add the FormViewer to a form

Drop a `TiroFormViewerR5` (or `TiroFormViewerR4`) onto your form in the Designer, hook the `FormSubmitted` and `CloseApplication` events, and call `SetContextAsync(questionnaireCanonicalUrl, patient)` once the form has loaded. The full sample lives at `samples/Tiro.Health.FormFiller.WebView2.Sample/Form1.vb`:

```vb
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class Form1
    ' Flag that keeps track if form has been submitted
    Private isFormSubmitted As Boolean = False

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication
        Await InitializeViewerAsync()
    End Sub

    Private Async Function InitializeViewerAsync() As System.Threading.Tasks.Task
        Dim patient As Patient = New Patient() With {
            .Name = New List(Of HumanName) From {
                New HumanName() With {
                    .Family = "da Vinci",
                    .Given = New List(Of String) From {"Leonardo"},
                    .Text = "Leonardo da Vinci"
                }
            },
            .BirthDate = "1452-04-15",
            .Gender = AdministrativeGender.Male,
            .Identifier = New List(Of Identifier) From {
                New Identifier() With {
                    .System = "http://test.org/test/patient-ids",
                    .Value = "test-123"
                }
            }
        }
        ' Hint: here it's possible to pass a previous QR as context
        Await TiroFormViewer.SetContextAsync("http://templates.tiro.health/templates/2630b8675c214707b1f86d1fbd4deb87", patient)
    End Function

    ' ----------------------------------------------------
    ' EVENT HANDLER FOR FORM SUBMISSION
    ' ----------------------------------------------------
    Private Sub HandleFormSubmitted(ByVal sender As Object, ByVal e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))

        ' Check if there are validation errors
        If e.Outcome IsNot Nothing AndAlso e.Outcome.Success = False Then
            Dim result As DialogResult = MessageBox.Show("There are validation errors. Do you want to close anyway?", "Validation Errors", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If result = DialogResult.No Then
                Return
            End If
        End If

        ' The FormSubmittedEventArgs contains the submitted FHIR resource
        Dim response As QuestionnaireResponse = TryCast(e.Response, QuestionnaireResponse)

        If response IsNot Nothing Then
            Dim narrativeHtml As String = response.Text?.Div
            If Not String.IsNullOrEmpty(narrativeHtml) Then
                MessageBox.Show(narrativeHtml, "QuestionnaireResponse Narrative", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Else
                MessageBox.Show("Submitted QuestionnaireResponse has no narrative text.", "Submission Received")
            End If
        Else
            MessageBox.Show("Form submission received, but resource was not a QuestionnaireResponse.", "Error")
        End If

        ' Close the form after handling submission
        isFormSubmitted = True
        Me.Close()
    End Sub

    ' ----------------------------------------------------
    ' EVENT HANDLER FOR CLOSE APPLICATION (ui.done)
    ' ----------------------------------------------------
    Private Sub HandleCloseApplication(ByVal sender As Object, ByVal e As CloseApplicationEventArgs)
        isFormSubmitted = True
        MessageBox.Show("Closing.", "Closing")
        Me.Close()
    End Sub

    Private Async Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        If Not isFormSubmitted Then
            e.Cancel = True
            Await TiroFormViewer.SendFormRequestSubmitAsync()
        End If
    End Sub

End Class
```

`SetContextAsync` returns once the embedded page has handshaken and acknowledged `sdc.displayQuestionnaire`. Pass a `CancellationToken` if the caller may abandon early; in-flight operations also cancel when the viewer is disposed.

## The embedded page

The host injects a JS bridge into every page before any page script runs. Your `index.html` therefore stays UI-only — no Sentry CDN tag, no SMART Web Messaging module, no WebView2 transport setup. The library ships a working default `index.html` so the samples run out-of-the-box; production integrators should ship their own (see [Shipping your own index.html](#shipping-your-own-indexhtml) below).

Two seams the page interacts with:

1. **`<tiro-form-filler>`** (from `tiro-web-sdk.iife.js`) — auto-wired by the bridge: questionnaires arrive via the `questionnaire` attribute, user submissions come back via the `tiro-submit` event, and the bridge takes care of marshalling them onto the protocol.
2. **`window.tiro.cancel()`** — call from a Cancel button to send `ui.done` to the host.

The bridge dispatches `CustomEvent`s on `document` for status hooks: `tiro-connected`, `tiro-disconnected`, `tiro-submitted`, `tiro-submit-error`, `tiro-cancelled`. Listen if you want a status bar; ignore if you don't.

For advanced flows that don't fit the auto-wired form-filler model, the lower-level API is still exposed at `window.SmartWebMessaging.{sendRequest, sendEvent, on}`.

### Configuring FHIR endpoints from the host

`<tiro-form-filler>` takes two endpoint attributes — `sdc-endpoint-address` (SDC FHIR server) and `data-endpoint-address` (FHIR data server). Configure both from the .NET host so the EHR process and the embedded JS hit the same servers; the host injects them via `AddScriptToExecuteOnDocumentCreatedAsync`, and the bridge applies them to every `<tiro-form-filler>` on the page before `tiro-web-sdk` reads attributes — overwriting any value baked into `index.html`.

```csharp
formViewer.SdcEndpointAddress  = "https://sdc.hospital.example/fhir/r5";
formViewer.DataEndpointAddress = "https://data.hospital.example/fhir/r5";
// then await formViewer.SetContextAsync(...);
```

`SdcEndpointAddress` is seeded from the closed binding's `DefaultSdcEndpointAddress` (`TiroFormViewerR5.DefaultSdcEndpointAddress` = `https://sdc.tiro.health/fhir/r5`; the R4 binding mirrors this for R4) so out-of-the-box use works. `DataEndpointAddress` has no default — set it when the form needs to reach a data server. Either property must be set before `SetContextAsync` (the bridge reads them once, when the page is first wired).

### Shipping your own index.html

The default page is fine for demos but couples your UI to the library's release cadence — branding, the embedded SDK version, copy strings, and clipboard layout all live inside the package. For production, host your own page:

1. Run any of the samples; the default page renders with a yellow banner at the top.
2. Click **Copy starter template** in that banner. The button copies a clean version of the page (banner stripped) to the clipboard.
3. Paste it into your project, e.g. `WebContent/index.html`, and tweak it — branding, the `tiro-web-sdk` version, status copy, etc. Endpoints are configured from the .NET host (see [Configuring FHIR endpoints from the host](#configuring-fhir-endpoints-from-the-host) above) — don't hardcode them in the page.
4. Mark the file(s) as content in your `.vbproj` / `.csproj` so they ship next to the executable:
   ```xml
   <ItemGroup>
     <Content Include="WebContent\**\*">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </Content>
   </ItemGroup>
   ```
5. Point `WebContentFolder` at the deployed folder before the viewer's handle is created (typically right after `InitializeComponent`):
   ```csharp
   formViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent");
   ```

The page contract stays the same: drop in a `<tiro-form-filler>` element (or call `window.SmartWebMessaging.{sendRequest, sendEvent, on}` directly for non-form-filler flows), and the auto-injected bridge handles the rest. The integrator owns the `tiro-web-sdk.iife.js` `<script>` tag.

## Using the handler without the WinForms control

The C# / netstandard2.0 path, for hosts that aren't WebView2-based:

```csharp
using Tiro.Health.SmartWebMessaging.Fhir.R5;

var handler = new SmartMessageHandler();
handler.SendMessage = json => YourTransport.PostAsync(json);  // fire-and-forget; returns Task

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
  - Optional consumer-supplied `WebContentFolder` for hosting your own `index.html`; the shipped one is a working sample with a visible banner prompting integrators to override it for production
  - Host-configured `<tiro-form-filler>` endpoints via `SdcEndpointAddress` / `DataEndpointAddress`; the bridge applies them on the page so the .NET host and embedded JS always agree on which FHIR servers to hit

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

## Troubleshooting

### WinForms Designer can't load `TiroFormViewerR5/R4`

`Hl7.Fhir.Base.dll`'s manifest strong-name-references `System.ComponentModel.Annotations` 4.2.0.0 while modern NuGet pulls 4.2.1.0. Runtime is fine (the auto-generated redirect handles it); the WinForms Designer in Visual Studio doesn't apply binding redirects, so it can fail to load the viewer at design time.

**Option A — pin the older Annotations package** (small apps):

```xml
<PackageReference Include="System.ComponentModel.Annotations" Version="4.4.1" />
```

This is the last package whose embedded assembly is still 4.2.0.0, satisfying `Hl7.Fhir.Base` directly without a redirect. NuGet emits an `NU1605` downgrade warning — expected; ignore. In larger applications skip the pin: it downgrades Annotations graph-wide and can collide with other libraries that strong-name reference 4.2.1.0+.

**Option B — instantiate the viewer programmatically** (any project size):

Skip the Designer entirely — declare and add the viewer in code:

```vb
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class Form1

    Private ReadOnly TiroFormViewer As New Tiro.Health.FormFiller.WebView2.Fhir.R5.TiroFormViewerR5() With {
        .Dock = DockStyle.Fill
    }

    Private isFormSubmitted As Boolean = False

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.Controls.Add(TiroFormViewer)
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication

        ' ... build patient, then:
        Await TiroFormViewer.SetContextAsync(
            questionnaireCanonicalUrl:="http://example.org/fhir/Questionnaire/my-form",
            patient:=patient)
    End Sub

    ' HandleFormSubmitted / HandleCloseApplication / Form1_FormClosing as in the sample
End Class
```

Disposal is handled by the form's `Controls` ownership chain — no extra cleanup needed.
