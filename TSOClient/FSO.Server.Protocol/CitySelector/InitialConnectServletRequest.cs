namespace FSO.Server.Protocol.CitySelector
{
    public class InitialConnectServletRequest
    {
        public string Ticket;
        public string Version;
        // Client runtime identifier (e.g. "win-x64", "osx-arm64"). Lets the server return a platform-correct
        // update payload: only Windows clients receive the win-x64 in-game patch URL. Null on legacy clients.
        public string RID;
    }
}
