using FSO.Client.Regulators;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Screens;
using FSO.Client.Utils;
using FSO.Server.Protocol.Electron.Packets;
using FSO.Server.Protocol.Voltron.Packets;
using System;

namespace FSO.Client.Controllers
{
    public class PersonSelectionEditController : IDisposable
    {
        private PersonSelectionEdit View;
        private CreateASimRegulator CASRegulator;
        private EditASimRegulator EditRegulator;

        public bool Archive;
        /// <summary>
        /// When set, the screen edits this existing avatar's appearance instead of creating a new one.
        /// </summary>
        public FSO.Server.Protocol.CitySelector.AvatarData EditTarget;

        public PersonSelectionEditController(PersonSelectionEdit view, CreateASimRegulator casRegulator, EditASimRegulator editRegulator)
        {
            this.View = view;
            this.CASRegulator = casRegulator;
            this.EditRegulator = editRegulator;

            CASRegulator.OnTransition += CASRegulator_OnTransition;
            EditRegulator.OnTransition += EditRegulator_OnTransition;
            EditRegulator.OnEditSimInfo += EditRegulator_OnEditSimInfo;
        }

        /// <summary>Server told us what a makeover costs and what this sim has. Hand it to the screen so
        /// the price shows and Accept can refuse an unaffordable change up front.</summary>
        private void EditRegulator_OnEditSimInfo(EditSimInfoResponse info)
        {
            Common.Utils.GameThread.NextUpdate(x =>
            {
                View?.SetEditPricing(info.AppearancePrice, info.Budget);
            });
        }

        /// <summary>Ask the server for the edit price + balance. Called once the CAS connection is up and
        /// the screen knows which avatar it is editing.</summary>
        public void RequestEditPricing()
        {
            if (EditTarget != null) EditRegulator.RequestInfo(EditTarget.ID);
        }

        private void CASRegulator_OnTransition(string state, object data)
        {
            Common.Utils.GameThread.NextUpdate(x =>
            {
                switch (state)
                {
                    case "Idle":
                        break;
                    case "CreateSim":
                        break;
                    case "Waiting":
                        //Show waiting dialog
                        View.ShowCreationProgressBar(true);
                        break;
                    case "Error":
                        if (data != null)
                        {
                            var casr = (CreateASimResponse)data;
                            if (casr.Reason == CreateASimFailureReason.NAME_TAKEN)
                            {
                                ShowError(ErrorMessage.FromUIText("222", "4", "5")); //name already in use
                            }
                            else if (casr.Reason == CreateASimFailureReason.NAME_VALIDATION_ERROR)
                            {
                                //don't claim the name is in use when it was actually INVALID (e.g.
                                //digits — usernames allow them, sim names don't).
                                ShowError(ErrorMessage.FromUIText("222", "4", "14")); //name is invalid
                            }
                            else
                            {
                                ShowError(ErrorMessage.FromUIText("222", "4", "7")); //could not create character
                            }
                        }
                        View.ShowCreationProgressBar(false);
                        break;
                    case "Success":
                        //Connect to the city with our new avatar
                        var response = (CreateASimResponse)data;
                        if (Archive)
                        {
                            FSOFacade.Controller.SelectFromCASArchive(response.NewAvatarId);
                        }
                        else
                        {
                            FSOFacade.Controller.ConnectToCity(null, response.NewAvatarId, null);
                        }
                        break;
                }
            });
        }

        private void EditRegulator_OnTransition(string state, object data)
        {
            Common.Utils.GameThread.NextUpdate(x =>
            {
                switch (state)
                {
                    case "Waiting":
                        View.ShowCreationProgressBar(true);
                        break;
                    case "Error":
                        View.ShowCreationProgressBar(false);
                        if (data != null && ((UpdateAvatarAppearanceResponse)data).Reason == UpdateAvatarAppearanceFailureReason.NAME_TAKEN)
                        {
                            ShowError(ErrorMessage.FromUIText("222", "4", "5")); //name already in use
                            break;
                        }
                        if (data != null && ((UpdateAvatarAppearanceResponse)data).Reason == UpdateAvatarAppearanceFailureReason.NAME_VALIDATION_ERROR)
                        {
                            ShowError(ErrorMessage.FromUIText("222", "4", "14")); //name is invalid
                            break;
                        }
                        var message = "Your Sim could not be updated. Please try again later.";
                        if (data != null)
                        {
                            switch (((UpdateAvatarAppearanceResponse)data).Reason)
                            {
                                case UpdateAvatarAppearanceFailureReason.INSUFFICIENT_FUNDS:
                                    message = "Your Sim does not have enough simoleons to pay for this makeover.";
                                    break;
                                case UpdateAvatarAppearanceFailureReason.AVATAR_IN_USE:
                                    message = "This Sim is currently in use. Make sure they are fully logged out, then try again.";
                                    break;
                                case UpdateAvatarAppearanceFailureReason.NAME_CHANGED_RECENTLY:
                                    message = "This Sim was renamed within the last day. Names can only be changed once per day - you can still change their look.";
                                    break;
                            }
                        }
                        ShowError(ErrorMessage.FromLiteral("Edit Sim", message));
                        break;
                    case "Success":
                        //back to Select A Sim; the disconnect flow re-fetches avatar data, so the
                        //refreshed slot doubles as confirmation of the new look
                        View.ShowCreationProgressBar(false);
                        FSOFacade.Controller.Disconnect();
                        break;
                }
            });
        }

        private void ShowError(ErrorMessage errorMsg) {
            /** Error message intended for the user **/
            UIAlertOptions Options = new UIAlertOptions();
            Options.Message = errorMsg.Message;
            Options.Title = errorMsg.Title;
            Options.Buttons = errorMsg.Buttons;
            UIScreen.GlobalShowAlert(Options, true);
        }

        public void Create(){
            var skinTone = Server.Protocol.Voltron.Model.SkinTone.LIGHT;
            switch (View.AppearanceType)
            {
                case Vitaboy.AppearanceType.Medium:
                    skinTone = Server.Protocol.Voltron.Model.SkinTone.MEDIUM;
                    break;
                case Vitaboy.AppearanceType.Dark:
                    skinTone = Server.Protocol.Voltron.Model.SkinTone.DARK;
                    break;
            }

            if (EditTarget != null)
            {
                EditRegulator.UpdateSim(new UpdateAvatarAppearanceRequest
                {
                    AvatarId = EditTarget.ID,
                    BodyOutfitId = (uint)(View.BodyOutfitId >> 32),
                    HeadOutfitId = (uint)(View.HeadOutfitId >> 32),
                    Name = View.Name,
                    Description = View.Description,
                    Gender = View.Gender == Gender.Male ? Server.Protocol.Voltron.Model.Gender.MALE : Server.Protocol.Voltron.Model.Gender.FEMALE,
                    SkinTone = skinTone
                });
                return;
            }

            var packet = new RSGZWrapperPDU
            {
                BodyOutfitId = (uint)(View.BodyOutfitId >> 32),
                HeadOutfitId = (uint)(View.HeadOutfitId >> 32),
                Name = View.Name,
                Description = View.Description,
                Gender = View.Gender == Gender.Male ? Server.Protocol.Voltron.Model.Gender.MALE : Server.Protocol.Voltron.Model.Gender.FEMALE,
                SkinTone = skinTone
            };

            CASRegulator.CreateSim(packet);
        }

        public void Cancel(){
            if (CASRegulator.CurrentState.Name == "Idle" && EditRegulator.CurrentState.Name == "Idle")
            {
                //Cant cancel while cas in progress
                FSOFacade.Controller.Disconnect();
            }
        }

        public void Dispose()
        {
            CASRegulator.OnTransition -= CASRegulator_OnTransition;
            EditRegulator.OnTransition -= EditRegulator_OnTransition;
        }
    }
}
