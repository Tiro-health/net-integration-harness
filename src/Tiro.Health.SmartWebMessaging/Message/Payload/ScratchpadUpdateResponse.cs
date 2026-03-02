namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class ScratchpadUpdateResponse<TOperationOutcome> : ResponsePayload
    {
        public string Status { get; }
        public TOperationOutcome OperationOutcome { get; }

        public ScratchpadUpdateResponse(string status, TOperationOutcome outcome)
        {
            Status = status;
            OperationOutcome = outcome;
        }
    }
}
