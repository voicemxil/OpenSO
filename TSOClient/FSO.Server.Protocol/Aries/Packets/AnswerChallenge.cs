using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Aries.Packets
{
    public class AnswerChallenge : IAriesPacket
    {
        public string Answer;

        public void Deserialize(IoBuffer input, ISerializationContext context)
        {
            Answer = input.GetPascalVLCString();
        }

        public AriesPacketType GetPacketType()
        {
            return AriesPacketType.AnswerChallenge;
        }

        public void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutPascalVLCString(Answer);
        }
    }
}
