using System.Collections.Generic;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadReadResponse<TResource, TOperationOutcome> : ResponsePayload
        where TResource : Resource
    {
        public TResource Resource { get; }
        public IEnumerable<TResource> Scratchpad { get; }
        public TOperationOutcome OperationOutcome { get; }

        public ScratchpadReadResponse(TResource resource, IEnumerable<TResource> scratchpad, TOperationOutcome outcome)
        {
            Resource = resource;
            Scratchpad = scratchpad;
            OperationOutcome = outcome;
        }
    }
}
