using System;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;

namespace FSO.Client.UI.Panels
{
    public class UILoginDialog : UIDialog
    {
        private UITextEdit m_TxtAccName, m_TxtPass;
        private Action Login;

        public UILoginDialog(Action login)
            : base(UIDialogStyle.Standard, true)
        {
            this.Login = login;
            this.Caption = GameFacade.Strings.GetString("UIText", "209", "1");

            SetSize(350, 290);

            m_TxtAccName = UITextEdit.CreateTextBox();
            m_TxtAccName.X = 20;
            m_TxtAccName.Y = 72;
            m_TxtAccName.MaxChars = 32;
            m_TxtAccName.SetSize(310, 27);
            m_TxtAccName.CurrentText = GlobalSettings.Default.LastUser;
            m_TxtAccName.OnChange += M_TxtAccName_OnChange;
            m_TxtAccName.OnEnterPress += new KeyPressDelegate(loginBtn_OnButtonClick);

            this.Add(m_TxtAccName);

            m_TxtPass = UITextEdit.CreateTextBox();
            m_TxtPass.X = 20;
            m_TxtPass.Y = 128;
            m_TxtPass.MaxChars = 64;
            m_TxtPass.SetSize(310, 27);
            m_TxtPass.Password = true;
            m_TxtPass.OnChange += M_TxtAccName_OnChange;
            m_TxtPass.OnEnterPress += new KeyPressDelegate(loginBtn_OnButtonClick);
            this.Add(m_TxtPass);

            // Three uniform full-width buttons stacked in rows, aligned with the input fields (X=20, W=310):
            // Login (primary) -> Create account (onboarding) -> Exit (leave).
            /** Login button **/
            var loginBtn = new UIButton
            {
                X = 20,
                Y = 170,
                Width = 310,
                ID = "LoginButton",
                Caption = GameFacade.Strings.GetString("UIText", "209", "2")
            };
            this.Add(loginBtn);
            loginBtn.OnButtonClick += new ButtonClickDelegate(loginBtn_OnButtonClick);

            var exitBtn = new UIButton
            {
                X = 20,
                Y = 242,
                Width = 310,
                ID = "ExitButton",
                Caption = GameFacade.Strings.GetString("UIText", "209", "3")
            };
            this.Add(exitBtn);
            exitBtn.OnButtonClick += new ButtonClickDelegate(exitBtn_OnButtonClick);

            // In-client account creation (email -> 6-digit code -> username/password). Pre-fills this dialog's
            // username field on success so the player can sign straight in.
            var createBtn = new UIButton
            {
                X = 20,
                Y = 206,
                Width = 310,
                ID = "CreateAccountButton",
                Caption = "Create account"
            };
            this.Add(createBtn);
            createBtn.OnButtonClick += _ => UIScreen.GlobalShowDialog(new UIRegistrationDialog(u => Username = u), true);

            this.Add(new UILabel
            {
                Caption = GameFacade.Strings.GetString("UIText", "209", "4"),
                X = 24,
                Y = 50
            });

            this.Add(new UILabel
            {
                Caption = GameFacade.Strings.GetString("UIText", "209", "5"),
                X = 24,
                Y = 106
            });

            if (!FSOEnvironment.SoftwareKeyboard) GameFacade.Screens.inputManager.SetFocus(m_TxtAccName);
            RefreshBlink();
        }

        private void M_TxtAccName_OnChange(UIElement element)
        {
            RefreshBlink();
        }

        private void RefreshBlink()
        {
            if(m_TxtAccName.CurrentText.Length == 0)
            {
                m_TxtAccName.FlashOnEmpty = true;
                m_TxtPass.FlashOnEmpty = false;
            }
            else
            {
                m_TxtAccName.FlashOnEmpty = false;
                m_TxtPass.FlashOnEmpty = true;
            }
        }

        public void FocusUsername()
        {
            GameFacade.Screens.inputManager.SetFocus(m_TxtAccName);
        }

        public void FocusPassword()
        {
            GameFacade.Screens.inputManager.SetFocus(m_TxtPass);
        }

        public void ClearPassword()
        {
            m_TxtPass.CurrentText = "";
        }

        public string Username
        {
            get
            {
                return m_TxtAccName.CurrentText;
            }
            set
            {
                m_TxtAccName.CurrentText = value;
            }
        }

        public string Password
        {
            get
            {
                return m_TxtPass.CurrentText;
            }
        }

        void loginBtn_OnButtonClick(UIElement button)
        {
            Login();
            //FSOFacade.Controller.ShowPersonSelection();
        }

        void exitBtn_OnButtonClick(UIElement button)
        {
            if (FSOFacade.Controller.CloseAttempt())
            {
                GameFacade.Kill();
            }
            /*var exitDialog = new UIExitDialog();
            Parent.Add(exitDialog);*/
        }
    }
}
