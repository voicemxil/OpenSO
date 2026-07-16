using FSO.Common.Serialization;
using FSO.Server.Protocol.Voltron.Model;

namespace FSO.Server.Protocol.Electron.Packets
{
    /// <summary>
    /// Requests an appearance edit (gender, skin tone, head, body, description) for an avatar owned
    /// by the session's account. Sent over the anonymous CAS connection, like RSGZWrapperPDU.
    /// Deliberately carries no name field — name changes are moderation-only.
    /// </summary>
    public class UpdateAvatarAppearanceRequest : AbstractElectronPacket
    {
        public uint AvatarId { get; set; }
        public Gender Gender { get; set; }
        public SkinTone SkinTone { get; set; }
        //high dword of the 64-bit outfit asset ID, matching RSGZWrapperPDU / RegistrationHandler validation
        public uint HeadOutfitId { get; set; }
        public uint BodyOutfitId { get; set; }
        public string Description { get; set; }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.UpdateAvatarAppearanceRequest;
        }

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            AvatarId = input.GetUInt32();
            Gender = input.Get() == 0x01 ? Gender.FEMALE : Gender.MALE;
            var skin = input.Get();
            switch (skin)
            {
                default:
                case 0x00:
                    SkinTone = SkinTone.LIGHT;
                    break;
                case 0x01:
                    SkinTone = SkinTone.MEDIUM;
                    break;
                case 0x02:
                    SkinTone = SkinTone.DARK;
                    break;
            }
            HeadOutfitId = input.GetUInt32();
            BodyOutfitId = input.GetUInt32();
            Description = input.GetPascalVLCString();
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutUInt32(AvatarId);
            output.Put(Gender == Gender.FEMALE ? (byte)0x01 : (byte)0x00);
            switch (SkinTone)
            {
                case SkinTone.LIGHT:
                    output.Put(0x00);
                    break;
                case SkinTone.MEDIUM:
                    output.Put(0x01);
                    break;
                case SkinTone.DARK:
                    output.Put(0x02);
                    break;
            }
            output.PutUInt32(HeadOutfitId);
            output.PutUInt32(BodyOutfitId);
            output.PutPascalVLCString(Description);
        }
    }
}
