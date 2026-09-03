# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2. Specifically targets the [SDC SMART Web Messaging protocol](https://github.com/brianpos/sdc-smart-web-messaging) — the dialect of SMART Web Messaging that defines `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, and `form.submitted` for embedding SDC questionnaire renderers in EHR shells.

Embed FHIR-based questionnaire forms in a WebView2 control and exchange `QuestionnaireResponse` data with them over the SMART Web Messaging protocol. The host control owns the protocol, transport, and (optional) telemetry; the embedded HTML page is purely UI — it does not need to know about SMART Web Messaging, Sentry, or WebView2 at all. The bridge JS that drives the page is bundled with the host library and auto-injected before any page script runs.

## Getting started

These libraries ship as NuGet packages and are typically consumed from a WinForms app on .NET Framework 4.8.

### 1. Reference the packages

There is no umbrella `net-integration-harness` package. In Visual Studio, right-click your project → **Manage NuGet Packages...** → **Browse** tab → install:

- **`Tiro.Health.FormFiller.WebView2.Fhir.R5`** (or `.Fhir.R4` for an R4 consumer) — the closed-binding control. Pulls in the messaging core, the WebView2 host, and `Hl7.Fhir.*` transitively.
- *(optional)* **`Tiro.Health.FormFiller.WebView2.Sentry`** — Sentry-backed telemetry adapter. Only if you want telemetry; see [Telemetry](#telemetry).

That's it — two top-level package references. Everything else (`Tiro.Health.SmartWebMessaging`, `Tiro.Health.SmartWebMessaging.Fhir.*`, `Tiro.Health.FormFiller.WebView2`, `Tiro.Health.FormSdk.Abstractions`, `Hl7.Fhir.Base`, `Hl7.Fhir.R5`/`R4`, `Hl7.Fhir.Conformance`, etc.) comes through transitively.

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

And a fifth worth wiring in production: `PageError` — the page **rejected** one of the host's requests (its handler threw, or it didn't recognise the message type). A send completes once the message is posted, so without this a refused request looks successful; the failure is also captured to telemetry. `PageErrorEventArgs` carries the message type, the page's error type and message, and the id of the rejected request. Raised on the UI thread.

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
   For fuller, checked-in examples, see the samples: `ExtractSample/WebContent/index.html` (a lightly-branded page that also hosts the [Magic Clipboard](#ai-autofill-with-the-magic-clipboard) next to the form) and the EhrShell sample's `WebContent/Form/index.html`. Note you don't need a second page for read-only viewing — set the viewer's `ReadOnly` property instead (see [Configuring FHIR endpoints from the host](#configuring-fhir-endpoints-from-the-host)).
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

The sample's own `Program.vb` registers Sentry **and** a local transcript rather than this one-liner —
see [When Sentry can't leave the hospital network](#when-sentry-cant-leave-the-hospital-network). The
placement is what matters here, not which sink you pick.

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

### When Sentry can't leave the hospital network

`FileTelemetrySink` writes a rolling JSONL transcript of every session to local disk. It ships **in the core package** — no Sentry NuGet, no network path — for sites whose intranet blocks egress to `ingest.de.sentry.io`. The failure it exists for is a quiet one: the Sentry .NET SDK drops transport failures silently, so a blocked DSN and a healthy one look identical from inside the process, and nobody finds out until support asks for a trace that was never sent.

```vb
' Air-gapped: file only.
TiroFormViewerDefaults.TelemetrySinkFactory =
    Function() New FileTelemetrySink()

' Both. The file always works; Sentry works when the network allows it.
TiroFormViewerDefaults.TelemetrySinkFactory =
    Function() New FileTelemetrySink(FileTelemetrySink.DefaultDirectory, New SentryTelemetrySink())
```

This is not an either/or with `UseSentry()`. Wrapping a `SentryTelemetrySink` keeps every Sentry behaviour described above — the embedded page's DSN, the unified trace, all of it — and adds a local copy; passing no inner sink gives you the file alone. Prefer wrapping to choosing, since you generally can't tell from outside whether a given site's egress works.

To change where it writes, how long it keeps, or how large files get, pass a `FileTelemetryOptions`:

```vb
TiroFormViewerDefaults.TelemetrySinkFactory =
    Function() New FileTelemetrySink(New FileTelemetryOptions With {
        .Directory = "D:\HospitalLogs\TiroFormFiller",
        .RetentionDays = 30,
        .MaxBytesPerFile = 1024L * 1024L
    }, New SentryTelemetrySink())
```

#### The transcript

One file per day, in `%LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry` unless you set `Directory`:

```
20260828.jsonl
```

Every viewer in the process writes to the same file — the log is shared and reference-counted, so two forms open at once don't fight over it and the first one closed doesn't take it from the others. Sessions are delimited by `session.start` / `session.end` records rather than by separate files, which is what makes bugs that span sessions visible at all. `FileTelemetrySink.CurrentFilePath` gives you the path, which is what an *Attach diagnostics* button should use rather than reconstructing a name.

A real capture, verbatim, from the e2e suite's stage C — one form session on a Windows runner
against a live SDC server, see [tests/e2e/README.md](tests/e2e/README.md). Every run uploads one
as an artifact:

```
{"type":"header","ts":"2026-08-28T12:51:31.082Z","sid":"process","v":1,"file_schema":"tiro-formfiller-telemetry-jsonl","host":"runnervmeef0v","pid":1948}
{"type":"session.start","ts":"2026-08-28T12:51:31.099Z","sid":"d6f21f64","session":"d6f21f64-49f8-4c37-80cc-cadca6042c0a","release":"Tiro.Health.FormFiller.WebView2@1.0.0+1d0dedbf962634084006fa63010ce31df692e746"}
{"type":"crumb","ts":"2026-08-28T12:51:31.100Z","sid":"d6f21f64","cat":"lifecycle","msg":"TiroFormViewer constructed"}
{"type":"span.start","ts":"2026-08-28T12:51:31.132Z","sid":"d6f21f64","span":"742fc8a5","parent":null,"name":"Initialize WebView","op":"swm.lifecycle.init"}
{"type":"span.start","ts":"2026-08-28T12:51:31.876Z","sid":"d6f21f64","span":"d9c34f79","parent":null,"name":"sdc.displayQuestionnaire","op":"swm.send"}
{"type":"span.tag","ts":"2026-08-28T12:51:31.876Z","sid":"d6f21f64","span":"d9c34f79","k":"messageType","v":"sdc.displayQuestionnaire"}
{"type":"span.tag","ts":"2026-08-28T12:51:31.878Z","sid":"d6f21f64","span":"d9c34f79","k":"questionnaire_url","v":"http://templates.tiro.health/templates/23030f2f048445af9ab171a7e4222699|1.0.0"}
{"type":"span.end","ts":"2026-08-28T12:51:32.963Z","sid":"d6f21f64","span":"742fc8a5","status":"ok","ms":1825}
{"type":"span.start","ts":"2026-08-28T12:51:33.665Z","sid":"d6f21f64","span":"52bae698","parent":null,"name":"status.handshake","op":"swm.receive"}
{"type":"span.tag","ts":"2026-08-28T12:51:33.665Z","sid":"d6f21f64","span":"52bae698","k":"messageType","v":"status.handshake"}
{"type":"crumb","ts":"2026-08-28T12:51:33.845Z","sid":"d6f21f64","cat":"lifecycle","msg":"Handshake received (tiro-web-sdk 0.3.3)"}
{"type":"span.start","ts":"2026-08-28T12:51:33.862Z","sid":"d6f21f64","span":"e36c2668","parent":"52bae698","name":"response","op":"swm.send"}
{"type":"span.end","ts":"2026-08-28T12:51:33.862Z","sid":"d6f21f64","span":"e36c2668","status":"ok","ms":0}
{"type":"span.end","ts":"2026-08-28T12:51:33.862Z","sid":"d6f21f64","span":"52bae698","status":"ok","ms":197}
{"type":"crumb","ts":"2026-08-28T12:51:33.866Z","sid":"d6f21f64","cat":"sdc.version","msg":"SDC server version v0.9.40-rc.0 satisfies the minimum v0.9.39 (read from CapabilityStatement.software.version)."}
{"type":"span.start","ts":"2026-08-28T12:51:33.866Z","sid":"d6f21f64","span":"724c0e90","parent":null,"name":"sdc.configure","op":"swm.send"}
{"type":"span.tag","ts":"2026-08-28T12:51:33.866Z","sid":"d6f21f64","span":"724c0e90","k":"messageType","v":"sdc.configure"}
{"type":"span.tag","ts":"2026-08-28T12:51:33.866Z","sid":"d6f21f64","span":"724c0e90","k":"sdc_server","v":"https://sdc-staging.tiro.health/fhir/r5"}
{"type":"span.end","ts":"2026-08-28T12:51:34.028Z","sid":"d6f21f64","span":"d9c34f79","status":"cancelled","ms":2150}
{"type":"span.end","ts":"2026-08-28T12:51:34.028Z","sid":"d6f21f64","span":"724c0e90","status":"cancelled","ms":160}
{"type":"crumb","ts":"2026-08-28T12:51:34.029Z","sid":"d6f21f64","cat":"lifecycle","msg":"TiroFormViewer disposed"}
{"type":"session.end","ts":"2026-08-28T12:51:34.030Z","sid":"d6f21f64"}
```

That's 22 records for one form session — 3,054 bytes. One line per event, flat keys, `sid` on every line: readable in Notepad on a locked-down box with no `jq`, and greppable by anything else.

Reading that one: `e36c2668` has `"parent":"52bae698"`, so it is a child span of the `status.handshake` transaction — that is how nesting appears. The two `"status":"cancelled"` spans are what a viewer disposed mid-send looks like, which here means a form closed while it was still loading. `release` carries the build's commit, so a transcript names the exact binary that wrote it.

**To pull one session out of a day:**

```
findstr d6f21f64 20260828.jsonl        REM Windows
grep    d6f21f64 20260828.jsonl        # anywhere else
```

Record types: `header` (once per file open), `session.start` / `session.end`, `crumb`, `tag`, `span.start` / `span.tag` / `span.extra` / `span.end`, `error`, `message`, `inner.error`, and `trunc`. Reading it:

| What you see | What it means |
|---|---|
| `session` on `session.start` | the full `form.session.id`. Paste it into Sentry to find the same session there — the record also carries `trace` when an inner Sentry sink supplied one, so the file and the Sentry trace are the *same* trace |
| `span.start` with no matching `span.end` | the viewer was still waiting. Start records exist for exactly this: a span that never finishes is the failure you most want the file for, and it would otherwise write nothing at all |
| `session.end` | that session ended. Records after it belong to the process, not the session, and carry `sid":"process"` |
| `"repeat":true` on a `span.end` | a second `Finish` the real span correctly ignored under first-finish-wins. The transcript records what the caller *asked for*, which is how you find out why a trace shipped green |
| `error` with `"span":null` | an exception captured out-of-band of any span (`ITelemetrySink.CaptureException`). A span-level error carries its span id instead — the field is always present so a reader never has to guess |
| `inner.error` | a call into the wrapped backend threw, and was swallowed rather than reaching the host. Note this does **not** cover a Sentry that failed to *initialise* — `SentrySdk.Init` runs in the `SentryTelemetrySink` constructor, before this sink ever sees it, so that failure throws out of your startup code and produces no transcript at all. Nor does it cover a firewall silently dropping envelopes, which raises no exception |
| a `-2` suffix on the file name | the previous file hit its size cap, or another process holds it. `-p<pid>` appears only in the rare case where every plain name is taken, so two processes can't lock each other out |
| `trunc` | a record was refused for size; the log rolled to the next file and continued |

#### Size and retention

A form session is a few dozen records, so this stays small on its own. Three bounds keep it that way regardless:

| Scope | Limit | Behaviour at the limit |
|---|---|---|
| Per value | 2048 chars for values (`msg`, `v`, `stack`) · 256 for names (`k`, `cat`, `op`, `release`, `env`, `session`, `trace`, `host`, `name`) | truncated, with a `…[trimmed]` marker on the end |
| Per file | `MaxBytesPerFile`, 8 MB by default | rolls to `20260828-2.jsonl` and keeps writing — no records are lost |
| Per directory | `RetentionDays` (7) **and** `MaxTotalBytes` (64 MB) | oldest files deleted; both bounds apply |

The sweep runs **at most once an hour**, and only when a file is opened or rolled — so a process with a viewer open all day sweeps at startup and again at the midnight roll, not on a timer. It deletes only files whose names match the ones this component writes (`yyyyMMdd[-n][-p<pid>].jsonl`), so pointing `Directory` at a folder holding your own `.jsonl` files is safe.

7 days is sized to how long a support request takes to arrive rather than to disk usage. If your own support process is slower than that, raise `RetentionDays`.

#### PHI

**The transcript is held to the same rule as Sentry: no FHIR payloads.** That matters more here, not less, because the point of the file is to leave the hospital. Concretely:

- **Every** string written is length-capped and has the user-profile path replaced with `%USERPROFILE%` — values and names alike (a Windows account name is often a person's name). Note the limit of that scrubbing: release-build stack traces carry the *build* machine's paths, which won't match, so it protects patient-adjacent paths rather than every path.
- **`SetExtra` writes strings and primitives; everything else becomes just its type name** — `<Hl7.Fhir.Model.QuestionnaireResponse>`. So attaching a resource records *that* you attached one, not its contents. If you need a detail from it in the transcript, pass that detail as a string.
- **No DSN is ever written**, on any path.
- If your own code puts patient identifiers in exception messages, scrub them before they bubble up — the same caveat as for Sentry above.

One deliberate difference from Sentry, worth knowing before you send a file: the header records `host` (the machine name), and the Sentry adapter leaves `SendDefaultPii` off, so **Sentry never receives a machine name and the file does**. Support needs to know which workstation a transcript came from; if your workstation names identify people or rooms, that's a reason to review a file before forwarding it.

#### What it doesn't cover

- **The embedded page stays dark in a file-only deployment.** Page-side telemetry needs a DSN to bootstrap, so with no inner Sentry sink there's nothing to inject and the JS side reports nothing. The transcript covers the .NET host only. Wrapping a `SentryTelemetrySink` restores it.
- **A blocked network is still not self-announcing.** The file tells you what the host did; it can't tell you Sentry's envelopes were dropped in transit. Comparing a transcript against a Sentry side with no matching `form.session.id` is what shows that.

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

The SDC server you point at should be at or above the version this harness release declares — the first `SetContextAsync` checks it and reports if it isn't. See [SDC server version compatibility](#sdc-server-version-compatibility).

### Rendering a form read-only

`ReadOnly` renders the form view-only — no answer can be changed. Set it before `SetContextAsync`, like the endpoint properties:

```vb
TiroFormViewer.ReadOnly = True
Await TiroFormViewer.SetContextAsync(templateUrl, patient, initialResponse:=savedResponse)
```

It travels to the page on the same `sdc.configure` message as the endpoints, so the bridge applies it before the form initializes — a read-only launch never paints an editable form first. This is the supported way to show a validated or archived report; you do **not** need a second `index.html` with the `read-only` attribute baked in.

Two limits worth knowing:

- **Per session, not per moment.** `ReadOnly` is read once when the `sdc.configure` payload is built, so a viewer can't be flipped between editable and view-only mid-session — use one viewer per role (the EhrShell sample opens its read-only consultation in a separate window with its own viewer). You don't need to do anything for the post-submit case: the form component locks itself once a **final** response has been submitted. A saved draft (`intent: "save-draft"`) deliberately does not lock it — the session stays editable so the user can keep filling and submit later.
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
│   ├── Tiro.Health.FormSdk.Abstractions/           # Shared SDC-server contract — MinimumSdcVersion + the version probe
│   ├── Tiro.Health.FormSdk.Client/                 # Typed SDC FHIR client core ($validate/$extract)
│   └── Tiro.Health.FormSdk.Client.Fhir.R5/         # SDC client — FHIR R5 closed binding
├── samples/
│   ├── Tiro.Health.FormFiller.WebView2.Sample/         # Single-form, single-patient demo — shows the response narrative (R5)
│   ├── Tiro.Health.FormFiller.WebView2.ExtractSample/  # Like Sample, but runs SDC $extract and shows the extracted Composition narrative (R5); page adds the Magic Clipboard
│   └── Tiro.Health.FormFiller.WebView2.EhrShellSample/ # Dummy EHR shell — patient/encounter/template selection,
│                                                       # tabbed viewer, in-memory QR persistence, custom index.html (R5)
├── tests/
│   ├── Tiro.Health.SmartWebMessaging.Tests/        # MSTest, protocol/handler coverage
│   ├── Tiro.Health.FormFiller.WebView2.Tests/      # MSTest, viewer lifecycle + telemetry contracts + embedded assets
│   ├── Tiro.Health.FormSdk.Client.Tests/           # MSTest, SDC client $validate/$extract + the version gate, over a fake HttpMessageHandler
│   ├── bridge/                                     # Node, bridge behaviour against a transcribed stub of the element
│   └── e2e/                                        # The real element and the real harness against a real SDC server — see its README
│       ├── browser/                                #   layer 1: Playwright, real <tiro-form-filler> + real bridge
│       ├── WebView2Probe/                          #   layer 2: the harness binary in real WebView2 (Windows)
│       └── fixtures/                               #   the pinned questionnaire + the template server that serves it
└── build/
    ├── web-sdk/                                    # The web-sdk pin; copy-bundle.mjs stages it when the pin moves
    ├── bridge-contract/                            # tsc --checkJs of the bridge against the frontend's published types
    └── release-notes/                              # Composes each release's notes from the pin and MinimumSdcVersion
```

### `Tiro.Health.SmartWebMessaging` (core)
FHIR-version-agnostic implementation of the SMART Web Messaging protocol.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandlerBase<TResource, TQuestionnaireResponse, TOperationOutcome>` — abstract generic handler covering protocol routing, request/response correlation via `Func<SmartMessageResponse, Task>` listeners, and `CancellationToken` plumbing across the entire async surface
- **Handles**: `status.handshake`, `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, `form.submitted`, `ui.form.requestSubmit`, `ui.form.insertText`, `ui.form.persist`, `ui.done`, `ui.form.dirtyChanged`
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
- **Telemetry seam** (namespace `Tiro.Health.FormFiller.WebView2.Telemetry`): `ITelemetrySink` (begins sessions, captures exceptions, flushes), `ITelemetrySession` (starts transactions in one trace), `ITelemetrySpan` (`IDisposable`; transactions and child spans), `TelemetrySpanStatus`, `NullTelemetrySink` (the no-op default), and `FileTelemetrySink` + `FileTelemetryOptions` (a rolling local JSONL transcript that also decorates any other sink). No backend dependency — the Sentry-backed implementation ships in `Tiro.Health.FormFiller.WebView2.Sentry`; implement the interfaces yourself for any other backend.
- **Features**:
  - Explicit lifecycle state machine (`TiroFormViewerState`: Initializing → Ready → ContextSet → Submitted → Disposed)
  - Async API with `CancellationToken` end-to-end; in-flight operations cancel cleanly on disposal
  - Pluggable `IEmbeddedBrowser` seam for testability (default: `WebView2EmbeddedBrowser`)
  - Pluggable `ITelemetrySink` seam (default: `NullTelemetrySink`); see telemetry section below
  - `FileTelemetrySink` — rolling JSONL transcript on local disk (one file per day, shared by every viewer in the process), standalone or wrapped around Sentry, for sites whose network blocks telemetry egress; see [When Sentry can't leave the hospital network](#when-sentry-cant-leave-the-hospital-network)
  - Embeds `WebAssets/tiro-swm-bridge.js` and auto-injects it into every page via WebView2's `AddScriptToExecuteOnDocumentCreatedAsync` — page is UI-only
  - Optional consumer-supplied `WebContentFolder` for hosting your own `index.html`; the shipped one is a working sample with a visible banner prompting integrators to override it for production
  - Host-configured `<tiro-form-filler>` endpoints via `SdcEndpointAddress` / `DataEndpointAddress`; the bridge applies them on the page so the .NET host and embedded JS always agree on which FHIR servers to hit
  - Host-configured view-only rendering via `ReadOnly`, applied before the form initializes so no second `index.html` is needed for read-only roles
  - Host-supplied right-click menu entries via `ContextMenuItems` (`TiroContextMenuItem`), appended to the embedded browser's own context menu through the optional `IContextMenuCapableBrowser` capability — the EHR's labels, the EHR's data, resolved per click; see [Host items in the form's right-click menu](#host-items-in-the-forms-right-click-menu)
  - SDC server version check on the first `SetContextAsync`, reported through telemetry when the configured server is older than `SdcCompatibility.MinimumSdcVersion` — see [SDC server version compatibility](#sdc-server-version-compatibility)

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

### `Tiro.Health.FormSdk.Abstractions`
The SDC-server contract shared by the form viewer and the SDC client — everything about the server that both surfaces have to agree on. UI-free, FHIR-model-free, `System.Text.Json` its only dependency; it arrives transitively with either package and integrators never reference it directly.

- **Targets**: `netstandard2.0`, `net48`
- **Key types**: `SdcCompatibility` (holds `MinimumSdcVersion` and the version grammar), `SdcServerVersionProbe` (reads a live server's version — public so a host can preflight at startup), `SdcVersionCheckResult` / `SdcVersionCheckOutcome`
- **Why a separate package**: the viewer and the client stay siblings with no dependency on each other (each has a different runtime and lifecycle), so a floor that lives in either one would have to be duplicated in the other — and two copies of a version number drift. This is the one deliberate shared type between them. See [SDC server version compatibility](#sdc-server-version-compatibility).

### `Tiro.Health.FormSdk.Client` (core) / `Tiro.Health.FormSdk.Client.Fhir.R5`
Thin, strongly-typed client over the **stateless SDC server** FHIR operations — call them directly instead of hand-building request bodies and parsing raw responses. A separate concern from the messaging/viewer packages (an HTTP/FHIR client, not an embedded-UI bridge); depend on it only if your host calls the SDC server itself.

- **Targets**: `netstandard2.0`, `net48`
- **Key types**: `SdcClientBase<TQuestionnaireResponse, TOperationOutcome, TBundle>` (core, FHIR-version-agnostic) and `SdcClient` (R5 closed binding)
- **Operations**: `ValidateAsync(qr)` → `OperationOutcome` (`QuestionnaireResponse/$validate`); `ExtractAsync(qr)` → transaction `Bundle` (`QuestionnaireResponse/$extract`)
  - `$extract` runs the questionnaire's SDC extraction over a completed `QuestionnaireResponse` and returns a transaction `Bundle` — the resources the answers produce, plus the source QR and a `Provenance` linking them. What's extracted depends on the questionnaire: Tiro's **template-based** questionnaires yield a `Composition` whose sections carry the rendered narrative, while **definition-based** questionnaires yield structured resources (e.g. `Observation`). The stateless endpoint computes the Bundle without persisting it.
- **Construction**: `new SdcClient(new Uri("https://host/fhir/r5"), httpClient?)` — inject a pre-configured `HttpClient` for custom TLS/proxy/timeouts. The client deliberately has **no default base** — you must pass one.
- **Point the client at the same SDC server as the form.** `baseAddress` here and the viewer's `SdcEndpointAddress` ([`TiroFormViewer`](#tirohealthformfillerwebview2)) are the *same* concept — the SDC server. A host that embeds the form **and** calls `$validate`/`$extract` directly should **construct the client from `viewer.SdcEndpointAddress`** (see [Extracting after a form submit](#extracting-after-a-form-submit)) so the two can't drift apart. Note this is a **convention, not an enforced guarantee** — nothing in the API stops you from pointing the form and the client at different servers, so derive the client's address from the viewer rather than configuring it separately.
- **Behaviour**: thin over Firely's serializer + `HttpClient` (POSTs a bare `QuestionnaireResponse`, the shape the SDC server expects). A validation failure comes back as `OperationOutcome` issues; transport/server errors (non-2xx) throw `SdcOperationException`. Responses are parsed in Firely's *recoverable* mode, so a `200` carrying an element/code a newer server emits that this Firely version doesn't recognize is still returned (partial POCO) rather than failing
- **SDC server version check** — the first operation on a client establishes the server's version and reports it against `SdcCompatibility.MinimumSdcVersion`. Nothing is refused; the verdict stays readable on `client.ServerVersionCheck`. The check runs once per client instance and travels the injected `HttpClient`, so custom TLS/proxy/auth apply to it too. See [SDC server version compatibility](#sdc-server-version-compatibility)
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
- **`ExtractSample`** — same shape as `Sample`, but instead of showing the response narrative it demonstrates the **SDC `$extract` client**. On submit it constructs an `SdcClient` from the viewer's `SdcEndpointAddress` (so the extract targets the same SDC server the form rendered against), runs `$extract` over the completed QR to get the transaction `Bundle` of structured resources, pulls the `Composition` out of the Bundle, and shows its narrative (`Composition.Text.Div`) in a `MessageBox`. Falls back to a Bundle summary if the questionnaire extracts non-Composition resources (e.g. definition-based `Observation`s). Its `WebContent/index.html` also shows the **Magic Clipboard** — a `<tiro-magic-clipboard>` pane beside the form that autofills it from pasted clinical notes via SDC `$populate`, with no host-side wiring at all. See [AI autofill with the Magic Clipboard](#ai-autofill-with-the-magic-clipboard). `Form1_Load` adds the host-side counterpart: three **right-click menu items** (`ContextMenuItems`) — two that paste a snippet straight into the field that was right-clicked (`InsertTextAsync`, hidden where there's nothing to type into) and one that copies the patient name to the clipboard for the clinician to paste with Ctrl+V. See [Host items in the form's right-click menu](#host-items-in-the-forms-right-click-menu) and [Typing host text into the focused field](#typing-host-text-into-the-focused-field).
- **`EhrShellSample`** — dummy EHR shell bound to FHIR **R5**. Demonstrates the integration patterns a real EHR is going to need:
  - **Practitioner identity** (top status strip) passed through as the `author` in `LaunchContext`.
  - **Patient / encounter / template selection** — three hardcoded patients with their own encounters in the left sidebar; three canonical templates verified live on the default SDC server, picked via a modal `TemplatePickerDialog` from the **+ New report** button.
  - **Reports list per patient** — every saved `QuestionnaireResponse` (finalized or draft) is stored in an in-memory `ResponseStore` keyed by a stable **report id** and shown newest-first in the Patient details tab. **+ New report** mints a fresh id, so it's always a distinct report; reopening one to edit reuses its id, so resubmitting updates that report in place rather than creating a duplicate.
  - **Save in progress** — a footer button alongside **Submit** that calls `SendFormRequestSubmitAsync(intent:="save-draft")` (maps to the frontend's `submit({ status: "in-progress" })`; requires `tiro-web-sdk >= 0.3.0`). The draft round-trips back with status `in-progress`; the shell persists it (so it's resumable from the reports list) but **keeps the session alive** so the doctor can keep filling — distinguished from a finalized Submit (status `completed`, which ends the session) by inspecting `e.Response.Status` in the `FormSubmitted` handler.
  - **Read-only narrative preview** — single-clicking a saved report renders its narrative in a `RichTextBox` (RTF when the SDC's `$generate-narrative` produced one, plain-text fallback otherwise — both via `QuestionnaireResponseHelper`). The preview is decoupled from session state, so the doctor can peek at older reports while a form is in progress.
  - **Reopen a report — edit or read-only** — double-clicking a report (or **Open this report**) prompts how to open it. **Edit** resumes filling it in the main shell's Form tab with the saved QR as `initialResponse`, reusing its report id (blocked while another session is live, to avoid orphaning the active viewer). **Read-only** spawns a separate top-level `ReportConsultationForm` with its own `TiroFormViewerR5` — leaving any live session untouched (showcasing that multiple viewer instances coexist) — setting `ReadOnly = True` so the form renders view-only off the *same* `WebContent/Form/index.html` the editable session uses.
  - **Tabbed embedding with dynamic Form tab** — the Form tab only exists while a session is alive (added to / removed from `TabControl.TabPages` on launch / dispose). A context banner above the form viewer shows what's being filled. Switching tabs while filling *hides* the WebView2 (state preserved, JS keeps running, messages still route); explicit **Close session** button *disposes* it (state gone, viewer recreated next launch). Showcases the hide-vs-dispose contrast.
  - **Telemetry, both sinks at once** — `Program.vb` registers a local JSONL transcript wrapped around Sentry, which is the configuration to copy for a site whose network might block telemetry egress. Because the shell can have several viewers open (an editable session plus a read-only consultation window), running it also shows the shared day-file: several `session.start` records in one `%LOCALAPPDATA%\Tiro.Health\FormFiller\telemetry\<yyyyMMdd>.jsonl`. See [When Sentry can't leave the hospital network](#when-sentry-cant-leave-the-hospital-network).
  - **Custom `index.html` + host-side role config** — bundles a single `WebContent/Form/index.html`, shared by the editable session and the read-only consultation window. Illustrates where the line sits: the host API owns what the EHR is authoritative about (endpoints, launch context, `ReadOnly`), the page owns static presentation (branding, `auto-collapse` / `compact-grouping` / `density-mode`). Roles that differ only in editability share one page instead of forking it. See [Shipping your own index.html](#shipping-your-own-indexhtml) and [Rendering a form read-only](#rendering-a-form-read-only).
- All three: `.NET 4.8` (VB.NET, old-style project format).

### `Tiro.Health.SmartWebMessaging.Tests` / `Tiro.Health.FormFiller.WebView2.Tests` / `Tiro.Health.FormSdk.Client.Tests`
- **Targets**: `net8.0` (SmartWebMessaging) / `net48;net8.0` (FormSdk.Client — multi-targeted so the net48 build the libraries ship is also exercised) / `net48` (FormFiller — needs WinForms + WebView2)
- **Framework**: MSTest + Moq
- **Coverage**:
  - `SmartWebMessaging.Tests` — protocol routing, request/response correlation, payload validation (including `form.submitted` `[Required]` enforcement), event firing, JSON probe, async-task extensions
  - `FormFiller.WebView2.Tests` — viewer lifecycle (state machine transitions, dispose semantics), telemetry sink contracts (`NullTelemetrySink` no-ops, span ordering, session tagging), embedded `WebAssets/` resource integrity, and the SDC server version check (too old → reported and the form still opens; unknown → reported as a check diagnostic)
  - `FormSdk.Client.Tests` — SDC `$validate`/`$extract` over a fake `HttpMessageHandler`: typed result parsing, validation-issues-without-throw, non-2xx → `SdcOperationException`, and a guard that the request body is a bare `QuestionnaireResponse`. Also covers `Tiro.Health.FormSdk.Abstractions`: the version grammar and prerelease rule, the probe (base-relative resolution through a gateway prefix, the software-name attribution guard, the response-size cap, the deadline, BOM handling, fail-open on everything unreadable, caller cancellation propagating), and the client's startup gate

## Architecture notes

### Generic type binding
`SmartMessageHandlerBase<TResource, TQR, TOO>` and `TiroFormViewer<TResource, TQR, TOO>` keep the protocol and UI control independent of any FHIR version. The R5/R4 modules each provide concrete sealed subclasses that bind to their `Hl7.Fhir.*` types, so designer-instantiation and consumer code use a non-generic name (`SmartMessageHandler`, `TiroFormViewerR5`, etc.).

### Polymorphic JSON deserialization
`System.Text.Json`'s `[JsonDerivedType]` attribute does not support open generic types. The base handler installs a `PayloadTypeInfoResolver<TResource, TQuestionnaireResponse>` that registers concrete closed-generic payload types (e.g. `SdcDisplayQuestionnaire<Resource, QuestionnaireResponse>`) onto whatever `JsonSerializerOptions` the consumer (or default) supplies.

### Lifecycle state machine
`TiroFormViewerState` is an explicit enum (Initializing, Ready, ContextSet, Submitted, Disposed) backed by `Interlocked` operations on an `int`. Public methods guard against invalid states (e.g. `SetContextAsync` after `Submitted` throws `InvalidOperationException`; any operation after `Dispose` throws `ObjectDisposedException`). Only a **final** submission advances to `Submitted`: a saved draft (`status = in-progress`) raises `FormSubmitted` but stays in `ContextSet`, which is what lets the documented save-draft-then-submit flow work.

### Per-message Sentry transactions
The `Sentry`-backed sink starts one Sentry transaction per outbound message (round-trip — finishing on response receipt) and one per inbound notification, all sharing the trace id of the viewer's session. Lifecycle events (init, handshake, dispose) are breadcrumbs, not transactions, so Sentry's Performance dashboard shows meaningful per-operation latency rather than the noise of a long-lived "form session" transaction.

### Cross-process trace propagation
The host's traceId is injected into the embedded page in two ways: (1) as `<meta name="sentry-trace">` set by the bridge before `Sentry.init`, so the JS pageload transaction inherits the trace; (2) as `_meta.sentry.trace` on every outbound SMART Web Messaging envelope (typed via `MessageMeta` on `SmartMessageBase`), so JS-side spans during inbound handling continue the trace. The JS side echoes the trace context back on outbound messages too, completing the bidirectional propagation.

### Bridge injection
The JS that owns the page side of the protocol (`tiro-swm-bridge.js`) ships embedded in `Tiro.Health.FormFiller.WebView2` and is injected via WebView2's `AddScriptToExecuteOnDocumentCreatedAsync` so it runs before any page script. Mirrors the pattern used in `tiro-health/java-integration-harness` (form-filler-swing). The page is UI-only.

## Page-side API

Reference for integrators customizing their `index.html`. The auto-injected bridge exposes the following to the page:

- **`<tiro-form-filler>`** (from the auto-injected `tiro-web-sdk`) — auto-wired by the bridge: questionnaires arrive via the `questionnaire` attribute, user submissions come back via the `tiro-submit` event, and the bridge takes care of marshalling them onto the protocol.
- **`<tiro-magic-clipboard>`** (same bundle) — optional AI autofill pane, *not* wired by the bridge; the page owns it. See [AI autofill with the Magic Clipboard](#ai-autofill-with-the-magic-clipboard).
- **`window.tiro.cancel()`** — call from a Cancel button to send `ui.done` to the host.
- **`document` `CustomEvent`s** for status hooks: `tiro-connected`, `tiro-disconnected`, `tiro-submitted`, `tiro-submit-error`, `tiro-cancelled`, `tiro-text-inserted` (`detail.text`, `detail.inserted`), plus `tiro-sdk-error`/`tiro-sdk-collision` for SDK-loading problems. Listen if you want a status bar; ignore if you don't.
- **`window.SmartWebMessaging.{sendRequest, sendEvent, on}`** — lower-level API for advanced flows that don't fit the auto-wired form-filler model.

The page carries **no** `tiro-web-sdk` script tag — the harness injects the SDK itself (next section).

### AI autofill with the Magic Clipboard

`<tiro-magic-clipboard>` is a second element out of the embedded `tiro-web-sdk`: a notes pane (rich-text editor, file attachments, optional voice dictation) that fills the form in for the user. On **Autofill** it runs SDC `$populate` over whatever was pasted, dictated, or attached, and writes the returned answers into the linked `<tiro-form-filler>` — the server marks AI-populated answers with a contained `Provenance`, so the clinician's own edits stay distinguishable. The user then reviews and submits as usual, and the populated `QuestionnaireResponse` comes back to the host through `FormSubmitted` like any other.

Unlike `<tiro-form-filler>`, the bridge does **not** wire it: it's page-owned, so adding it is an `index.html` change and nothing else. Drop it next to the form filler and link the two by id:

```html
<tiro-magic-clipboard for="form-filler" dictation-endpoint="Endpoint/corti">
    <tiro-magic-clipboard-button>Autofill the form</tiro-magic-clipboard-button>
</tiro-magic-clipboard>

<tiro-form-filler id="form-filler"></tiro-form-filler>
```

- **A submit trigger is required.** The element renders the editor only; the pane has no button of its own. Slot in a `<tiro-magic-clipboard-button>` (or any `<button type="submit">`) — without one there is no way to start a population.
- **No endpoint to configure.** At Autofill time the element reads the SDC client and the questionnaire off the linked form filler, so it always targets the server the host configured (`SdcEndpointAddress`) for the form actually on screen. Don't hardcode an endpoint in the page — same rule as everywhere else.
- **`<tiro-magic-clipboard-button>` ships no styles** (no shadow DOM) and reflects the lifecycle on `data-state` (`idle` → `pending` → `success`/`error`, auto-resetting after 2s, or set `reset-delay="0"`). Style it with attribute selectors; see the Extract sample's page for a worked set.
- **Dictation is one attribute.** `dictation-endpoint` puts a microphone in the editor toolbar: give it a FHIR `Endpoint` relative reference (`Endpoint/corti` on Tiro's SDC servers), which the element resolves through the linked form filler's SDC client and whose `dictation-provider` identifier selects the provider (Corti, DMSK, Squire). **No host code is needed** — `WebView2EmbeddedBrowser` auto-grants the microphone permission for pages served from the harness's virtual host, so recording just works in the WinForms shell (any other origin falls through to WebView2's default-deny). Dictated text lands in the notes and is populated from like anything else.
- **Other optional attributes**: `dictation-language` (e.g. `nl-BE`; without it dictation follows the form's language), `placeholder`, `hide-toolbar`, `initial-files` (JSON `DocumentReference`s to preload).
- **Events** fire on the element, not on `document`: `tiro-populate-start`, `tiro-populate-complete` (`detail.response`), `tiro-populate-error` (`detail.error`), and `tiro-clipboard-change` (`detail.value`). `$populate` is a page-side round-trip to the SDC server, so none of this reaches the host.

Worked example: `samples/Tiro.Health.FormFiller.WebView2.ExtractSample/WebContent/index.html` — the clipboard in the left pane (dictation on), the form on the right, a status line driven by those events, and the host side (`Form1.vb`) untouched apart from a comment.

### Typing host text into the focused field

The Magic Clipboard lives in the page. When the labelled text lives in the **host** instead — a
snippet list, a phrase menu, a departmental macro palette in the EHR's own UI —
`InsertTextAsync` types it into the form field the user is standing in. The natural home for it
is [the right-click menu](#host-items-in-the-forms-right-click-menu):

```vb
' Right-click a text field -> "Paste conclusion" -> the text lands at the caret.
Dim pasteConclusion As New TiroContextMenuItem(
    "Paste conclusion",
    Function(context) TiroFormViewer.InsertTextAsync(conclusion))
pasteConclusion.IsVisible = Function(context) context.IsEditable
TiroFormViewer.ContextMenuItems.Add(pasteConclusion)
```

It works from any host UI, though — a toolbar button, a menu bar item, a docked phrase list:

```vb
Dim inserted As Boolean = Await TiroFormViewer.InsertTextAsync(snippet)
If Not inserted Then StatusLabel.Text = "Click in a text field first."
```

- **The caret is the target.** There is no `linkId` parameter and no `QuestionnaireResponse` in
  play: the text goes in at the caret through the input events a keystroke produces, so the
  renderer stays the only writer of answers and validation, dirty-state, provenance and the
  form's own undo all keep working. The host needs to know nothing about the questionnaire's
  structure — which also means it cannot choose the question. If you need to fill a *named*
  answer, that's a different job: prefill it with `SetContextAsync(initialResponse:=...)` at
  launch, or let the Magic Clipboard's `$populate` decide where prose belongs.
- **The bool says whether it landed.** `False` means there was nothing to type into — the user
  hasn't clicked into a field, or is standing in one that doesn't take free text (a checkbox, a
  date picker). From a context-menu item filtered with `IsVisible = ctx.IsEditable` that's
  nearly unreachable, which is the main reason to prefer the menu; from a button it's worth
  surfacing, or the button reads as broken. `Nothing`/`""` also returns `False`, without
  sending anything.
- **Focus is handled either way.** A context-menu item is the easy case: the menu is the
  browser's own, so focus never leaves the page and the caret stays where the user
  right-clicked. From a WinForms `Button`, the click takes keyboard focus off the WebView2 —
  `InsertTextAsync` gives it back to the browser control, and the bridge re-focuses the field
  the caret was in (tracking it across shadow boundaries, where the SDK's fields live), so the
  text still lands there and the next keystroke continues after it.
- **It replaces the selection**, like typing does. Insert a trailing space in the snippet if you
  want the next word separated. Newlines only survive in a field that accepts them.
- **Requires a displayed form.** Before `SetContextAsync` there are no fields, so the call
  throws `InvalidOperationException` rather than blocking on a handshake that can't arrive —
  same guard as `SendFormRequestSubmitAsync`.
- Pages can follow along: the bridge fires a `tiro-text-inserted` `CustomEvent` on `document`
  with `detail.text` and `detail.inserted`. Useful if your page drives a status area of its own;
  the insert is visible in the field either way, so it's optional.

Under the hood this is one protocol message, `ui.form.insertText`, with a
`FormInsertText` payload (`{ text }`) — `MessageHandler.SendFormInsertTextAsync` if you want to
send it yourself. The page answers with `inserted` on the ack, which is what the `Boolean`
carries back. Insertion goes through `document.execCommand("insertText")`, deliberately: it is
the only insertion Chromium routes through `beforeinput`/`input` as if it had been typed, and
therefore the only one a React-controlled field keeps. Writing `.value` looks right until the
next render reverts it, with the text never reaching the `QuestionnaireResponse`.

Worked example: the Extract sample's two **Paste ...** menu items (`Form1.vb`, in
`Form1_Load`), on the same form its in-page Magic Clipboard autofills.

### Host items in the form's right-click menu

The clipboard route: the EHR offers labelled entries in the form's context menu, each one
copying host data, and the clinician pastes it where they want with **Ctrl+V**. Pasting itself
needs nothing from the harness — WebView2 handles Ctrl+V as real typed input, so the answer
reaches the `QuestionnaireResponse` exactly as if it had been typed. What the harness supplies
is the menu:

```vb
' In Form_Load, or wherever the EHR knows its data. Read at menu time, not now.
TiroFormViewer.ContextMenuItems.Add(
    TiroContextMenuItem.CopyToClipboard("Copy patient name", Function() patient.Name(0).Text))

Dim copyConclusion As TiroContextMenuItem =
    TiroContextMenuItem.CopyToClipboard("Copy conclusion", Function() currentConclusion)
copyConclusion.IsVisible = Function(context) context.IsEditable   ' only over typeable fields
TiroFormViewer.ContextMenuItems.Add(copyConclusion)
```

Right-click inside the form and the items appear below WebView2's own entries, separated from
them.

**Configuring from the EHR.** `ContextMenuItems` is a plain `IList` you fill in code, and the
harness reads it *on every right-click* — so nothing is baked in at startup:

- Build it from the EHR's own configuration (a settings table, a per-department list, a user's
  saved phrases) with an ordinary loop; the harness never parses config of its own.
- Add, remove or relabel items whenever you like — when the patient changes, when a report
  reaches a state that makes a snippet meaningful.
- The **text** is resolved at click time too. `CopyToClipboard` takes a `Func(Of String)`, not a
  string, so a lambda closing over the EHR's current state always copies what's current — no
  refresh step, no stale conclusion.
- `IsVisible` decides per click, from a `TiroContextMenuContext` carrying `IsEditable` (was the
  click in something typeable?) and `SelectionText` (what the user had selected — useful for a
  "look up this term" item, and clinical content, so treat it accordingly).

`CopyToClipboard` carries **plain text** — which is what a form answer stores anyway. There is
no rich-text flavour: HTML on the Windows clipboard needs a CF_HTML envelope with byte offsets,
and RTF never reaches the page at all (Chromium doesn't read that flavour), so neither earns its
place here. If you need formatted content in a field, the route is a converter on your side plus
the insert path below, not the clipboard.

**Items that paste for you.** For anything other than copying, construct the item directly:
`New TiroContextMenuItem(label, action)`, where the action is `Action`,
`Action(Of TiroContextMenuContext)`, or a `Func(Of TiroContextMenuContext, Task)` for async
work. That last one skips the clipboard entirely — right-click, "Paste conclusion", and
[`InsertTextAsync`](#typing-host-text-into-the-focused-field) puts it at the caret, leaving
whatever the clinician had copied untouched:

```vb
Dim pasteConclusion As New TiroContextMenuItem(
    "Paste conclusion",
    Function(context) TiroFormViewer.InsertTextAsync(conclusion))
pasteConclusion.IsVisible = Function(context) context.IsEditable
TiroFormViewer.ContextMenuItems.Add(pasteConclusion)
```

Hand back the task rather than writing an `Async Sub` lambda: the harness observes it, so a
failed insert reaches telemetry instead of becoming an unhandled async-void exception. Which
kind of item to use is per item — copy for data the clinician may want in another application,
paste for text that belongs in this form.

**Details worth knowing:**

- **The menu is Chromium's, extended — not a `ContextMenuStrip`.** A WinForms menu can't appear
  over web content at all, and suppressing the browser's menu to show one would move focus out
  of the WebView2 and lose the caret the user just right-clicked into. Extending the native menu
  keeps focus in the page, which is exactly what makes the following Ctrl+V land where they
  expect.
- **The label is the item's identity.** WebView2 caps the number of live custom menu items per
  environment and asks that they be reused, so the harness creates one per label and reuses it
  across menus. Two items sharing a label collapse into one.
- **`CopyToClipboard` handles the awkward parts**: it skips a null/empty result (the clipboard
  API rejects it, and clearing the clinician's clipboard is worse than doing nothing), and goes
  through the retrying `Clipboard.SetDataObject(value, copy:=True, retryTimes:=5, retryDelay:=50)`
  because the clipboard is a machine-wide single-owner resource another process can be holding.
  `copy:=True` means a copy made just before the form closes still pastes afterwards.
- **What you copy leaves the application.** The Windows clipboard is readable by every process
  on the machine, and clipboard managers, Remote Desktop redirection and Windows Cloud Clipboard
  may persist or sync it. Unremarkable for a name the clinician is about to paste; worth a
  deliberate decision before wiring a whole report to it.
- **A throwing item costs itself, nothing else.** Actions and `IsVisible` tests run inside the
  browser's menu dispatch, where an escaping exception would land unhandled on the message pump.
  They're captured to telemetry instead — and deliberately *without* the label or the copied
  value, both of which are host-authored and can carry patient data.
- Needs an `IEmbeddedBrowser` implementing `IContextMenuCapableBrowser` (the WebView2 one does).
  With any other browser the items are simply never shown.

Worked example: the Extract sample's four items in `Form1_Load` — two **Paste ...** snippets
filtered to editable targets, and two **Add ... to clipboard** items shown everywhere.

### Frontend version compatibility

The harness **embeds** the exact `tiro-web-sdk` version it was validated against (pinned in `build/web-sdk/package.json`) and serves it to the page itself — there is no SDK version to choose, in the page or anywhere else. Bridge and element ship and are CI-validated as one pair, so the historical skew hazards are gone by construction:

- **Save-draft** (`SendFormRequestSubmitAsync(intent: "save-draft")`) needs `submit({ status })` (web-sdk >= 0.3.0) — the embedded SDK satisfies this. On the old page-pinned model, an older SDK silently **finalized** instead of saving a draft; that failure mode can no longer occur.
- **`TiroFormViewer.IsDirty`/`FormDirtyChanged`** needs [`isDirty`/`tiro-dirty-change`](https://github.com/Tiro-health/atticus-frontend/issues/2831) (web-sdk >= 0.3.2) — likewise satisfied by the embedded SDK.

A page that still loads its own `tiro-web-sdk` copy collides with the embedded one: the bridge skips injection, fires `tiro-sdk-collision`, and the viewer **refuses the session** — `SetContextAsync` throws `WebSdkLoadException` — remove the script tag. The same refusal applies when the embedded SDK fails to load (`tiro-sdk-error`), so a broken environment surfaces as a clear exception instead of a blank form. The `build/bridge-contract/` type-check gates every PR and release against the pinned version, so nothing in the page is a version choice any more.

### SDC server version compatibility

> **Pin the harness NuGet; run an SDC server at or above `MinimumSdcVersion`.** That pair is the
> whole compatibility surface — the embedded web-sdk is not a version you choose.

```csharp
Tiro.Health.FormSdk.Abstractions.SdcCompatibility.MinimumSdcVersion   // e.g. "v0.9.39"
```

The value is in each release's notes. It is checked at first use — `TiroFormViewer` on the first
`SetContextAsync`, `SdcClient` on the first `$validate`/`$extract` — by reading
`GET {sdcEndpoint}/metadata` → `CapabilityStatement.software.version`, accepted only from a
document whose `software.name` identifies it as the SDC server. Each surface checks once and
caches the verdict. **Nothing is ever refused**; the check reports and gets out of the way.

- **Satisfied** — at or above the floor. Silent.
- **TooOld** — an actionable warning naming both versions. Upgrade the SDC server, or run the
  harness release whose minimum your server satisfies.
- **Unknown** — unreachable, timed out, 4xx/5xx, a document that isn't the SDC server's, a server
  that requires caller credentials, or a version outside the grammar (`dev`, `development`, a PR
  checkpoint id). A diagnostic about the *check*, not about your server.

Only `(major, minor, patch)` is compared, so `v0.9.39-rc.0` satisfies a floor of `v0.9.39` — the
production deploy accepts any tag, so a release candidate can legitimately reach a customer.

Read the verdict from `viewer.SdcServerVersionCheck` / `client.ServerVersionCheck`. The viewer
also captures an `ITelemetrySink` message, so the Sentry adapter surfaces a warning even when
nothing else in the session fails, and writes an `sdc.version` breadcrumb. Both surfaces write a
`System.Diagnostics.Trace` warning; `SdcClient` has only that, since it takes no telemetry seam.

To learn about a bad pairing at login — where the error reaches IT rather than a clinician
mid-consult — run the same probe yourself:

```vb
Dim verdict = Await SdcServerVersionProbe.CheckAsync(New Uri(sdcEndpoint))
If verdict.Outcome = SdcVersionCheckOutcome.TooOld Then
    ' block the workflow / alert operations — verdict.ToString() names both versions
End If
```

> Why this reports rather than refuses, why `software.name` is required, why there is no
> `/openapi.json` fallback, and how to raise the floor are recorded in
> [#76](https://github.com/Tiro-health/net-integration-harness/pull/76) and in
> `SdcCompatibility.MinimumSdcVersion`'s doc comment.

### WinForms Designer can't load `TiroFormViewerR5/R4`

`Hl7.Fhir.Base.dll`'s manifest strong-name-references `System.ComponentModel.Annotations` 4.2.0.0 while modern NuGet pulls 4.2.1.0. Runtime is fine (the auto-generated redirect handles it); the WinForms Designer in Visual Studio doesn't apply binding redirects, so it can fail to load the viewer at design time.

**Option A — pin the older Annotations package** (small apps):

```xml
<PackageReference Include="System.ComponentModel.Annotations" Version="4.4.1" />
```

This is the last package whose embedded assembly is still 4.2.0.0, satisfying `Hl7.Fhir.Base` directly without a redirect. NuGet emits an `NU1605` downgrade warning — expected; ignore.

**Unless your build escalates it.** With `TreatWarningsAsErrors` or `WarningsAsErrors=NU1605`, "ignore" is not available and the pin fails the build outright: `Hl7.Fhir.Base 5.13.2` requires `System.ComponentModel.Annotations >= 5.0.0`. Take Option B instead — it needs no pin, so there is no downgrade to escalate. The samples in this repo carry the pin, so a strict build of this solution reports it too; unload them if you only need the libraries.

In larger applications skip the pin regardless: it downgrades Annotations graph-wide and can collide with other libraries that strong-name reference 4.2.1.0+.

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
