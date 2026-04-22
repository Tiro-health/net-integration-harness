using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Fhir.R5
{
    /// <summary>
    /// FHIR R5 concrete handler. Binds Resource, QuestionnaireResponse, OperationOutcome.
    /// </summary>
    public class SmartMessageHandler
        : SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome>
    {
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
        // R5-typed convenience send overloads
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
            var launchContext = BuildLaunchContext(patient, encounter, author);
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
}
