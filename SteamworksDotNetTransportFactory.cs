using LiteNetLibManager;

namespace SteamworksDotNetTransportLayer
{
    public class SteamworksDotNetTransportFactory : BaseTransportFactory
    {
        public override ITransport Build()
        {
#if UNITY_STANDALONE
            return new SteamworksDotNetTransport();
#else
            Logging.LogError("`SteamworksDotNetTransport` is made to works with standalone platforms only.");
            return null;
#endif
        }
    }
}