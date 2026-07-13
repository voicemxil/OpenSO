using FSO.Common.Serialization;
using FSO.Server.Protocol.Electron.Model;

namespace FSO.Server.Protocol.Electron.Packets
{
    public class ArchiveModerationRequest : AbstractElectronPacket
    {
        public ArchiveModerationRequestType Type;
        public uint EntityId;
        public int Value;

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            Type = input.GetEnum<ArchiveModerationRequestType>();
            EntityId = input.GetUInt32();
            Value = input.GetInt32();
        }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.ArchiveModerationRequest;
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutEnum(Type);
            output.PutUInt32(EntityId);
            output.PutInt32(Value);
        }
    }
}
