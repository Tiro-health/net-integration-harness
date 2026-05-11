# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2. Specifically targets the [SDC SMART Web Messaging protocol](https://github.com/brianpos/sdc-smart-web-messaging) — the dialect of SMART Web Messaging that defines `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, and `form.submitted` for embedding SDC questionnaire renderers in EHR shells.

Embed FHIR-based questionnaire forms in a WebView2 control and exchange `QuestionnaireResponse` data with them over the SMART Web Messaging protocol. The host control owns the protocol, transport, and (optional) telemetry; the embedded HTML page is purely UI — it does not need to know about SMART Web Messaging, Sentry, or WebView2 at all. The bridge JS that drives the page is bundled with the host library and auto-injected before any page script runs.

## Getting started

These libraries ship as NuGet packages and are typically consumed from a WinForms app on .NET Framework 4.8.

### 1. Reference the packages

There is no umbrella `net-integration-harness` package — the harness ships as a handful of individual NuGet packages that you install separately. In Visual Studio, right-click your project → **Manage NuGet Packages...** and search for each one (swap `.Fhir.R5` → `.Fhir.R4` for an R4 consumer):

- `Hl7.Fhir.Base`
- `Hl7.Fhir.R5` (or `Hl7.Fhir.R4`)
- `Tiro.Health.SmartWebMessaging`
- `Tiro.Health.SmartWebMessaging.Fhir.R5` (or `.Fhir.R4`)
- `Tiro.Health.FormFiller.WebView2`
- `Tiro.Health.FormFiller.WebView2.Fhir.R5` (or `.Fhir.R4`)
- *(optional)* `Tiro.Health.FormFiller.WebView2.Sentry` — only if you want Sentry telemetry; see [Telemetry](#telemetry)

The resulting `<PackageReference>` block in your `.csproj` / `.vbproj` should look like:

```xml
<ItemGroup>
  <PackageReference Include="Hl7.Fhir.Base" Version="5.13.2" />
  <PackageReference Include="Hl7.Fhir.R5" Version="5.13.2" />
  <PackageReference Include="Tiro.Health.SmartWebMessaging" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.SmartWebMessaging.Fhir.R5" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2" Version="1.0.0" />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2.Fhir.R5" Version="1.0.0" />
</ItemGroup>
```

Old-style `.vbproj` projects (the `<Project ToolsVersion="15.0">` format — anything that isn't SDK-style `<Project Sdk="...">`) need a few extra properties. There's no Properties UI for these, so edit the XML directly: in Visual Studio right-click the project → **Unload Project** → right-click again → **Edit `<ProjectName>.vbproj`** (or open the file in any text editor). Add these inside the first `<PropertyGroup>` — the one with `<Configuration>` / `<OutputType>` / `<TargetFrameworkVersion>` etc.:

```xml
<RestoreProjectStyle>PackageReference</RestoreProjectStyle>
<RuntimeIdentifiers>win</RuntimeIdentifiers>
```

- `RestoreProjectStyle` — pins the project to PackageReference. Without it the **Manage NuGet Packages** dialog can silently fall back to `packages.config` for new installs, and you end up with a mix of `<PackageReference>` (existing) and `<Reference>` + `packages.config` (new ones).
- `RuntimeIdentifiers` — tells MSBuild to copy the runtime-specific native DLLs into your output folder (WebView2's `WebView2Loader.dll`, and Sentry's native bits if you opt in). Without it the WebView2 control fails at runtime with a missing-DLL error.

> **Working against a local build of the harness?** Run `dotnet pack` and add `artifacts/packages/` as a custom package source via **Tools → NuGet Package Manager → Package Manager Settings → Package Sources**, then install from that source.

### 2. Enable auto-generated binding redirects

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

### 3. Add the FormViewer to a form

Open `Form1.vb` in the WinForms Designer in Visual Studio, then:

1. **Drop the form viewer first.** Drag `TiroFormViewerR5` (or `TiroFormViewerR4`) from the Toolbox onto the form, set `Name = TiroFormViewer` (matches what the sample code below references) and `Dock = Fill`. If it's not in the Toolbox, build the project once, then right-click the Toolbox → **Choose Items...** → browse to your `bin\Debug\Tiro.Health.FormFiller.WebView2.Fhir.R5.dll`.
2. **Drop a `Panel` onto the form** (not into the viewer), set `Dock = Bottom`, `Height = 46`. The viewer should resize to fill the area above it.
3. **Drop a `Button` into the panel**, set `Name = SubmitButton` (this matters — VB.NET wires the `Handles SubmitButton.Click` clause from the sample by name; the default `Button1` won't bind), `Text = "Submit"`, `Anchor = Top, Right`, drag it near the right edge.

> **Add order matters for docking.** The viewer needs to be added to the form's `Controls` collection *before* the bottom panel — that's how WinForms decides which docked sibling claims its slice first. The Designer gets this right as long as you place the viewer before the panel; if it doesn't, swap the two `Controls.Add(...)` calls in `Form1.Designer.vb`.

Then wire three things:

- `FormSubmitted` — the page emitted a QR. Inspect `e.Response`, optionally check `e.Outcome.Success` for validation errors, then close.
- `CloseApplication` — the page emitted `ui.done` (e.g. its own Cancel button). Just close the form.
- The Submit button's `Click` — `Await TiroFormViewer.SendFormRequestSubmitAsync()`. The page validates and round-trips back via `FormSubmitted`.

The full sample lives at `samples/Tiro.Health.FormFiller.WebView2.Sample/Form1.vb`:

```vb
Imports Hl7.Fhir.Model
Imports Tiro.Health.SmartWebMessaging.Events

Public Class Form1

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        AddHandler TiroFormViewer.FormSubmitted, AddressOf HandleFormSubmitted
        AddHandler TiroFormViewer.CloseApplication, AddressOf HandleCloseApplication

        Dim patient As New Patient() With {
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

        Await TiroFormViewer.SetContextAsync(
            "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699",
            patient)
    End Sub

    Private Async Sub SubmitButton_Click(sender As Object, e As EventArgs) Handles SubmitButton.Click
        Await TiroFormViewer.SendFormRequestSubmitAsync()
    End Sub

    Private Sub HandleFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
        If e.Outcome IsNot Nothing AndAlso e.Outcome.Success = False Then
            Dim result As DialogResult = MessageBox.Show(
                "There are validation errors. Close anyway?",
                "Validation Errors",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning)
            If result = DialogResult.No Then Return
        End If

        ' QuestionnaireResponse.Text.Div is the XHTML narrative the SDC backend
        ' generates. Plain-text and RTF alternatives live on QR.text via the
        ' http://fhir.tiro.health/StructureDefinition/narrative-alternative-format
        ' extension (an Attachment with ContentType "text/plain" or "text/rtf").
        ' See the EhrShellSample's QuestionnaireResponseHelper for how to read those.
        Dim narrativeHtml As String = e.Response.Text?.Div
        If Not String.IsNullOrEmpty(narrativeHtml) Then
            MessageBox.Show(narrativeHtml, "QuestionnaireResponse Narrative", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End If

        Me.Close()
    End Sub

    Private Sub HandleCloseApplication(sender As Object, e As CloseApplicationEventArgs)
        Me.Close()
    End Sub

End Class
```

> **Want X-button → page-validate → keep-form-open-on-errors?** Hook `Form1_FormClosing`, set `e.Cancel = True`, `Await TiroFormViewer.SendFormRequestSubmitAsync()`, and use a flag to let the eventual `FormSubmitted` close re-enter cleanly. Skipped in this minimal sample — the EHR Shell sample shows the equivalent pattern (tab switch + explicit `Close session` button) for an embedded-in-tab integration.

`SetContextAsync` returns once the embedded page has handshaken and acknowledged `sdc.displayQuestionnaire`. Pass a `CancellationToken` if the caller may abandon early; in-flight operations also cancel when the viewer is disposed.

## Shipping your own index.html

The library ships a working default `index.html` so the samples run out-of-the-box, but for production you'll want to host your own page. The bridge and SMART Web Messaging plumbing are auto-injected by the host (regardless of which page is loaded), so your `index.html` stays UI-only — no SDK init, no transport setup, no Sentry CDN tag.

1. Run any of the samples; the default page renders with a yellow banner at the top.
2. Click **Copy starter template** in that banner. The button writes a hardcoded minimal HTML5 page to your clipboard — `<!DOCTYPE html>`, the SDK script, the two CSS rules needed to make the form-filler fill the viewport, and a bare `<tiro-form-filler id="form-filler">`. No banner, no runtime-applied attributes (`questionnaire`, `launch-context`, `sdc-endpoint-address`), no SDK-injected styles.
3. Paste it into your project, e.g. `WebContent/index.html`, and tweak it — branding, the `tiro-web-sdk` version, status copy, etc. Endpoints are configured from the .NET host (see [Configuring FHIR endpoints from the host](#configuring-fhir-endpoints-from-the-host) below) — don't hardcode them in the page.
4. Mark the file(s) as content in your `.vbproj` / `.csproj` so they ship next to the executable:
   ```xml
   <ItemGroup>
     <Content Include="WebContent\**\*">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </Content>
   </ItemGroup>
   ```
5. Point `WebContentFolder` at the deployed folder. In a Designer-built VB.NET form, set it inside `Form_Load` *before* you call `SetContextAsync` (the WebView2 initializes lazily inside `SetContextAsync`, so as long as the property is set first, the right `index.html` is the one that loads):
   ```vb
   Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
       TiroFormViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent")
       ' ... AddHandler / build patient / SetContextAsync ...
   End Sub
   ```

## Telemetry

The core `Tiro.Health.FormFiller.WebView2` package has **no** telemetry dependency. Telemetry is plugged in via `ITelemetrySink`:

```vb
Public Interface ITelemetrySink
    Inherits IDisposable

    Function BeginSession(sessionId As String) As ITelemetrySession
    Sub CaptureException(ex As Exception)
    Sub Flush(timeout As TimeSpan)
End Interface
```

The FHIR-version closed bindings (`TiroFormViewerR5`/`R4`) default to `NullTelemetrySink` (no-op): the `.Sentry` adapter is **not** a transitive dependency, so by default no Sentry NuGet, no SDK init, no `Sentry.init` on the embedded page.

To **opt in to Sentry telemetry**, add the adapter package and assign `New SentryTelemetrySink()` to the Designer-placed viewer's `TelemetrySink` property in `Form_Load`, before `SetContextAsync`:

```xml
<PackageReference Include="Tiro.Health.FormFiller.WebView2.Sentry" Version="1.0.0" />
```

```vb
Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
    TiroFormViewer.TelemetrySink = New SentryTelemetrySink()
    ' ... AddHandler / SetContextAsync ...
End Sub
```

The session is started lazily on the first `SetContextAsync` call, so as long as the property is set before then, the supplied sink is used. Setting `TelemetrySink` after the session has begun throws `InvalidOperationException`.

The parameterless ctor uses **Tiro's hosted DSNs** — host telemetry to `tirohealth/dotnet-winforms`, embedded-page telemetry to `tirohealth/javascript` (same Sentry org, unified trace view). This is the **recommended** path during integration: the Tiro team can see your form sessions and help diagnose issues quickly. The defaults are designed to be safe to ship — **no FHIR payloads are attached to spans**, so PHI does not flow to Sentry. What you do get:

- **One Sentry transaction per round-trip message** (e.g. `sdc.displayQuestionnaire`, `form.submitted`) — actual request/response latency, not just the `PostMessage` cost
- **One unified trace per form session** spanning both .NET and JS Sentry projects (the host injects its `traceId` into the embedded page; the JS Sentry SDK continues that trace)
- **`form.session.id` tag** + **`messageType` tag** on every transaction for cross-project correlation
- **`questionnaire_url` tag** on `sdc.displayQuestionnaire` — the canonical URL of the form, not patient data
- **Lifecycle breadcrumbs** for construction / handshake / dispose
- **Outcome-aware status** on the `form.submitted` transaction (Sentry `Ok` on success, `InvalidArgument` on validation failures)
- **Exceptions** captured via `SentrySdk.CaptureException` — the .NET-side exception type, message, and stack trace (these typically don't carry PHI; if your application code surfaces patient identifiers in exception messages, you'd want to scrub them before they bubble up)
- **Release tag** auto-derived from the FormFiller assembly's `AssemblyInformationalVersion` (`Tiro.Health.FormFiller.WebView2@<semver>+<commit>`)

To **redirect to your own Sentry project(s)** instead, assign `New SentryTelemetrySink(dsn, embeddedDsn, environment, release)` (or pass a `SentryOptions`) to the same `TelemetrySink` property. The host owns both DSNs (one for the .NET process, one injected into the embedded page).

For any other backend, implement `ITelemetrySink` yourself and pass it the same way.

## Configuring FHIR endpoints from the host

A `<tiro-form-filler>` typically talks to **two** FHIR servers:

- **SDC server** — Tiro's Form SDK backend (see [docs.tiro.health → SDC Backend](https://docs.tiro.health/form-sdk/sdc-backend)). Serves the `Questionnaire` definitions, expands ValueSets for choice fields, and runs `$populate` (prefill), `$validate`, and `$generate-narrative`.
- **Data server** — the FHIR endpoint that holds the **prepopulation data** the form fills itself from (`Patient`, `Observation`, `Condition`, etc.). The SDC backend's `$populate` operation reads from this server (via the `X-Data-Endpoint` header) to seed initial values.

Configure both from the .NET host so the host process and the embedded JS hit the same servers. After the SMART Web Messaging handshake, the host sends an [`sdc.configure`](https://github.com/brianpos/sdc-smart-web-messaging) message carrying the endpoint addresses; the bridge stashes the payload and applies it to every `<tiro-form-filler>` on the page right before flipping the `questionnaire` attribute on — overwriting any value baked into `index.html`. (The SDC server lands on `payload.configuration.sdcServer`, not `payload.terminologyServer`: an SDC backend isn't a terminology server in the strict SDC SWM sense, so we use the protocol's renderer-specific extension point.)

```vb
' SDC backend — fetches the Questionnaire by canonical URL, expands ValueSets,
' runs $populate / $validate / $generate-narrative.
TiroFormViewer.SdcEndpointAddress = "https://sdc.hospital.example/fhir/r5"

' Data server — the FHIR endpoint with the prepopulation data. The SDC backend
' reaches into it (via X-Data-Endpoint) when running $populate. Leave unset if
' the form doesn't prefill from FHIR data.
TiroFormViewer.DataEndpointAddress = "https://data.hospital.example/fhir/r5"

' then Await TiroFormViewer.SetContextAsync(...)
```

`SdcEndpointAddress` is seeded from the closed binding's `DefaultSdcEndpointAddress` (`TiroFormViewerR5.DefaultSdcEndpointAddress` = `https://sdc.tiro.health/fhir/r5`; the R4 binding mirrors this for R4) so out-of-the-box demos work without configuration. `DataEndpointAddress` has no default. Either property must be set **before** `SetContextAsync` (the bridge reads them once, when the page is first wired).

> **Production integrators should host their own SDC server and override `SdcEndpointAddress`.** `sdc.tiro.health` is a best-effort shared instance for demos and getting-started use — it offers no SLA, no uptime guarantees, and isn't suitable for clinical workflows.

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
│   ├── Tiro.Health.FormFiller.WebView2.Sample/         # Single-form, single-patient demo (R4)
│   └── Tiro.Health.FormFiller.WebView2.EhrShellSample/ # Dummy EHR shell — patient/encounter/template selection,
│                                                       # tabbed viewer, in-memory QR persistence, custom index.html (R5)
└── tests/
    ├── Tiro.Health.SmartWebMessaging.Tests/        # MSTest, protocol/handler coverage
    └── Tiro.Health.FormFiller.WebView2.Tests/      # MSTest, viewer lifecycle + telemetry contracts + embedded assets
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
- **Defaults**: telemetry → `NullTelemetrySink` (no-op). Opt in to Sentry by referencing `Tiro.Health.FormFiller.WebView2.Sentry` and passing `New SentryTelemetrySink()` to the ctor — see [Telemetry](#telemetry)

### `Tiro.Health.FormFiller.WebView2.Sentry`
Sentry-backed `ITelemetrySink` adapter. Optional: only depend on this if you want the Sentry behaviour.

- **Targets**: `net48`
- **Key type**: `SentryTelemetrySink` — owns two DSNs (one for the .NET host process, one injected into the embedded page) plus environment and release. Ctor overloads let consumers override either DSN, the Sentry options, or the entire SDK init.
- Auto-detects release as `Tiro.Health.FormFiller.WebView2@<version>+<commit>` from the FormFiller assembly's `AssemblyInformationalVersion` (so traces deep-link to source via Sentry's release pipeline if you upload symbols)

### `Tiro.Health.FormFiller.WebView2.Sample` / `EhrShellSample`
WinForms demos.

- **`Sample`** — single-form, single-patient demo bound to FHIR **R4**. The smallest possible "see the API working" reference: native Submit button, default `index.html`, no persistence. Shows the submitted QR's XHTML narrative (`Text.Div`) in a `MessageBox` — for a richer rendering or the plain-text alternative-format extension, see the `EhrShellSample`'s `QuestionnaireResponseHelper`.
- **`EhrShellSample`** — dummy EHR shell bound to FHIR **R5**. Demonstrates the integration patterns a real EHR is going to need:
  - **Practitioner identity** (top status strip) passed through as the `author` in `LaunchContext`.
  - **Patient / encounter / template selection** — three hardcoded patients with their own encounters in the left sidebar; three canonical templates verified live on the default SDC server, picked via a modal `TemplatePickerDialog` from the **+ New report** button.
  - **Reports list per patient** — every submitted `QuestionnaireResponse` is saved in an in-memory `ResponseStore` keyed by `(patient, encounter, template)` and shown newest-first in the Patient details tab. Relaunching the same combination passes the saved QR as `initialResponse` so the user resumes where they left off.
  - **Read-only narrative preview** — single-clicking a saved report renders its narrative in a `RichTextBox` (RTF when the SDC's `$generate-narrative` produced one, plain-text fallback otherwise — both via `QuestionnaireResponseHelper`). The preview is decoupled from session state, so the doctor can peek at older reports while a form is in progress.
  - **Consultation window** — clicking **Open this report** spawns a separate top-level `ReportConsultationForm` with its own `TiroFormViewerR5`. The main shell's session is left alive (showcasing that multiple viewer instances coexist), and the consultation viewer loads a different `WebContent/Consultation/index.html` that bakes `<tiro-form-filler read-only>` into the page so the form is view-only.
  - **Tabbed embedding with dynamic Form tab** — the Form tab only exists while a session is alive (added to / removed from `TabControl.TabPages` on launch / dispose). A context banner above the form viewer shows what's being filled. Switching tabs while filling *hides* the WebView2 (state preserved, JS keeps running, messages still route); explicit **Close session** button *disposes* it (state gone, viewer recreated next launch). Showcases the hide-vs-dispose contrast.
  - **Custom `index.html` per role** — bundles `WebContent/Form/index.html` (editable) and `WebContent/Consultation/index.html` (read-only). The integrator picks which page to load by setting `WebContentFolder`, not by passing UI flags through the host API — UI concerns stay in the page. See [Shipping your own index.html](#shipping-your-own-indexhtml).
- Both: `.NET 4.8` (VB.NET, old-style project format).

### `Tiro.Health.SmartWebMessaging.Tests` / `Tiro.Health.FormFiller.WebView2.Tests`
- **Targets**: `net8.0` (SmartWebMessaging) / `net48` (FormFiller — needs WinForms + WebView2)
- **Framework**: MSTest + Moq
- **Coverage** (~79 test methods across both projects):
  - `SmartWebMessaging.Tests` — protocol routing, request/response correlation, payload validation (including `form.submitted` `[Required]` enforcement), event firing, JSON probe, async-task extensions
  - `FormFiller.WebView2.Tests` — viewer lifecycle (state machine transitions, dispose semantics), telemetry sink contracts (`NullTelemetrySink` no-ops, span ordering, session tagging), embedded `WebAssets/` resource integrity

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

## Page-side API

Reference for integrators customizing their `index.html`. The auto-injected bridge exposes the following to the page:

- **`<tiro-form-filler>`** (from `tiro-web-sdk.iife.js`) — auto-wired by the bridge: questionnaires arrive via the `questionnaire` attribute, user submissions come back via the `tiro-submit` event, and the bridge takes care of marshalling them onto the protocol.
- **`window.tiro.cancel()`** — call from a Cancel button to send `ui.done` to the host.
- **`document` `CustomEvent`s** for status hooks: `tiro-connected`, `tiro-disconnected`, `tiro-submitted`, `tiro-submit-error`, `tiro-cancelled`. Listen if you want a status bar; ignore if you don't.
- **`window.SmartWebMessaging.{sendRequest, sendEvent, on}`** — lower-level API for advanced flows that don't fit the auto-wired form-filler model.

The integrator owns the `tiro-web-sdk.iife.js` `<script>` tag in their `index.html`.

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
