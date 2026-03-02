namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadDeleteResponse<TOperationOutcome> : ResponsePayload
    {
        public string Status { get; }
        public TOperationOutcome OperationOutcome { get; }

        public ScratchpadDeleteResponse(string status, TOperationOutcome outcome)
        {
            Status = status;
            OperationOutcome = outcome;
        }
    }
}
