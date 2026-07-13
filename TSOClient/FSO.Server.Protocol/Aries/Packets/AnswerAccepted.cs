using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Aries.Packets
{
    public class AnswerAccepted : IAriesPacket
    {
        public void Deserialize(IoBuffer input, ISerializationContext context)
        {
        }

        public AriesPacketType GetPacketType()
        {
            return AriesPacketType.AnswerAccepted;
        }

        public void Serialize(IoBuffer output, ISerializationContext context)
        {
        }
    }
}
