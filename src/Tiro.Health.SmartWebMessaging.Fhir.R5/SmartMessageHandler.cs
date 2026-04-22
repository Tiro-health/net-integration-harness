using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Fhir.R5
{
    /// <summary>
    /// FHIR R5 concrete handler. Binds Resource, QuestionnaireResponse, OperationOutcome.
    /// </summary>
    public class SmartMessageHandler
        : SmartMessageHandlerBase<Resource, QuestionnaireResponse, OperationOutcome>
    {
        // Typed events for R5 consumers
        public event EventHandler<FormSubmittedEventArgs> FormSubmitted;

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
            : base(logger, EnsureR5Resolver(serializeOptions ?? CreateDefaultOptions()))
        {
        }

        /// <summary>Ensures the R5 polymorphic type resolver is present in the options.</summary>
        private static JsonSerializerOptions EnsureR5Resolver(JsonSerializerOptions options)
        {
            if (options.TypeInfoResolver is R5TypeInfoResolver) return options;
            var inner = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.TypeInfoResolver = new R5TypeInfoResolver(inner);
            return options;
        }

        private static JsonSerializerOptions CreateDefaultOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }.ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Recoverable);

            // Wrap the resolver to register concrete R5 generic payload types for polymorphic dispatch.
            var inner = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.TypeInfoResolver = new R5TypeInfoResolver(inner);
            return options;
        }

        private sealed class R5TypeInfoResolver : IJsonTypeInfoResolver
        {
            private readonly IJsonTypeInfoResolver _inner;

            public R5TypeInfoResolver(IJsonTypeInfoResolver inner) => _inner = inner;

            public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                var info = _inner.GetTypeInfo(type, options);
                if (info == null) return null;

                if (type == typeof(RequestPayload))
                {
                    EnsurePolymorphism(info);
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SdcConfigureContext<Resource>), "sdcConfigureContext"));
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(SdcDisplayQuestionnaire<Resource, QuestionnaireResponse>), "sdcDisplayQuestionnaire"));
                }

                return info;
            }

            private static void EnsurePolymorphism(JsonTypeInfo info)
            {
                if (info.PolymorphismOptions == null)
                {
                    info.PolymorphismOptions = new JsonPolymorphismOptions
                    {
                        TypeDiscriminatorPropertyName = "$type",
                        UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType
                    };
                }
                else
                {
                    info.PolymorphismOptions.UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType;
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Template method overrides — raise R5-typed events
        // ---------------------------------------------------------------------------
        protected override void OnFormSubmitted(QuestionnaireResponse response, OperationOutcome outcome)
        {
            FormSubmitted?.Invoke(this, new FormSubmittedEventArgs(response, outcome));
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
    // Concrete event arg types for R5 consumers (no generics required at call site)
    // ---------------------------------------------------------------------------

    public sealed class FormSubmittedEventArgs
        : FormSubmittedEventArgs<QuestionnaireResponse, OperationOutcome>
    {
        public FormSubmittedEventArgs(QuestionnaireResponse response, OperationOutcome outcome)
            : base(response, outcome) { }
    }
}
