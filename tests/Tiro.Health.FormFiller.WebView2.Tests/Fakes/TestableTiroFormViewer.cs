using System;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.FormSdk.Abstractions;
using Tiro.Health.SmartWebMessaging;
// Hl7.Fhir.Model also defines a `Task` resource; disambiguate the async return type.
using Task = System.Threading.Tasks.Task;

namespace Tiro.Health.FormFiller.WebView2.Tests.Fakes
{
    /// <summary>
    /// Concrete subclass of <see cref="TiroFormViewer{TResource,TQR,TOO}"/> for tests.
    /// Uses the protected DI ctor so tests inject a <see cref="FakeEmbeddedBrowser"/>,
    /// a real R5 <c>SmartMessageHandler</c>, and a <see cref="FakeTelemetrySink"/>.
    /// </summary>
    internal sealed class TestableTiroFormViewer : TiroFormViewer<Resource, QuestionnaireResponse, OperationOutcome>
    {
        public TestableTiroFormViewer(
            IEmbeddedBrowser browser,
            SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome> handler,
            ITelemetrySink telemetry)
            : base(browser, handler, telemetry)
        {
        }

        protected override SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome> CreateMessageHandler()
            => throw new NotSupportedException("Tests use the DI ctor; the factory should never be called.");

        protected override bool IsOutcomeSuccessful(OperationOutcome outcome)
            => outcome == null || outcome.Success;

        // Mirrors the R5/R4 bindings, so lifecycle tests see production draft semantics.
        protected override bool IsResponseFinal(QuestionnaireResponse response)
            => response?.Status != QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;

        /// <summary>
        /// Verdict handed back instead of probing a real SDC server. Defaults to a satisfied
        /// result at exactly <see cref="SdcCompatibility.MinimumSdcVersion"/>, so tests that
        /// set <c>SdcEndpointAddress</c> for other reasons neither reach the network nor trip
        /// the gate. Assign a too-old / unknown result to exercise the gate itself.
        /// </summary>
        public SdcVersionCheckResult SdcVersionCheckToReturn { get; set; } =
            SdcVersionCheckResult.FromReportedVersion(
                SdcCompatibility.MinimumSdcVersion, SdcVersionCheckResult.CapabilityStatementSource);

        /// <summary>How many times the version probe was invoked — the check must run once per viewer.</summary>
        public int SdcVersionCheckCount => _sdcVersionCheckCount;
        private int _sdcVersionCheckCount;

        /// <summary>The base address the probe was handed, so tests can assert it came from SdcEndpointAddress.</summary>
        public Uri LastSdcVersionCheckAddress { get; private set; }

        /// <summary>
        /// Raw task handed back instead of a completed one, so tests can reach the paths a
        /// canned result cannot: a probe that faults, and one that ends up cancelled without
        /// the caller or the viewer having cancelled anything (which is what the launch budget
        /// expiring looks like from here). <c>null</c> means "use
        /// <see cref="SdcVersionCheckToReturn"/>".
        /// </summary>
        public Task<SdcVersionCheckResult> SdcVersionCheckTaskToReturn { get; set; }

        protected override Task<SdcVersionCheckResult> CheckSdcServerVersionAsync(
            Uri sdcBaseAddress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sdcVersionCheckCount);
            LastSdcVersionCheckAddress = sdcBaseAddress;
            return SdcVersionCheckTaskToReturn ?? Task.FromResult(SdcVersionCheckToReturn);
        }
    }
}
