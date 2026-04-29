using System;
using Hl7.Fhir.Model;
using Tiro.Health.FormFiller.WebView2.Telemetry;
using Tiro.Health.SmartWebMessaging;

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
    }
}
