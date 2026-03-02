using System.ComponentModel.DataAnnotations;

namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadRead : RequestPayload
    {
        [Required(ErrorMessage = "Location is mandatory.")]
        public string Location { get; }

        public ScratchpadRead(string location)
        {
            Location = location;
        }
    }
}
