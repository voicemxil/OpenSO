using FSO.Server.Clients;
using FSO.Server.Clients.Framework;
using FSO.Server.Protocol.Electron.Packets;
using Ninject;

namespace FSO.Client.Regulators
{
    /// <summary>
    /// Drives an appearance edit for an existing avatar over the CAS city connection.
    /// Same shape as CreateASimRegulator, but for UpdateAvatarAppearanceRequest/Response.
    /// </summary>
    public class EditASimRegulator : AbstractRegulator, IAriesMessageSubscriber
    {
        private AriesClient City;

        public EditASimRegulator([Named("City")] AriesClient cityClient)
        {
            this.City = cityClient;
            this.City.AddSubscriber(this);

            AddState("Idle")
                .Default()
                .Transition()
                .OnData(typeof(UpdateAvatarAppearanceRequest))
                    .TransitionTo("UpdateSim");

            AddState("UpdateSim").OnlyTransitionFrom("Idle");
            AddState("Waiting")
                .OnlyTransitionFrom("UpdateSim")
                .OnData(typeof(UpdateAvatarAppearanceResponse))
                .TransitionTo("ProcessResponse");

            AddState("ProcessResponse").OnlyTransitionFrom("Waiting");
            AddState("Error").OnlyTransitionFrom("ProcessResponse");
            AddState("Success").OnlyTransitionFrom("ProcessResponse");
        }

        ~EditASimRegulator(){
            this.City.RemoveSubscriber(this);
        }

        public void UpdateSim(UpdateAvatarAppearanceRequest packet){
            this.AsyncProcessMessage(packet);
        }

        /// <summary>Raised when the server answers with the edit price / balance. Deliberately outside the
        /// state machine: this is a read-only query that can arrive at any time and must not disturb an
        /// edit in flight.</summary>
        public event System.Action<EditSimInfoResponse> OnEditSimInfo;

        /// <summary>Ask what an edit costs and what this sim can afford, so CAS can price the change up
        /// front instead of failing on Accept.</summary>
        public void RequestInfo(uint avatarId)
        {
            City.Write(new EditSimInfoRequest { AvatarId = avatarId });
        }

        protected override void OnAfterTransition(RegulatorState oldState, RegulatorState newState, object data)
        {
        }

        protected override void OnBeforeTransition(RegulatorState oldState, RegulatorState newState, object data)
        {
            switch (newState.Name)
            {
                case "UpdateSim":
                    var packet = (UpdateAvatarAppearanceRequest)data;
                    City.Write(packet);
                    AsyncTransition("Waiting");
                    break;
                case "ProcessResponse":
                    var response = (UpdateAvatarAppearanceResponse)data;
                    if(response.Status == UpdateAvatarAppearanceStatus.SUCCESS){
                        AsyncTransition("Success", data);
                    }else{
                        AsyncTransition("Error", data);
                    }
                    break;
                case "Error":
                    AsyncReset();
                    break;
                case "Success":
                    AsyncReset();
                    break;
            }
        }

        public void MessageReceived(AriesClient client, object message)
        {
            if(message is UpdateAvatarAppearanceResponse){
                AsyncProcessMessage(message);
            }
            else if (message is EditSimInfoResponse info)
            {
                OnEditSimInfo?.Invoke(info);
            }
        }
    }
}
