using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Electron.Packets
{
    /// <summary>
    /// Asks what an appearance edit would cost and what the sim can afford, so CAS can show the price
    /// and refuse an unaffordable change before the player commits to it. Sent over the anonymous CAS
    /// connection, like UpdateAvatarAppearanceRequest.
    ///
    /// The balance is only ever returned for an avatar the requesting account owns - see
    /// EditSimInfoHandler. It exists because nothing else reaches the client: fso_avatars.budget is
    /// exposed by no web endpoint and no DataService field, and the CAS session has no selected avatar.
    /// </summary>
    public class EditSimInfoRequest : AbstractElectronPacket
    {
        public uint AvatarId { get; set; }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.EditSimInfoRequest;
        }

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            AvatarId = input.GetUInt32();
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutUInt32(AvatarId);
        }
    }
}
