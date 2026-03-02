using System;
using Tiro.Health.SmartWebMessaging.Message;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging.Events
{
    /// <summary>
    /// Triggered when a status.handshake message is received.
    /// </summary>
    public class HandshakeReceivedEventArgs : EventArgs
    {
        public SmartMessageRequest Message { get; }
        public RequestPayload Payload { get; }

        public HandshakeReceivedEventArgs(SmartMessageRequest message, RequestPayload payload)
        {
            Message = message;
            Payload = payload;
        }
    }
}
