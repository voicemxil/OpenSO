using FSO.Common.Serialization;

namespace FSO.Server.Protocol.Electron.Packets
{
    /// <summary>
    /// What an appearance edit costs, and what this sim can pay. Answers <see cref="EditSimInfoRequest"/>.
    ///
    /// The price is authoritative - it comes from the server's 'edit_sim' tuning row, so the CAS label
    /// can never drift from what is actually charged. Budget lets CAS grey out Accept instead of letting
    /// the player fill in a new look and only then be told they can't afford it. Neither value is
    /// trusted for the charge itself: UpdateAvatarAppearanceHandler re-reads the price and the debit is
    /// still guarded by budget >= cost in SQL.
    /// </summary>
    public class EditSimInfoResponse : AbstractElectronPacket
    {
        /// <summary>Cost of changing the sim's look, in simoleons. 0 means the makeover is free.</summary>
        public int AppearancePrice { get; set; }
        /// <summary>The sim's current simoleons.</summary>
        public int Budget { get; set; }
        /// <summary>False when this sim was renamed within the rename cooldown, so CAS can say so up
        /// front rather than after the player has typed a new name.</summary>
        public bool CanRename { get; set; }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.EditSimInfoResponse;
        }

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            AppearancePrice = input.GetInt32();
            Budget = input.GetInt32();
            CanRename = input.Get() != 0;
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutInt32(AppearancePrice);
            output.PutInt32(Budget);
            output.Put(CanRename ? (byte)1 : (byte)0);
        }
    }
}
