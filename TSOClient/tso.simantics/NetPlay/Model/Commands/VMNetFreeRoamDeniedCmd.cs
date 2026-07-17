using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// Sent DIRECTLY (single client) by the lot server when a free-roam lot transition is denied by
    /// the target lot's admit rules (admit list / ban list / ban all). Presentation-only: the client
    /// shows the same "not admitted" dialog it shows when a map join returns NO_ADMIT.
    /// </summary>
    public class VMNetFreeRoamDeniedCmd : VMNetCommandBodyAbstract
    {
        public override bool AcceptFromClient { get { return false; } }

        public override bool Execute(VM vm)
        {
            vm.SignalGenericVMEvt(VMEventType.TSOFreeRoamDenied, null);
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return !FromNet; //server only
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
        }
        #endregion
    }
}
