using FSO.Client.Controllers;
using FSO.Client.UI.Controls;
using FSO.Common;
using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework;

namespace FSO.Client.UI.Archive
{
    internal class UIArchiveJoinDialog : UIDialog
    {
        public UITextBox NameInput;
        public UITextBox AddressInput;
        public UIButton JoinButton;

        public UIArchiveJoinDialog() : base(UIDialogStyle.Close, true)
        {
            Caption = "Join Server";
            var vbox = new UIVBoxContainer();

            var clientConfig = ClientArchiveConfiguration.Default;

            vbox.Add(new UILabel()
            {
                Caption = "Join a server hosted by another player by using their IP address or URL. Depending on the server settings, you might need an admin to verify you, so use a display name that makes it clear who you are.",
                Size = new Vector2(300, 100),
                Wrapped = true
            });

            vbox.Add(new UILabel()
            {
                Caption = "Display name:"
            });

            vbox.Add(NameInput = new UITextBox()
            {
                Size = new Microsoft.Xna.Framework.Vector2(150, 25)
            });

            vbox.Add(new UILabel()
            {
                Caption = "Server address:"
            });

            vbox.Add(AddressInput = new UITextBox()
            {
                Size = new Microsoft.Xna.Framework.Vector2(300, 25)
            });

            var vbox2 = new UIVBoxContainer() { HorizontalAlignment = UIContainerHorizontalAlignment.Right };

            vbox2.Add(JoinButton = new UIButton()
            {
                Caption = "Join",
                Disabled = true
            });

            vbox2.AutoSize(); //TODO: somehow force horiz size from parent?

            vbox.Add(vbox2);

            vbox.AutoSize();
            vbox.Position = new Vector2(20, 45);

            SetSize((int)vbox.Size.X + 40, (int)vbox.Size.Y + 70);

            Add(vbox);

            NameInput.CurrentText = clientConfig.PlayerName;
            AddressInput.CurrentText = clientConfig.LastJoinedHost;

            NameInput.OnChange += ValidateInputs;
            AddressInput.OnChange += ValidateInputs;

            JoinButton.OnButtonClick += Join;
            CloseButton.OnButtonClick += Close;

            ValidateInputs(NameInput);
        }

        private void SaveConfig()
        {
            var clientConfig = ClientArchiveConfiguration.Default;

            clientConfig.PlayerName = NameInput.CurrentText;
            clientConfig.LastJoinedHost = AddressInput.CurrentText;
            clientConfig.Save();
        }

        private void Close(Framework.UIElement button)
        {
            SaveConfig();
            FindController<ConnectArchiveController>().SwitchMode(ConnectArchiveMode.Landing);
        }

        private void Join(Framework.UIElement button)
        {
            SaveConfig(); 
            FSOFacade.Controller.ConnectToArchive(NameInput.CurrentText, AddressInput.CurrentText, false);
        }

        private void ValidateInputs(Framework.UIElement element)
        {
            JoinButton.Disabled = NameInput.CurrentText.Length == 0 || AddressInput.CurrentText.Length == 0;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            FindController<ConnectArchiveController>().TickRPC();
        }
    }
}
