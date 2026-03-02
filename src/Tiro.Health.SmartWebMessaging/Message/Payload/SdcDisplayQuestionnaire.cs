using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class QuestionnaireTypeAttribute : ValidationAttribute
    {
        public override bool IsValid(object value)
        {
            if (value == null) return true;
            return value is string || value is ResourceReference || value is Resource;
        }
    }

    public class SdcDisplayQuestionnaire<TResource, TQuestionnaireResponse> : RequestPayload
        where TResource : Resource
    {
        [Required(ErrorMessage = "Questionnaire is mandatory.")]
        [QuestionnaireType(ErrorMessage = "Questionnaire must be a string (canonical URL), Reference, or Resource.")]
        public object Questionnaire { get; set; }

        public TQuestionnaireResponse QuestionnaireResponse { get; set; }

        public SdcDisplayQuestionnaireContext<TResource> Context { get; set; }

        public SdcDisplayQuestionnaire()
        {
        }

        public SdcDisplayQuestionnaire(object questionnaire,
            TQuestionnaireResponse questionnaireResponse = default,
            SdcDisplayQuestionnaireContext<TResource> context = null)
        {
            Questionnaire = questionnaire;
            QuestionnaireResponse = questionnaireResponse;
            Context = context;
        }
    }

    public class SdcDisplayQuestionnaireContext<TResource>
        where TResource : Resource
    {
        public ResourceReference Subject { get; set; }

        public ResourceReference Author { get; set; }

        public ResourceReference Encounter { get; set; }

        public List<LaunchContext<TResource>> LaunchContext { get; set; }

        public SdcDisplayQuestionnaireContext()
        {
            LaunchContext = new List<LaunchContext<TResource>>();
        }

        public SdcDisplayQuestionnaireContext(ResourceReference subject = null, ResourceReference author = null,
            ResourceReference encounter = null, List<LaunchContext<TResource>> launchContext = null)
        {
            Subject = subject;
            Author = author;
            Encounter = encounter;
            LaunchContext = launchContext ?? new List<LaunchContext<TResource>>();
        }
    }
}
