using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Server.Clients;
using Microsoft.Xna.Framework;
using Ninject;

namespace FSO.Client.UI.Panels
{
    internal class UIServerConnectDialog : UIDialog
    {
        public UITextBox AddressInput;
        public UIButton ConnectButton;

        public UIServerConnectDialog() : base(UIDialogStyle.Close, true)
        {
            Caption = "Connect to Server";
            var vbox = new UIVBoxContainer();

            vbox.Add(new UILabel()
            {
                Caption = "Enter the server address:"
            });

            vbox.Add(AddressInput = new UITextBox()
            {
                Size = new Vector2(350, 25)
            });

            var vbox2 = new UIVBoxContainer() { HorizontalAlignment = UIContainerHorizontalAlignment.Right };

            vbox2.Add(ConnectButton = new UIButton()
            {
                Caption = "Connect",
                Disabled = true
            });

            vbox2.AutoSize();

            vbox.Add(vbox2);

            vbox.AutoSize();
            vbox.Position = new Vector2(20, 45);

            SetSize((int)vbox.Size.X + 40, (int)vbox.Size.Y + 70);

            Add(vbox);

            AddressInput.CurrentText = GlobalSettings.Default.GameEntryUrl ?? "";

            AddressInput.OnChange += ValidateInputs;
            AddressInput.OnEnterPress += OnEnterPress;
            ConnectButton.OnButtonClick += Connect;
            CloseButton.OnButtonClick += Close;

            ValidateInputs(AddressInput);
        }

        private void Close(UIElement button)
        {
            UIScreen.RemoveDialog(this);
        }

        private void OnEnterPress(UIElement element)
        {
            if (!ConnectButton.Disabled)
            {
                Connect(ConnectButton);
            }
        }

        private void Connect(UIElement button)
        {
            var url = AddressInput.CurrentText;
            GlobalSettings.Default.GameEntryUrl = url;
            GlobalSettings.Default.CitySelectorUrl = url;
            var auth = FSOFacade.Kernel.Get<AuthClient>();
            auth.SetBaseUrl(url);
            var city = FSOFacade.Kernel.Get<CityClient>();
            city.SetBaseUrl(url);
            GlobalSettings.Default.Save();

            UIScreen.RemoveDialog(this);
            FSOFacade.Controller.ShowServerLogin();
        }

        private void ValidateInputs(UIElement element)
        {
            ConnectButton.Disabled = AddressInput.CurrentText.Length == 0;
        }
    }
}
