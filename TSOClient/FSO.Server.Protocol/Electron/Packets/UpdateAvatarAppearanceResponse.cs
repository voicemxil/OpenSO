using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Electron.Packets
{
    public class UpdateAvatarAppearanceResponse : AbstractElectronPacket
    {
        public UpdateAvatarAppearanceStatus Status { get; set; }
        public UpdateAvatarAppearanceFailureReason Reason { get; set; } = UpdateAvatarAppearanceFailureReason.NONE;

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.UpdateAvatarAppearanceResponse;
        }

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            Status = input.GetEnum<UpdateAvatarAppearanceStatus>();
            Reason = input.GetEnum<UpdateAvatarAppearanceFailureReason>();
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutEnum<UpdateAvatarAppearanceStatus>(Status);
            output.PutEnum<UpdateAvatarAppearanceFailureReason>(Reason);
        }
    }

    public enum UpdateAvatarAppearanceStatus
    {
        SUCCESS = 0x01,
        FAILED = 0x02
    }

    public enum UpdateAvatarAppearanceFailureReason
    {
        NONE = 0x00,
        NOT_OWNER = 0x01,
        AVATAR_IN_USE = 0x02,
        HEAD_VALIDATION_ERROR = 0x03,
        BODY_VALIDATION_ERROR = 0x04,
        DESC_VALIDATION_ERROR = 0x05,
        INSUFFICIENT_FUNDS = 0x06,
        NAME_VALIDATION_ERROR = 0x07,
        NAME_TAKEN = 0x08,
        /// <summary>Renamed within the last day. Renaming is free, so it is rate limited instead.</summary>
        NAME_CHANGED_RECENTLY = 0x09
    }
}
