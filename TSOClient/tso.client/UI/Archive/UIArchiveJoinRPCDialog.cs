using FSO.Client.Controllers;
using FSO.Client.UI.Controls;
using FSO.Common;
using FSO.Common.Rendering.Framework.Model;
using FSO.UI.Model;
using Microsoft.Xna.Framework;

namespace FSO.Client.UI.Archive
{
    internal class UIArchiveJoinRPCDialog : UIDialog
    {
        public UITextBox NameInput;
        public UIButton JoinButton;

        public UIArchiveJoinRPCDialog() : base(UIDialogStyle.Close, true)
        {
            Caption = GameFacade.Strings.GetString("f128", "117");
            var vbox = new UIVBoxContainer() { HorizontalAlignment = UIContainerHorizontalAlignment.Right };

            var clientConfig = ClientArchiveConfiguration.Default;

            vbox.Add(new UILabel()
            {
                Caption = GameFacade.Strings.GetString("f128", "118"),
                Size = new Vector2(300, 35),
                Wrapped = true
            });

            vbox.Add(NameInput = new UITextBox()
            {
                Size = new Microsoft.Xna.Framework.Vector2(300, 25)
            });

            vbox.Add(JoinButton = new UIButton()
            {
                Caption = "Join",
                Disabled = true
            });

            vbox.AutoSize();
            vbox.Position = new Vector2(20, 45);

            SetSize((int)vbox.Size.X + 40, (int)vbox.Size.Y + 70);

            Add(vbox);

            NameInput.CurrentText = clientConfig.PlayerName;
            NameInput.OnChange += ValidateInputs;

            JoinButton.OnButtonClick += Join;
            CloseButton.OnButtonClick += Close;

            ValidateInputs(NameInput);
        }

        private void SaveConfig()
        {
            var clientConfig = ClientArchiveConfiguration.Default;

            clientConfig.PlayerName = NameInput.CurrentText;
            clientConfig.Save();
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            var rpc = DiscordRpcEngine.Secret;

            if (rpc == null || !rpc.Value.ArchiveMode || string.IsNullOrEmpty(rpc.Value.ServerHostname))
            {
                FindController<ConnectArchiveController>().SwitchMode(ConnectArchiveMode.Landing);
            }
        }

        private void Close(Framework.UIElement button)
        {
            SaveConfig();
            DiscordRpcEngine.Secret = null;
            FindController<ConnectArchiveController>().SwitchMode(ConnectArchiveMode.Landing);
        }

        private void Join(Framework.UIElement button)
        {
            SaveConfig();
            var rpc = DiscordRpcEngine.Secret;
            FSOFacade.Controller.ConnectToArchive(NameInput.CurrentText, rpc.Value.ServerHostname, false);
        }

        private void ValidateInputs(Framework.UIElement element)
        {
            JoinButton.Disabled = NameInput.CurrentText.Length == 0;
        }
    }
}
