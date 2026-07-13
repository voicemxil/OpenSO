using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Electron.Packets
{
    public class VerificationNotification : AbstractElectronPacket
    {
        public bool IsVerified;

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            IsVerified = input.GetBool();
        }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.VerificationNotification;
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutBool(IsVerified);
        }
    }
}
