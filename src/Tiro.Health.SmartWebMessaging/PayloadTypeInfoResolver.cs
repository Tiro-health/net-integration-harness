using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hl7.Fhir.Model;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// Registers closed generic payload types for polymorphic JSON serialization.
    /// Applied automatically by <see cref="SmartMessageHandlerBase{TResource, TQuestionnaireResponse, TOperationOutcome}"/>.
    /// </summary>
    internal sealed class PayloadTypeInfoResolver<TResource, TQuestionnaireResponse> : IJsonTypeInfoResolver
        where TResource : Resource
    {
        private readonly IJsonTypeInfoResolver _inner;

        public PayloadTypeInfoResolver(IJsonTypeInfoResolver inner) => _inner = inner;

        public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var info = _inner.GetTypeInfo(type, options);
            if (info == null) return null;

            if (type == typeof(RequestPayload))
            {
                EnsurePolymorphism(info);
                info.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(typeof(SdcConfigureContext<TResource>), "sdcConfigureContext"));
                info.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(typeof(SdcDisplayQuestionnaire<TResource, TQuestionnaireResponse>), "sdcDisplayQuestionnaire"));
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
}
