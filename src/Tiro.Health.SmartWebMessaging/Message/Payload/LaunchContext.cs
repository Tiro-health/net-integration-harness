using System;
using System.ComponentModel.DataAnnotations;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class LaunchContext<TResource>
        where TResource : Resource
    {
        [Required]
        public string Name { get; set; }

        public ResourceReference ContentReference { get; set; }

        public TResource ContentResource { get; set; }

        public LaunchContext() { }

        public LaunchContext(string name, ResourceReference contentReference = null, TResource contentResource = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("LaunchContext name is required", nameof(name));

            Name = name;
            ContentReference = contentReference;
            ContentResource = contentResource;
        }
    }
}
