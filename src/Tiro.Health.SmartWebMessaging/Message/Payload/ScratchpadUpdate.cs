using System.ComponentModel.DataAnnotations;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadUpdate<TResource> : RequestPayload
        where TResource : Resource
    {
        [Required(ErrorMessage = "Resource is mandatory.")]
        public TResource Resource { get; }

        public ScratchpadUpdate(TResource resource)
        {
            Resource = resource;
        }
    }
}
