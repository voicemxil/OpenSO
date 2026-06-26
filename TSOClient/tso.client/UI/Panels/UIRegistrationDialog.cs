using System;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Server.Clients;
using Microsoft.Xna.Framework;

namespace FSO.Client.UI.Panels
{
    /// <summary>
    /// In-client account creation. Step 1: enter an email -> server emails a 6-digit code. Step 2: enter the
    /// code + a username + password to finish. A "Resend code" button re-requests (server-side cooldown). The
    /// website link flow still works in parallel - the same code rides in the emailed confirmation link.
    /// </summary>
    public class UIRegistrationDialog : UIDialog
    {
        private readonly RegistrationClient Client;
        private readonly Action<string> OnRegistered; // optional: hand the new username back to the login form
        private const string CONFIRM_URL = "https://openso.org/confirm.html?token=%token%";

        private string PendingEmail;
        private bool Busy;

        private UILabel MsgLabel;
        private UILabel EmailLabel, CodeLabel, UserLabel, PassLabel;
        private UITextEdit EmailField, CodeField, UserField, PassField;
        private UIButton ActionBtn, ResendBtn;

        public UIRegistrationDialog(Action<string> onRegistered = null)
            : base(UIDialogStyle.Standard | UIDialogStyle.Close, true)
        {
            OnRegistered = onRegistered;
            Client = new RegistrationClient(GlobalSettings.Default.GameEntryUrl);
            Caption = "Create an account";
            SetSize(360, 300);
            if (CloseButton != null) CloseButton.OnButtonClick += _ => UIScreen.RemoveDialog(this);

            MsgLabel = new UILabel { X = 24, Y = 36, Wrapped = true };
            MsgLabel.Size = new Vector2(312, 46);
            Add(MsgLabel);

            EmailLabel = AddLabel("Email", 88);
            EmailField = AddField(106, false, 64);

            CodeLabel  = AddLabel("Verification code (from your email)", 88);
            CodeField  = AddField(106, false, 6);
            UserLabel  = AddLabel("Username", 144);
            UserField  = AddField(162, false, 24);
            PassLabel  = AddLabel("Password", 200);
            PassField  = AddField(218, true, 64);

            ActionBtn = new UIButton { X = 24, Y = 258, Width = 150, Caption = "Send code" };
            ActionBtn.OnButtonClick += Action_Click;
            Add(ActionBtn);

            ResendBtn = new UIButton { X = 206, Y = 258, Width = 130, Caption = "Resend code" };
            ResendBtn.OnButtonClick += Resend_Click;
            Add(ResendBtn);

            ShowStep1();
            if (!FSOEnvironment.SoftwareKeyboard) GameFacade.Screens.inputManager.SetFocus(EmailField);
        }

        private UILabel AddLabel(string text, int y)
        {
            var l = new UILabel { Caption = text, X = 24, Y = y };
            Add(l);
            return l;
        }

        private UITextEdit AddField(int y, bool password, int maxChars)
        {
            var f = UITextEdit.CreateTextBox();
            f.X = 24; f.Y = y; f.SetSize(312, 27);
            f.MaxChars = maxChars; f.Password = password;
            Add(f);
            return f;
        }

        private void ShowStep1()
        {
            EmailLabel.Visible = EmailField.Visible = true;
            CodeLabel.Visible = CodeField.Visible = false;
            UserLabel.Visible = UserField.Visible = false;
            PassLabel.Visible = PassField.Visible = false;
            ResendBtn.Visible = false;
            ActionBtn.Caption = "Send code";
            MsgLabel.Caption = "Enter your email and we'll send you a 6-digit verification code.";
        }

        private void ShowStep2()
        {
            EmailLabel.Visible = EmailField.Visible = false;
            CodeLabel.Visible = CodeField.Visible = true;
            UserLabel.Visible = UserField.Visible = true;
            PassLabel.Visible = PassField.Visible = true;
            ResendBtn.Visible = true;
            ActionBtn.Caption = "Create account";
        }

        private void SetBusy(bool busy, string msg)
        {
            Busy = busy;
            ActionBtn.Disabled = busy;
            ResendBtn.Disabled = busy;
            if (msg != null) MsgLabel.Caption = msg;
        }

        private void Action_Click(UIElement btn)
        {
            if (Busy) return;
            if (EmailField.Visible) // step 1: request a code
            {
                var email = EmailField.CurrentText.Trim();
                if (email.Length < 3 || !email.Contains("@")) { MsgLabel.Caption = "Please enter a valid email address."; return; }
                PendingEmail = email;
                SetBusy(true, "Sending code...");
                _ = Client.RequestCode(email, CONFIRM_URL, OnRequestResult);
            }
            else // step 2: confirm
            {
                var code = CodeField.CurrentText.Trim();
                var user = UserField.CurrentText.Trim();
                var pass = PassField.CurrentText;
                if (code.Length == 0) { MsgLabel.Caption = "Enter the code from your email."; return; }
                if (user.Length < 3) { MsgLabel.Caption = "Username must be at least 3 characters."; return; }
                if (pass.Length == 0) { MsgLabel.Caption = "Please choose a password."; return; }
                SetBusy(true, "Creating account...");
                _ = Client.ConfirmCode(code, user, pass, OnConfirmResult);
            }
        }

        private void Resend_Click(UIElement btn)
        {
            if (Busy || PendingEmail == null) return;
            SetBusy(true, "Resending code...");
            _ = Client.RequestCode(PendingEmail, CONFIRM_URL, OnResendResult);
        }

        private void OnRequestResult(RegistrationResult r)
        {
            SetBusy(false, null);
            if (r.Success)
            {
                ShowStep2();
                MsgLabel.Caption = "We emailed a code to " + PendingEmail + ". Enter it below with the username and password you'd like.";
                if (!FSOEnvironment.SoftwareKeyboard) GameFacade.Screens.inputManager.SetFocus(CodeField);
            }
            else MsgLabel.Caption = FriendlyError(r.Error);
        }

        private void OnResendResult(RegistrationResult r)
        {
            SetBusy(false, r.Success ? ("A new code is on its way to " + PendingEmail + ".") : FriendlyError(r.Error));
        }

        private void OnConfirmResult(RegistrationResult r)
        {
            SetBusy(false, null);
            if (r.Success)
            {
                var newUser = UserField.CurrentText.Trim();
                // Collapse the form; turn the action button into a Close.
                CodeLabel.Visible = CodeField.Visible = false;
                UserLabel.Visible = UserField.Visible = false;
                PassLabel.Visible = PassField.Visible = false;
                ResendBtn.Visible = false;
                MsgLabel.Caption = "Your account '" + newUser + "' is ready! Close this window and sign in.";
                ActionBtn.Caption = "Close";
                ActionBtn.OnButtonClick -= Action_Click;
                ActionBtn.OnButtonClick += _ => { OnRegistered?.Invoke(newUser); UIScreen.RemoveDialog(this); };
            }
            else MsgLabel.Caption = FriendlyError(r.Error);
        }

        private static string FriendlyError(string code)
        {
            switch (code)
            {
                case "resend_cooldown": return "Please wait a moment before requesting another code.";
                case "too_many_attempts": return "Too many incorrect codes. Please wait a few minutes, then try again.";
                case "invalid_token": return "That code isn't right (or it expired). Double-check your email, or resend.";
                case "email_taken": return "That email already has an account.";
                case "email_invalid": return "That doesn't look like a valid email address.";
                case "user_short": return "Username must be at least 3 characters.";
                case "user_long": return "Username must be 24 characters or fewer.";
                case "user_invalid": return "Usernames can only use lowercase letters, numbers, and underscores.";
                case "user_exists": return "That username is taken - please pick another.";
                case "pass_required": return "Please choose a password.";
                case "registrations_too_frequent": return "You registered very recently - please wait a bit.";
                case "ip_banned": return "Registration isn't available from your connection.";
                case "email_failed": return "We couldn't send the email just now. Please try again shortly.";
                case "smtp_disabled": return "Email verification isn't available right now.";
                case "missing_fields": return "Please fill in all the fields.";
                case "network_error": return "Couldn't reach the server. Check your connection and try again.";
                default: return "Something went wrong. Please try again.";
            }
        }
    }
}
