using System;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// Triggered when a scratchpad create or update is received.
    /// </summary>
    public class ResourceChangedEventArgs<TResource> : EventArgs
        where TResource : Resource
    {
        public TResource Resource { get; }

        public ResourceChangedEventArgs(TResource resource)
        {
            Resource = resource;
        }
    }
}
