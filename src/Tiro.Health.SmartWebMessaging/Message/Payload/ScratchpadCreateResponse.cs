namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadCreateResponse<TOperationOutcome> : ResponsePayload
    {
        public string Status { get; set; }
        public string Location { get; set; }
        public TOperationOutcome OperationOutcome { get; set; }

        public ScratchpadCreateResponse()
        {
        }

        public ScratchpadCreateResponse(string status, string location, TOperationOutcome outcome)
        {
            Status = status;
            Location = location;
            OperationOutcome = outcome;
        }
    }
}
