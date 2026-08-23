using System;
using System.Threading;
using System.Threading.Tasks;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.FormFiller.WebView2.Tests.Fakes
{
    /// <summary>Message builders and waits shared by the SMART Web Messaging tests.</summary>
    internal static class SwmTest
    {
        /// <summary>A page→host status.handshake envelope; payload defaults to the legacy empty payload.</summary>
        public static string Handshake(string id, string payloadJson = "{}") => $@"{{
            ""messageId"": ""{id}"",
            ""messagingHandle"": ""smart-web-messaging"",
            ""messageType"": ""status.handshake"",
            ""payload"": {payloadJson}
        }}";

        /// <summary>Awaits with the tests' standard 5s deadline.</summary>
        public static Task Within5s(this Task task)
            => task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }
}
