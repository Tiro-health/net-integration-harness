using System;
using System.Collections.Generic;
using Hl7.Fhir.Model;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// In-memory working store for FHIR resources received via scratchpad messages.
    /// </summary>
    public class Scratchpad<TResource>
        where TResource : Resource
    {
        private readonly Dictionary<string, TResource> _resources = new Dictionary<string, TResource>();
        private readonly Random _random = new Random();

        private string CreateResourceLocation(TResource resource)
        {
            if (resource.Id != null)
                return resource.TypeName + "/" + resource.Id;
            else
                return resource.TypeName + "/" + _random.Next(0, int.MaxValue);
        }

        /// <summary>
        /// Adds a new resource. Returns the location (TypeName/Id).
        /// </summary>
        public string CreateResource(TResource resource)
        {
            string location = CreateResourceLocation(resource);
            resource.Id = location.Substring(location.LastIndexOf('/') + 1);
            _resources[location] = resource;
            return location;
        }

        /// <summary>
        /// Updates an existing resource, or creates it if absent.
        /// </summary>
        public void UpdateResource(TResource resource)
        {
            string location = CreateResourceLocation(resource);
            _resources[location] = resource;
        }

        /// <summary>
        /// Removes a resource by its location key.
        /// </summary>
        public void DeleteResource(string location)
        {
            _resources.Remove(location);
        }

        /// <summary>
        /// Retrieves a resource by its location key, or null if not found.
        /// </summary>
        public TResource GetResource(string location)
        {
            _resources.TryGetValue(location, out TResource value);
            return value;
        }

        /// <summary>
        /// Returns all resources in the scratchpad.
        /// </summary>
        public IEnumerable<TResource> GetAllResources()
        {
            return _resources.Values;
        }
    }
}
