using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Fhir.R4
{
    /// <summary>
    /// FHIR R4 concrete handler. Binds Resource, QuestionnaireResponse, OperationOutcome.
    /// </summary>
    public class SmartMessageHandler
        : SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome>
    {
        // Typed events for R4 consumers
        public event EventHandler<FormSubmittedEventArgs> FormSubmitted;
        public event EventHandler<ResourceChangedEventArgs> ResourceChanged;

        // ---------------------------------------------------------------------------
        // Constructors
        // ---------------------------------------------------------------------------
        public SmartMessageHandler()
            : this(NullLogger<SmartMessageHandler>.Instance)
        {
        }

        public SmartMessageHandler(JsonSerializerOptions serializeOptions)
            : this(NullLogger<SmartMessageHandler>.Instance, serializeOptions)
        {
        }

        public SmartMessageHandler(ILogger<SmartMessageHandler> logger, JsonSerializerOptions serializeOptions = null)
            : base(logger, serializeOptions ?? CreateDefaultOptions())
        {
        }

        private static JsonSerializerOptions CreateDefaultOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }.ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Recoverable);
        }

        // ---------------------------------------------------------------------------
        // Template method overrides — raise R4-typed events
        // ---------------------------------------------------------------------------
        protected override void OnFormSubmitted(QuestionnaireResponse response, OperationOutcome outcome)
        {
            FormSubmitted?.Invoke(this, new FormSubmittedEventArgs(response, outcome));
        }

        protected override void OnResourceChanged(Resource resource)
        {
            ResourceChanged?.Invoke(this, new ResourceChangedEventArgs(resource));
        }

        // ---------------------------------------------------------------------------
        // R4-typed convenience send overloads
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Sends sdc.configureContext, building launch context from optional FHIR resources.
        /// </summary>
        public System.Threading.Tasks.Task<string> SendSdcConfigureContextAsync(
            Patient patient = null,
            Encounter encounter = null,
            Practitioner author = null,
            Func<Tiro.Health.SmartWebMessaging.Message.SmartMessageResponse, System.Threading.Tasks.Task> responseHandler = null)
        {
            var launchContext = new List<LaunchContext<Resource>>();
            if (patient != null) launchContext.Add(new LaunchContext<Resource>("patient", contentResource: patient));
            if (encounter != null) launchContext.Add(new LaunchContext<Resource>("encounter", contentResource: encounter));
            if (author != null) launchContext.Add(new LaunchContext<Resource>("user", contentResource: author));

            return SendSdcConfigureContextAsync(launchContext: launchContext, responseHandler: responseHandler);
        }

        /// <summary>
        /// Sends sdc.displayQuestionnaire with a Questionnaire resource.
        /// </summary>
        public System.Threading.Tasks.Task<string> SendSdcDisplayQuestionnaireAsync(
            Questionnaire questionnaire,
            QuestionnaireResponse questionnaireResponse = null,
            Patient patient = null,
            Encounter encounter = null,
            Practitioner author = null,
            Func<Tiro.Health.SmartWebMessaging.Message.SmartMessageResponse, System.Threading.Tasks.Task> responseHandler = null)
        {
            var launchContext = BuildLaunchContext(patient, encounter, author);
            return SendSdcDisplayQuestionnaireAsync(
                questionnaire: (object)questionnaire,
                questionnaireResponse: questionnaireResponse,
                launchContext: launchContext,
                responseHandler: responseHandler);
        }

        /// <summary>
        /// Sends sdc.displayQuestionnaire with a canonical URL.
        /// </summary>
        public System.Threading.Tasks.Task<string> SendSdcDisplayQuestionnaireAsync(
            string questionnaireCanonicalUrl,
            QuestionnaireResponse questionnaireResponse = null,
            Patient patient = null,
            Encounter encounter = null,
            Practitioner author = null,
            Func<Tiro.Health.SmartWebMessaging.Message.SmartMessageResponse, System.Threading.Tasks.Task> responseHandler = null)
        {
            var launchContext = BuildLaunchContext(patient, encounter, author);
            return SendSdcDisplayQuestionnaireAsync(
                questionnaire: (object)questionnaireCanonicalUrl,
                questionnaireResponse: questionnaireResponse,
                launchContext: launchContext,
                responseHandler: responseHandler);
        }

        private static List<LaunchContext<Resource>> BuildLaunchContext(Patient patient, Encounter encounter, Practitioner author)
        {
            var ctx = new List<LaunchContext<Resource>>();
            if (patient != null) ctx.Add(new LaunchContext<Resource>("patient", contentResource: patient));
            if (encounter != null) ctx.Add(new LaunchContext<Resource>("encounter", contentResource: encounter));
            if (author != null) ctx.Add(new LaunchContext<Resource>("user", contentResource: author));
            return ctx;
        }
    }

    // ---------------------------------------------------------------------------
    // Concrete event arg types for R4 consumers
    // ---------------------------------------------------------------------------

    public sealed class FormSubmittedEventArgs
        : FormSubmittedEventArgs<QuestionnaireResponse, OperationOutcome>
    {
        public FormSubmittedEventArgs(QuestionnaireResponse response, OperationOutcome outcome)
            : base(response, outcome) { }
    }

    public sealed class ResourceChangedEventArgs
        : ResourceChangedEventArgs<Resource>
    {
        public ResourceChangedEventArgs(Resource resource) : base(resource) { }
    }
}
