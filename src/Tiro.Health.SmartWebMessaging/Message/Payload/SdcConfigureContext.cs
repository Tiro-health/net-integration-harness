using System.Collections.Generic;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class SdcConfigureContext<TResource> : RequestPayload
        where TResource : Resource
    {
        public ResourceReference Subject { get; set; }

        public ResourceReference Author { get; set; }

        public ResourceReference Encounter { get; set; }

        public List<LaunchContext<TResource>> LaunchContext { get; set; }

        public SdcConfigureContext()
        {
            LaunchContext = new List<LaunchContext<TResource>>();
        }

        public SdcConfigureContext(ResourceReference subject = null, ResourceReference author = null,
            ResourceReference encounter = null, List<LaunchContext<TResource>> launchContext = null)
        {
            Subject = subject;
            Author = author;
            Encounter = encounter;
            LaunchContext = launchContext ?? new List<LaunchContext<TResource>>();
        }
    }
}
