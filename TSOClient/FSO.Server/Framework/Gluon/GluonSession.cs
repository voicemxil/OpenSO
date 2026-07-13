using FSO.Server.Framework.Aries;
using System.Collections.Generic;
using FSO.Common.Security;

namespace FSO.Server.Framework.Gluon
{
    public class GluonSession : AriesSession, IGluonSession
    {
        public GluonSession(AriesTransportSession ioSession) : base(ioSession)
        {
        }

        public string CallSign { get; set; }

        public string InternalHost { get; set; }

        public string PublicHost { get; set; }

        public bool HasModerationLevel(int threshold)
        {
            return true;
        }


        public void DemandAvatar(uint id, AvatarPermissions permission)
        {
        }

        public void DemandAvatars(IEnumerable<uint> id, AvatarPermissions permission)
        {
        }

        public void DemandInternalSystem()
        {
        }
    }
}
