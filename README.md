# net-integration-harness

A .NET library for integrating [SMART Web Messaging](https://hl7.org/fhir/smart-app-launch/smart-web-messaging.html) and [FHIR Structured Data Capture (SDC)](https://hl7.org/fhir/uv/sdc/) into Windows desktop applications using WebView2.

## Overview

This solution enables Windows desktop applications to embed FHIR-based questionnaire forms in a WebView2 control and communicate with them using the SMART Web Messaging protocol. The desktop host and the web-based form exchange JSON messages over a bidirectional channel, allowing the host to supply patient context and the form to return a completed `QuestionnaireResponse`.

## Solution Structure

```
net-integration-harness/
├── src/
│   ├── Tiro.Health.SmartWebMessaging/              # Core protocol handler (FHIR-version-agnostic)
│   ├── Tiro.Health.SmartWebMessaging.Fhir.R5/      # FHIR R5 concrete bindings
│   ├── Tiro.Health.SmartWebMessaging.Fhir.R4/      # FHIR R4 concrete bindings
│   └── Tiro.Health.FormFiller.WebView2/            # WinForms UserControl (VB.NET, .NET 4.8)
├── samples/
│   └── Tiro.Health.FormFiller.WebView2.Sample/     # WinForms demo application
└── tests/
    └── Tiro.Health.SmartWebMessaging.Tests/        # MSTest unit tests
```

## Projects

### `Tiro.Health.SmartWebMessaging` (Core)
FHIR-version-agnostic implementation of the SMART Web Messaging protocol.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandlerBase<TResource, TQuestionnaireResponse, TOperationOutcome>` — abstract generic handler with full protocol routing
- **Handles**: `scratchpad.create/read/update/delete`, `sdc.configure`, `sdc.configureContext`, `sdc.displayQuestionnaire`, `form.submitted`, `ui.done`, `status.handshake`
- **Includes**: `Scratchpad<TResource>` in-memory resource store with CRUD operations

### `Tiro.Health.SmartWebMessaging.Fhir.R5`
Concrete R5 bindings on top of the core library.

- **Targets**: `netstandard2.0`, `net48`
- **Key type**: `SmartMessageHandler` — binds the base handler to `Resource`, `QuestionnaireResponse`, `OperationOutcome` from `Hl7.Fhir.R5`
- **Adds**: Strongly-typed `FormSubmitted` and `ResourceChanged` events, R5-specific convenience overloads for `SendSdcConfigureContextAsync` and `SendSdcDisplayQuestionnaireAsync`

### `Tiro.Health.SmartWebMessaging.Fhir.R4`
Identical structure to the R5 package, but bound to FHIR R4 types.

### `Tiro.Health.FormFiller.WebView2`
Reusable WinForms `UserControl` that hosts a WebView2 browser and wires it to the SMART Web Messaging handler.

- **Targets**: `.NET 4.8` (VB.NET, old-style project format)
- **Key type**: `TiroFormViewer` — embeds a WebView2, establishes the JSON message channel, and delegates to `SmartMessageHandler`
- **Observability**: Sentry integration for error tracking and distributed tracing across the full form lifecycle

### `Tiro.Health.FormFiller.WebView2.Sample`
WinForms demo showing end-to-end usage: display a questionnaire for a patient, wait for submission, and handle the resulting `QuestionnaireResponse`.

- **Targets**: `.NET 4.8` (VB.NET, old-style project format)

### `Tiro.Health.SmartWebMessaging.Tests`
Unit test suite for the core messaging library.

- **Targets**: `net8.0`
- **Framework**: MSTest + Moq
- **Coverage**: 30 tests covering scratchpad CRUD, event firing, and message deserialization

## Usage

### 1. Add the `TiroFormViewer` control to a WinForms form

Drop `TiroFormViewer` onto a form, then call `SetContextAsync` once the form is shown:

```vb
Imports Tiro.Health.FormFiller.WebView2
Imports Hl7.Fhir.Model

Dim viewer As New TiroFormViewer()

' Minimal patient context
Dim patient As New Patient()
patient.Id = "patient-1"
patient.Name.Add(New HumanName() With {.Family = "Smith"})

Await viewer.SetContextAsync(
    questionnaireCanonicalUrl:="http://example.org/fhir/Questionnaire/my-form",
    patient:=patient)

AddHandler viewer.FormSubmitted, AddressOf OnFormSubmitted
```

### 2. Handle the submitted response

```vb
Private Sub OnFormSubmitted(sender As Object, e As FormSubmittedEventArgs)
    If e.Outcome.Success Then
        ' e.Response is the completed QuestionnaireResponse
        Console.WriteLine(e.Response.ToJson())
    End If
End Sub
```

### 3. Use the handler directly (without the WinForms control)

```csharp
using Tiro.Health.SmartWebMessaging.Fhir.R5;

var handler = new SmartMessageHandler(sendMessage: json => webView.PostWebMessageAsString(json));

handler.FormSubmitted += (_, e) =>
{
    Console.WriteLine(e.Response.ToJson());
};

// Wire incoming messages from WebView2
webView.WebMessageReceived += (_, e) => handler.HandleMessage(e.TryGetWebMessageAsString());

// Send context once the handshake fires
handler.HandshakeReceived += async (_, _) =>
{
    await handler.SendSdcConfigureContextAsync(patient: patient, encounter: encounter, author: author);
    await handler.SendSdcDisplayQuestionnaireAsync("http://example.org/fhir/Questionnaire/my-form");
};
```

## Dependencies

| Package | Version | Used by |
|---|---|---|
| `Hl7.Fhir.Base` | 5.11.4 | Core |
| `Hl7.Fhir.R5` | 5.11.4 | Fhir.R5, FormFiller, Sample, Tests |
| `Hl7.Fhir.R4` | 5.11.4 | Fhir.R4 |
| `System.Text.Json` | 9.0.2 | Core, Fhir.R5, Fhir.R4 |
| `Microsoft.Extensions.Logging.Abstractions` | 9.0.7 | Core, Fhir.R5, Fhir.R4 |
| `Microsoft.Web.WebView2` | 1.0.3650.58 | FormFiller, Sample |
| `Sentry` | 6.0.0 | FormFiller |
| `Newtonsoft.Json` | 13.0.3 | FormFiller |

## Building

### C# SDK-style projects (core libraries and tests)

```bash
dotnet build net-integration-harness.sln
dotnet test
```

### VB.NET .NET 4.8 projects (FormFiller + Sample)

The old-style `.vbproj` projects require Visual Studio's MSBuild, not the `dotnet` CLI:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    net-integration-harness.sln /nologo /verbosity:minimal
```

## Architecture Notes

### Generic type binding
`SmartMessageHandlerBase<TResource, TQR, TOO>` keeps the protocol layer independent of any FHIR version. R5 and R4 modules each provide a concrete `SmartMessageHandler` sealed to their respective types.

### Polymorphic JSON deserialization
`System.Text.Json`'s `[JsonDerivedType]` attribute does not support open generic types. The R5 module works around this with `R5TypeInfoResolver : IJsonTypeInfoResolver`, which registers concrete closed-generic payload types (e.g., `ScratchpadCreate<Resource>`) at runtime.

### Template method pattern for events
The base class exposes virtual `OnFormSubmitted` and `OnResourceChanged` methods. R5/R4 subclasses override these to raise strongly-typed C# events, so consumers never need to deal with the generic type parameters directly.

### Scratchpad
`Scratchpad<TResource>` is an in-memory working store (`Dictionary<string, TResource>` keyed by `ResourceType/Id`) that persists FHIR resources for the lifetime of a form session.
