using LiteNetLibManager;

namespace SteamworksDotNetTransportLayer
{
    public class SteamworksDotNetTransportFactory : BaseTransportFactory
    {
        public override ITransport Build()
        {
            return new SteamworksDotNetTransport();
        }
    }
}