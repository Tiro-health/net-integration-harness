using System.ComponentModel.DataAnnotations;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadDelete : RequestPayload
    {
        [Required(ErrorMessage = "Location is mandatory.")]
        public string Location { get; }

        public ScratchpadDelete(string location)
        {
            Location = location;
        }
    }
}
