# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2. Specifically targets the [SDC SMART Web Messaging protocol](https://github.com/brianpos/sdc-smart-web-messaging) — the dialect of SMART Web Messaging that defines `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, and `form.submitted` for embedding SDC questionnaire renderers in EHR shells.

Embed FHIR-based questionnaire forms in a WebView2 control and exchange `QuestionnaireResponse` data with them over the SMART Web Messaging protocol. The host control owns the protocol, transport, and (optional) telemetry; the embedded HTML page is purely UI — it does not need to know about SMART Web Messaging, Sentry, or WebView2 at all. The bridge JS that drives the page is bundled with the host library and auto-injected before any page script runs.

## Getting started

These libraries ship as NuGet packages and are typically consumed from a WinForms app on .NET Framework 4.8.

### 1. Reference the packages

There is no umbrella `net-integration-harness` package. In Visual Studio, right-click your project → **Manage NuGet Packages...** → **Browse** tab → install:

- **`Tiro.Health.FormFiller.WebView2.Fhir.R5`** (or `.Fhir.R4` for an R4 consumer) — the closed-binding control. Pulls in the messaging core, the WebView2 host, and `Hl7.Fhir.*` transitively.
- *(optional)* **`Tiro.Health.FormFiller.WebView2.Sentry`** — Sentry-backed telemetry adapter. Only if you want telemetry; see [Telemetry](#telemetry).

That's it — two top-level package references. Everything else (`Tiro.Health.SmartWebMessaging`, `Tiro.Health.SmartWebMessaging.Fhir.*`, `Tiro.Health.FormFiller.WebView2`, `Hl7.Fhir.Base`, `Hl7.Fhir.R5`/`R4`, `Hl7.Fhir.Conformance`, etc.) comes through transitively.

The resulting `<PackageReference>` block in your `.csproj` / `.vbproj`:

```xml
<ItemGroup>
  <PackageReference Include="Tiro.Health.FormFiller.WebView2.Fhir.R5" Version="..." />
  <PackageReference Include="Tiro.Health.FormFiller.WebView2.Sentry" Version="..." />
</ItemGroup>
```

(Drop the Sentry line if you don't want telemetry.) Pin to the latest published version — the **Manage NuGet Packages** UI fills the `Version` in for you on install.

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

Optionally, a fourth: `FormDirtyChanged`/`IsDirty` — track whether the user has made unsaved changes, e.g. to warn before closing. See [Warn on unsaved changes](#warn-on-unsaved-changes) below.

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

#### Warn on unsaved changes

`TiroFormViewer.IsDirty` reflects whether the user has made any changes to the displayed form
since it loaded — kept in sync from the page's `ui.form.dirtyChanged` notifications, and also
raised as the `FormDirtyChanged` event. Pre-populated/auto-`$populate`d answers do not count as
dirty, only genuine user edits do. Hook `Form1_FormClosing` to warn before the window closes
with unsaved changes:

```vb
Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
    If TiroFormViewer.IsDirty Then
        If MessageBox.Show("You have unsaved changes. Close anyway?", "Unsaved changes",
                            MessageBoxButtons.YesNo) = DialogResult.No Then
            e.Cancel = True
        End If
    End If
End Sub
```

If a program-initiated close (e.g. from `HandleFormSubmitted`/`HandleCloseApplication`) can also
run while the form is still dirty, guard with a flag set right before that `Me.Close()` call so
`Form1_FormClosing` doesn't re-prompt on its own close — see
`samples/Tiro.Health.FormFiller.WebView2.ExtractSample/Form1.vb` for the full pattern.

> `IsDirty` needs a frontend that fires `tiro-dirty-change` (`tiro-web-sdk` >= 0.3.2).
> The harness embeds an SDK that satisfies this — see
> [Frontend version compatibility](#frontend-version-compatibility).

`SetContextAsync` returns once the embedded page has handshaken and acknowledged `sdc.displayQuestionnaire`. Pass a `CancellationToken` if the caller may abandon early; in-flight operations also cancel when the viewer is disposed.

`patient`/`encounter`/`author` cover the well-known launch context entries ("patient"/"encounter"/"user"). To pass any other named resource (e.g. a `Coverage`, `Device`, or an app-specific launch parameter), use the `launchContext` parameter — it's appended alongside the named shorthand, not instead of it:

```vb
Dim coverage As New Coverage() With {.Id = "COV1"}

Await TiroFormViewer.SetContextAsync(
    "http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699",
    patient:=patient,
    launchContext:=New List(Of LaunchContext(Of Resource)) From {
        New LaunchContext(Of Resource)("coverage", contentResource:=coverage)
    })
```

## Shipping your own index.html

The library ships a working default `index.html` so the samples run out-of-the-box, but for production you'll want to host your own page. The bridge, the SMART Web Messaging plumbing, **and the `tiro-web-sdk` itself** are auto-injected by the host (regardless of which page is loaded), so your `index.html` stays purely branding — no SDK script tag, no SDK init, no transport setup, no Sentry CDN tag.

Only one thing is non-negotiable: a `<tiro-form-filler id="form-filler">` element. Everything else (endpoints, `questionnaire`, `launch-context`) is applied at runtime by the host — don't bake it into the page. Do **not** add a `tiro-web-sdk` `<script>` tag: the harness embeds and serves the exact SDK version it was validated against (see [Frontend version compatibility](#frontend-version-compatibility)); a page-loaded copy would collide with it and is reported as an error.

1. Start from this minimal starter template. (You can also get it by running any sample and clicking **Copy starter template** in the default page's yellow banner, but the canonical copy is right here — no need to run anything.)
   ```html
   <!DOCTYPE html>
   <html lang="en">
   <head>
       <meta charset="UTF-8">
       <title>Tiro Form Filler</title>
       <!-- No SDK script tag: the harness embeds the validated tiro-web-sdk and the
            bridge injects it (GH-60). Do not add one — the page is branding only. -->
       <style>
           html, body { margin: 0; height: 100%; }
           tiro-form-filler { display: block; height: 100%; }
       </style>
   </head>
   <body>
       <tiro-form-filler id="form-filler"></tiro-form-filler>
   </body>
   </html>
   ```
   For fuller, checked-in examples, see the samples: `ExtractSample/WebContent/index.html` (a lightly-branded single page) and the EhrShell sample's `WebContent/Form/index.html`. Note you don't need a second page for read-only viewing — set the viewer's `ReadOnly` property instead (see [Configuring FHIR endpoints from the host](#configuring-fhir-endpoints-from-the-host)).
2. Save it into your project, e.g. `WebContent/index.html`, and tweak it — branding, status copy, etc. Endpoints are configured from the .NET host (see [Configuring FHIR endpoints from the host](#configuring-fhir-endpoints-from-the-host) below) — don't hardcode them in the page. If the page sets a Content-Security-Policy, `script-src` must allow the SDK's serving origin `https://tiro-sdk.example` (and `frame-src`/defaults per your policy).
3. Mark the file(s) as content in your `.vbproj` / `.csproj` so they ship next to the executable:
   ```xml
   <ItemGroup>
     <Content Include="WebContent\**\*">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </Content>
   </ItemGroup>
   ```
4. Point `WebContentFolder` at the deployed folder. It's read once, at the first `SetContextAsync` call — the point the viewer navigates to your page — so set it any time **before** that call; setting it afterwards has no effect. Both of these are safe (there's no init-timing race — navigation is deferred until `SetContextAsync` reads the property):

   - **Object initializer** — natural when you build the viewer in code, as the EhrShell sample does:
     ```vb
     Private ReadOnly TiroFormViewer As New TiroFormViewerR5() With {
         .Dock = DockStyle.Fill,
         .WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent")
     }
     ```
   - **`Form_Load`, before `SetContextAsync`** — convenient for a Designer-placed viewer, and what the Sample and ExtractSample do:
     ```vb
     Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
         TiroFormViewer.WebContentFolder = Path.Combine(AppContext.BaseDirectory, "WebContent")
         ' ... AddHandler / build patient / SetContextAsync ...
     End Sub
     ```

## Telemetry

The telemetry abstraction ships **inside `Tiro.Health.FormFiller.WebView2`** (namespace `Tiro.Health.FormFiller.WebView2.Telemetry`) and carries no telemetry *backend* dependency — so a consumer that wants no telemetry pulls no Sentry NuGet. Telemetry is plugged in via `ITelemetrySink`:

```vb
Public Interface ITelemetrySink
    Inherits IDisposable

    Function BeginSession(sessionId As String) As ITelemetrySession
    Sub CaptureException(ex As Exception)
    Sub Flush(timeout As TimeSpan)
End Interface
```

A session (`ITelemetrySession`) starts transactions; each transaction is an `ITelemetrySpan`. Spans are `IDisposable` — a `Using` block finishes the span with `Ok` on scope exit unless an explicit `Finish` (e.g. on a failure path) already ran.

The FHIR-version closed bindings (`TiroFormViewerR5`/`R4`) default to `NullTelemetrySink` (no-op): the `.Sentry` adapter is **not** a transitive dependency, so by default no Sentry NuGet, no SDK init, no `Sentry.init` on the embedded page.

To **opt in to Sentry telemetry**, add the adapter package and call `TiroFormFillerSentry.UseSentry()` **once at application startup** — before any form containing a viewer is constructed:

```xml
<PackageReference Include="Tiro.Health.FormFiller.WebView2.Sentry" Version="..." />
```

```vb
' My.MyApplication.Startup handler (or Sub Main):
Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
    TiroFormFillerSentry.UseSentry()
End Sub
```

#### Where this code lives

VB.NET WinForms apps come in two flavors. Pick the one that matches your project:

**1. `My.MyApplication` framework (the default Visual Studio WinForms template)**

Your `.vbproj` has `<MyType>WindowsForms</MyType>` and `<StartupObject>YourApp.My.MyApplication</StartupObject>`. There's no `Sub Main` — the framework calls `Application.Run` for you. The `Startup` event fires after `My.MyApplication` is constructed but before `Application.Run`, which is before any form (and therefore any `TiroFormViewer`) exists. Drop the handler into `My Project\ApplicationEvents.vb`:

```vb
Imports Microsoft.VisualBasic.ApplicationServices
Imports Tiro.Health.FormFiller.WebView2.Sentry

Namespace My
    Partial Friend Class MyApplication
        Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
            TiroFormFillerSentry.UseSentry()
        End Sub
    End Class
End Namespace
```

The quickest way to create this file: right-click the project → **Properties** → **Application** tab → **View Application Events** button. Visual Studio creates `ApplicationEvents.vb` under `My Project\` and adds the `<Compile>` entry to your `.vbproj` for you. If you hand-create the file, also add this to the project's `<Compile>` group:

```xml
<Compile Include="My Project\ApplicationEvents.vb" />
```

**2. Explicit `Sub Main` (used by the `EhrShellSample`)**

Your `.vbproj` has `<StartupObject>YourApp.Program</StartupObject>` (or similar) pointing at a module with `<STAThread> Sub Main()`. Call `UseSentry()` at the top of `Sub Main`, before `Application.Run`:

```vb
Module Program
    <STAThread>
    Public Sub Main()
        TiroFormFillerSentry.UseSentry()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New MainForm())
    End Sub
End Module
```

---

That's it. Every Designer-placed `TiroFormViewerR5` / `TiroFormViewerR4` in your application — anywhere in the codebase — picks up the configured sink at construction. No `Form_Load` code, no per-form awareness.

> ⚠ **Ordering matters.** `UseSentry` registers a process-global factory consulted by `TiroFormViewer` at construction time. Call it before the first form containing a viewer is shown. Viewers constructed before `UseSentry` runs do not retroactively pick it up.

The zero-arg call uses **Tiro's hosted DSNs** — host telemetry to `tirohealth/dotnet-winforms`, embedded-page telemetry to `tirohealth/javascript` (same Sentry org, unified trace view). This is the **recommended** path during integration: the Tiro team can see your form sessions and help diagnose issues quickly. The defaults are designed to be safe to ship — **no FHIR payloads are attached to spans**, so PHI does not flow to Sentry. What you do get:

- **One Sentry transaction per round-trip message** (e.g. `sdc.displayQuestionnaire`, `form.submitted`) — actual request/response latency, not just the `PostMessage` cost
- **One unified trace per form session** spanning both .NET and JS Sentry projects (the host injects its `traceId` into the embedded page; the JS Sentry SDK continues that trace)
- **`form.session.id` tag** + **`messageType` tag** on every transaction for cross-project correlation
- **`questionnaire_url` tag** on `sdc.displayQuestionnaire` — the canonical URL of the form, not patient data
- **Lifecycle breadcrumbs** for construction / handshake / dispose
- **Outcome-aware status** on the `form.submitted` transaction (Sentry `Ok` on success, `InvalidArgument` on validation failures)
- **Exceptions** captured via `SentrySdk.CaptureException` — the .NET-side exception type, message, and stack trace (these typically don't carry PHI; if your application code surfaces patient identifiers in exception messages, you'd want to scrub them before they bubble up)
- **Release tag** auto-derived from the FormFiller assembly's `AssemblyInformationalVersion` (`Tiro.Health.FormFiller.WebView2@<semver>+<commit>`)

To **redirect to your own Sentry project** instead, pass your DSN (and optionally environment/release, or a full `SentryOptions`):

```vb
TiroFormFillerSentry.UseSentry(dsn:="https://...@your-org.ingest.sentry.io/...")
' or:
TiroFormFillerSentry.UseSentry(dsn:="...", environment:="staging", release:="myapp@1.2.3")
```

For any other backend, implement `ITelemetrySink` yourself and register it directly:

```vb
TiroFormViewerDefaults.TelemetrySinkFactory = Function() New MyCustomTelemetrySink()
```

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

`SdcEndpointAddress` is seeded from the closed binding's `DefaultSdcEndpointAddress` (`TiroFormViewerR5.DefaultSdcEndpointAddress` = `https://sdc.tiro.health/fhir/r5`) so out-of-the-box demos work without configuration. `DataEndpointAddress` has no default. Either property must be set **before** `SetContextAsync` (the bridge reads them once, when the page is first wired).

### Rendering a form read-only

`ReadOnly` renders the form view-only — no answer can be changed. Set it before `SetContextAsync`, like the endpoint properties:

```vb
TiroFormViewer.ReadOnly = True
Await TiroFormViewer.SetContextAsync(templateUrl, patient, initialResponse:=savedResponse)
```

It travels to the page on the same `sdc.configure` message as the endpoints, so the bridge applies it before the form initializes — a read-only launch never paints an editable form first. This is the supported way to show a validated or archived report; you do **not** need a second `index.html` with the `read-only` attribute baked in.

Two limits worth knowing:

- **Per session, not per moment.** `ReadOnly` is read once when the `sdc.configure` payload is built, so a viewer can't be flipped between editable and view-only mid-session — use one viewer per role (the EhrShell sample opens its read-only consultation in a separate window with its own viewer). You don't need to do anything for the post-submit case: the form component locks itself once a response has been submitted.
- **Rich-text fields aren't hard-locked yet.** For `rich-text` items the frontend currently suppresses editing with CSS rather than disabling the editor outright, so read-only there is a strong deterrent rather than an enforced guarantee. Don't rely on it as the only control if immutability is a legal requirement.

> **R4 routes to the R5 endpoint today.** `TiroFormViewerR4.DefaultSdcEndpointAddress` also points at `/fhir/r5` — Tiro doesn't yet host a dedicated R4 SDC server. The R5 endpoint round-trips most R4 questionnaire content fine for development and demos, but resource shapes that diverge between versions can be coerced silently. R4 consumers running anything beyond exploration should override `SdcEndpointAddress` with their own R4-hosting SDC server.

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
│   ├── Tiro.Health.FormFiller.WebView2/            # WinForms UserControl + bridge JS + telemetry seam (FHIR-agnostic)
│   ├── Tiro.Health.FormFiller.WebView2.Fhir.R5/    # Designer-friendly R5 viewer
│   ├── Tiro.Health.FormFiller.WebView2.Fhir.R4/    # Designer-friendly R4 viewer
│   ├── Tiro.Health.FormFiller.WebView2.Sentry/     # Sentry-backed ITelemetrySink adapter
│   ├── Tiro.Health.FormSdk.Client/                 # Typed SDC FHIR client core ($validate/$extract)
│   └── Tiro.Health.FormSdk.Client.Fhir.R5/         # SDC client — FHIR R5 closed binding
├── samples/
│   ├── Tiro.Health.FormFiller.WebView2.Sample/         # Single-form, single-patient demo — shows the response narrative (R5)
│   ├── Tiro.Health.FormFiller.WebView2.ExtractSample/  # Like Sample, but runs SDC $extract and shows the extracted Composition narrative (R5)
│   └── Tiro.Health.FormFiller.WebView2.EhrShellSample/ # Dummy EHR shell — patient/encounter/template selection,
│                                                       # tabbed viewer, in-memory QR persistence, custom index.html (R5)
└── tests/
    ├── Tiro.Health.SmartWebMessaging.Tests/        # MSTest, protocol/handler coverage
    ├── Tiro.Health.FormFiller.WebView2.Tests/      # MSTest, viewer lifecycle + telemetry contracts + embedded assets
    └── Tiro.Health.FormSdk.Client.Tests/           # MSTest, SDC client $validate/$extract over a fake HttpMessageHandler
```

### `Tiro.Health.SmartWebMessaging` (core)
FHIR-version-agnostic implementation of the SMART Web Messaging protocol.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandlerBase<TResource, TQuestionnaireResponse, TOperationOutcome>` — abstract generic handler covering protocol routing, request/response correlation via `Func<SmartMessageResponse, Task>` listeners, and `CancellationToken` plumbing across the entire async surface
- **Handles**: `status.handshake`, `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, `form.submitted`, `ui.form.requestSubmit`, `ui.form.persist`, `ui.done`, `ui.form.dirtyChanged`
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
- **Telemetry seam** (namespace `Tiro.Health.FormFiller.WebView2.Telemetry`): `ITelemetrySink` (begins sessions, captures exceptions, flushes), `ITelemetrySession` (starts transactions in one trace), `ITelemetrySpan` (`IDisposable`; transactions and child spans), `TelemetrySpanStatus`, and `NullTelemetrySink` (the no-op default). No backend dependency — the Sentry-backed implementation ships in `Tiro.Health.FormFiller.WebView2.Sentry`; implement the interfaces yourself for any other backend.
- **Features**:
  - Explicit lifecycle state machine (`TiroFormViewerState`: Initializing → Ready → ContextSet → Submitted → Disposed)
  - Async API with `CancellationToken` end-to-end; in-flight operations cancel cleanly on disposal
  - Pluggable `IEmbeddedBrowser` seam for testability (default: `WebView2EmbeddedBrowser`)
  - Pluggable `ITelemetrySink` seam (default: `NullTelemetrySink`); see telemetry section below
  - Embeds `WebAssets/tiro-swm-bridge.js` and auto-injects it into every page via WebView2's `AddScriptToExecuteOnDocumentCreatedAsync` — page is UI-only
  - Optional consumer-supplied `WebContentFolder` for hosting your own `index.html`; the shipped one is a working sample with a visible banner prompting integrators to override it for production
  - Host-configured `<tiro-form-filler>` endpoints via `SdcEndpointAddress` / `DataEndpointAddress`; the bridge applies them on the page so the .NET host and embedded JS always agree on which FHIR servers to hit
  - Host-configured view-only rendering via `ReadOnly`, applied before the form initializes so no second `index.html` is needed for read-only roles

### `Tiro.Health.FormFiller.WebView2.Fhir.R5` / `Tiro.Health.FormFiller.WebView2.Fhir.R4`
Designer-friendly closed bindings of `TiroFormViewer<,,>`.

- **Targets**: `net48`
- **Key type**: `TiroFormViewerR5` / `TiroFormViewerR4` (sealed) — drop-in WinForms control
- **Defaults**: telemetry → `NullTelemetrySink` (no-op). Opt in to Sentry by referencing `Tiro.Health.FormFiller.WebView2.Sentry` and calling `TiroFormFillerSentry.UseSentry()` once at application startup — see [Telemetry](#telemetry)

### `Tiro.Health.FormFiller.WebView2.Sentry`
Sentry-backed `ITelemetrySink` adapter. Optional: only depend on this if you want the Sentry behaviour.

- **Targets**: `net48`
- **Key types**:
  - `TiroFormFillerSentry.UseSentry(...)` — one-line startup hook. Initializes the Sentry SDK and registers the global telemetry factory consulted by every viewer. Overloads for zero-args (Tiro defaults), a custom DSN, DSN + environment + release, or a full `SentryOptions`.
  - `SentryTelemetrySink` — the underlying `ITelemetrySink` implementation. Owns two DSNs (one for the .NET host process, one injected into the embedded page) plus environment and release. Use directly only when registering with `TiroFormViewerDefaults.TelemetrySinkFactory` by hand or implementing a custom adapter chain.
- Auto-detects release as `Tiro.Health.FormFiller.WebView2@<version>+<commit>` from the FormFiller assembly's `AssemblyInformationalVersion` (so traces deep-link to source via Sentry's release pipeline if you upload symbols)

### `Tiro.Health.FormSdk.Client` (core) / `Tiro.Health.FormSdk.Client.Fhir.R5`
Thin, strongly-typed client over the **stateless SDC server** FHIR operations — call them directly instead of hand-building request bodies and parsing raw responses. A separate concern from the messaging/viewer packages (an HTTP/FHIR client, not an embedded-UI bridge); depend on it only if your host calls the SDC server itself.

- **Targets**: `netstandard2.0`, `net48`
- **Key types**: `SdcClientBase<TQuestionnaireResponse, TOperationOutcome, TBundle>` (core, FHIR-version-agnostic) and `SdcClient` (R5 closed binding)
- **Operations**: `ValidateAsync(qr)` → `OperationOutcome` (`QuestionnaireResponse/$validate`); `ExtractAsync(qr)` → transaction `Bundle` (`QuestionnaireResponse/$extract`)
  - `$extract` runs the questionnaire's SDC extraction over a completed `QuestionnaireResponse` and returns a transaction `Bundle` — the resources the answers produce, plus the source QR and a `Provenance` linking them. What's extracted depends on the questionnaire: Tiro's **template-based** questionnaires yield a `Composition` whose sections carry the rendered narrative, while **definition-based** questionnaires yield structured resources (e.g. `Observation`). The stateless endpoint computes the Bundle without persisting it.
- **Construction**: `new SdcClient(new Uri("https://host/fhir/r5"), httpClient?)` — inject a pre-configured `HttpClient` for custom TLS/proxy/timeouts. The client deliberately has **no default base** — you must pass one.
- **Point the client at the same SDC server as the form.** `baseAddress` here and the viewer's `SdcEndpointAddress` ([`TiroFormViewer`](#tirohealthformfillerwebview2)) are the *same* concept — the SDC server. A host that embeds the form **and** calls `$validate`/`$extract` directly should **construct the client from `viewer.SdcEndpointAddress`** (see [Extracting after a form submit](#extracting-after-a-form-submit)) so the two can't drift apart. Note this is a **convention, not an enforced guarantee** — nothing in the API stops you from pointing the form and the client at different servers, so derive the client's address from the viewer rather than configuring it separately.
- **Behaviour**: thin over Firely's serializer + `HttpClient` (POSTs a bare `QuestionnaireResponse`, the shape the SDC server expects). A validation failure comes back as `OperationOutcome` issues; transport/server errors (non-2xx) throw `SdcOperationException`. Responses are parsed in Firely's *recoverable* mode, so a `200` carrying an element/code a newer server emits that this Firely version doesn't recognize is still returned (partial POCO) rather than failing
- **Telemetry-free** — the client takes no telemetry seam; it's a pure HTTP/FHIR client. If you want a span around a call, wrap it at the call site with a session you own — `Using session.StartTransaction("sdc.extract", "http.client") : Await client.ExtractAsync(qr) : End Using` — where `session` is any `ITelemetrySession` you create (e.g. from a sink via `BeginSession`). Keeping telemetry out of the client avoids coupling its lifetime to a session's
- **R5-only**: these SDC operations exist only on `/fhir/r5`, so there is no R4/R5 split — a future R4 server would be one new `.Fhir.R4` binding. `$populate` is tracked separately (#29)

#### Extracting after a form submit

A host that embeds a viewer **and** wants the extraction `Bundle` constructs the client from the viewer's own `SdcEndpointAddress`, so the extract targets the same SDC server as the rendered form. (Deriving the address from the viewer this way is the convention that keeps them in sync — see the note above.) Foreground — `await`, then close:

```vb
Imports Tiro.Health.FormSdk.Client            ' SdcOperationException
Imports Tiro.Health.FormSdk.Client.Fhir.R5    ' SdcClient

Private Async Sub HandleFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
    Try
        Using client As New SdcClient(New Uri(TiroFormViewer.SdcEndpointAddress))
            Dim bundle As Bundle = Await client.ExtractAsync(e.Response)
            ' persist / inspect bundle ...
        End Using
    Catch ex As SdcOperationException
        MessageBox.Show($"Extraction failed: {ex.Message}")
    End Try
    Me.Close()
End Sub
```

To extract in the **background** and let the form close immediately, build the client in the synchronous part (before `Me.Close()`), then await it in a tracked task you drain on shutdown:

```vb
Private ReadOnly _pending As New List(Of Task)()

Private Sub HandleFormSubmitted(sender As Object, e As FormSubmittedEventArgs(Of QuestionnaireResponse, OperationOutcome))
    Dim client As New SdcClient(New Uri(TiroFormViewer.SdcEndpointAddress))   ' synchronous — capture before close
    _pending.Add(ExtractInBackground(client, e.Response))
    Me.Close()
End Sub

Private Async Function ExtractInBackground(client As SdcClient, qr As QuestionnaireResponse) As Task
    Try
        Using client
            Dim bundle As Bundle = Await client.ExtractAsync(qr)
            ' persist bundle ...
        End Using
    Catch ex As Exception
        ' nobody awaits this Task — log/report here
    End Try
End Function
```

Drain `_pending` on app exit (`Await Task.WhenAll(_pending)` with a timeout) so an in-flight extract isn't lost. `e.Response` is fully self-contained, so the viewer closing doesn't affect either path.

### `Tiro.Health.FormFiller.WebView2.Sample` / `ExtractSample` / `EhrShellSample`
WinForms demos.

- **`Sample`** — single-form, single-patient demo bound to FHIR **R5**. The smallest possible "see the API working" reference: native Submit button, default `index.html`, no persistence. Shows the submitted QR's XHTML narrative (`Text.Div`) in a `MessageBox` — for a richer rendering or the plain-text alternative-format extension, see the `EhrShellSample`'s `QuestionnaireResponseHelper`.
- **`ExtractSample`** — same shape as `Sample`, but instead of showing the response narrative it demonstrates the **SDC `$extract` client**. On submit it constructs an `SdcClient` from the viewer's `SdcEndpointAddress` (so the extract targets the same SDC server the form rendered against), runs `$extract` over the completed QR to get the transaction `Bundle` of structured resources, pulls the `Composition` out of the Bundle, and shows its narrative (`Composition.Text.Div`) in a `MessageBox`. Falls back to a Bundle summary if the questionnaire extracts non-Composition resources (e.g. definition-based `Observation`s).
- **`EhrShellSample`** — dummy EHR shell bound to FHIR **R5**. Demonstrates the integration patterns a real EHR is going to need:
  - **Practitioner identity** (top status strip) passed through as the `author` in `LaunchContext`.
  - **Patient / encounter / template selection** — three hardcoded patients with their own encounters in the left sidebar; three canonical templates verified live on the default SDC server, picked via a modal `TemplatePickerDialog` from the **+ New report** button.
  - **Reports list per patient** — every saved `QuestionnaireResponse` (finalized or draft) is stored in an in-memory `ResponseStore` keyed by a stable **report id** and shown newest-first in the Patient details tab. **+ New report** mints a fresh id, so it's always a distinct report; reopening one to edit reuses its id, so resubmitting updates that report in place rather than creating a duplicate.
  - **Save in progress** — a footer button alongside **Submit** that calls `SendFormRequestSubmitAsync(intent:="save-draft")` (maps to the frontend's `submit({ status: "in-progress" })`; requires `tiro-web-sdk >= 0.3.0`). The draft round-trips back with status `in-progress`; the shell persists it (so it's resumable from the reports list) but **keeps the session alive** so the doctor can keep filling — distinguished from a finalized Submit (status `completed`, which ends the session) by inspecting `e.Response.Status` in the `FormSubmitted` handler.
  - **Read-only narrative preview** — single-clicking a saved report renders its narrative in a `RichTextBox` (RTF when the SDC's `$generate-narrative` produced one, plain-text fallback otherwise — both via `QuestionnaireResponseHelper`). The preview is decoupled from session state, so the doctor can peek at older reports while a form is in progress.
  - **Reopen a report — edit or read-only** — double-clicking a report (or **Open this report**) prompts how to open it. **Edit** resumes filling it in the main shell's Form tab with the saved QR as `initialResponse`, reusing its report id (blocked while another session is live, to avoid orphaning the active viewer). **Read-only** spawns a separate top-level `ReportConsultationForm` with its own `TiroFormViewerR5` — leaving any live session untouched (showcasing that multiple viewer instances coexist) — setting `ReadOnly = True` so the form renders view-only off the *same* `WebContent/Form/index.html` the editable session uses.
  - **Tabbed embedding with dynamic Form tab** — the Form tab only exists while a session is alive (added to / removed from `TabControl.TabPages` on launch / dispose). A context banner above the form viewer shows what's being filled. Switching tabs while filling *hides* the WebView2 (state preserved, JS keeps running, messages still route); explicit **Close session** button *disposes* it (state gone, viewer recreated next launch). Showcases the hide-vs-dispose contrast.
  - **Custom `index.html` + host-side role config** — bundles a single `WebContent/Form/index.html`, shared by the editable session and the read-only consultation window. Illustrates where the line sits: the host API owns what the EHR is authoritative about (endpoints, launch context, `ReadOnly`), the page owns static presentation (branding, `auto-collapse` / `compact-grouping` / `density-mode`). Roles that differ only in editability share one page instead of forking it. See [Shipping your own index.html](#shipping-your-own-indexhtml) and [Rendering a form read-only](#rendering-a-form-read-only).
- All three: `.NET 4.8` (VB.NET, old-style project format).

### `Tiro.Health.SmartWebMessaging.Tests` / `Tiro.Health.FormFiller.WebView2.Tests` / `Tiro.Health.FormSdk.Client.Tests`
- **Targets**: `net8.0` (SmartWebMessaging) / `net48;net8.0` (FormSdk.Client — multi-targeted so the net48 build the libraries ship is also exercised) / `net48` (FormFiller — needs WinForms + WebView2)
- **Framework**: MSTest + Moq
- **Coverage**:
  - `SmartWebMessaging.Tests` — protocol routing, request/response correlation, payload validation (including `form.submitted` `[Required]` enforcement), event firing, JSON probe, async-task extensions
  - `FormFiller.WebView2.Tests` — viewer lifecycle (state machine transitions, dispose semantics), telemetry sink contracts (`NullTelemetrySink` no-ops, span ordering, session tagging), embedded `WebAssets/` resource integrity
  - `FormSdk.Client.Tests` — SDC `$validate`/`$extract` over a fake `HttpMessageHandler`: typed result parsing, validation-issues-without-throw, non-2xx → `SdcOperationException`, and a guard that the request body is a bare `QuestionnaireResponse`

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

- **`<tiro-form-filler>`** (from the auto-injected `tiro-web-sdk`) — auto-wired by the bridge: questionnaires arrive via the `questionnaire` attribute, user submissions come back via the `tiro-submit` event, and the bridge takes care of marshalling them onto the protocol.
- **`window.tiro.cancel()`** — call from a Cancel button to send `ui.done` to the host.
- **`document` `CustomEvent`s** for status hooks: `tiro-connected`, `tiro-disconnected`, `tiro-submitted`, `tiro-submit-error`, `tiro-cancelled`, plus `tiro-sdk-error`/`tiro-sdk-collision` for SDK-loading problems. Listen if you want a status bar; ignore if you don't.
- **`window.SmartWebMessaging.{sendRequest, sendEvent, on}`** — lower-level API for advanced flows that don't fit the auto-wired form-filler model.

The page carries **no** `tiro-web-sdk` script tag — the harness injects the SDK itself (next section).

### Frontend version compatibility

The harness **embeds** the exact `tiro-web-sdk` version it was validated against (pinned in `build/web-sdk/package.json`) and serves it to the page itself — there is no SDK version to choose, in the page or anywhere else. Bridge and element ship and are CI-validated as one pair, so the historical skew hazards are gone by construction:

- **Save-draft** (`SendFormRequestSubmitAsync(intent: "save-draft")`) needs `submit({ status })` (web-sdk >= 0.3.0) — the embedded SDK satisfies this. On the old page-pinned model, an older SDK silently **finalized** instead of saving a draft; that failure mode can no longer occur.
- **`TiroFormViewer.IsDirty`/`FormDirtyChanged`** needs [`isDirty`/`tiro-dirty-change`](https://github.com/Tiro-health/atticus-frontend/issues/2831) (web-sdk >= 0.3.2) — likewise satisfied by the embedded SDK.

A page that still loads its own `tiro-web-sdk` copy collides with the embedded one: the bridge skips injection, logs an error, and fires `tiro-sdk-collision` — remove the script tag. The `build/bridge-contract/` type-check gates every PR and release against the pinned version; the version story for integrators is one line: **pin the harness NuGet, done**.

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
