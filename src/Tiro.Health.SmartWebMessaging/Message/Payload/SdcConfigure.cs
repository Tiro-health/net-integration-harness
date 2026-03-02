namespace Tiro.Health.SmartWebMessaging.Message.Payload
{
    public class SdcConfigure : RequestPayload
    {
        public string TerminologyServer { get; set; }

        public string DataServer { get; set; }

        public object Configuration { get; set; }

        public SdcConfigure()
        {
        }

        public SdcConfigure(string terminologyServer = null, string dataServer = null, object configuration = null)
        {
            TerminologyServer = terminologyServer;
            DataServer = dataServer;
            Configuration = configuration;
        }
    }
}
