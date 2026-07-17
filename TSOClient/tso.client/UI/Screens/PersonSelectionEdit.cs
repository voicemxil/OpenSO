using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework.Parser;
using Microsoft.Xna.Framework;
using FSO.Common.Utils;
using FSO.Vitaboy;
using FSO.Client.Controllers;
using System.Text.RegularExpressions;
using FSO.Client.UI.Model;
using FSO.Common;
using FSO.Client.UI.Panels;

namespace FSO.Client.UI.Screens
{
    public class PersonSelectionEdit : GameScreen
    {
        /// <summary>
        /// Must not start with whitespace
        /// May not contain numbers or special characters
        /// At least 3 characters
        /// No more than 24 characters
        /// </summary>
        private static Regex NAME_VALIDATION = new Regex("^([a-zA-Z]){1}([a-zA-Z ]){2,23}$");

        /// <summary>
        /// Only printable ascii characters
        /// Minimum 0 characters
        /// Maximum 499 characters
        /// </summary>
        private static Regex DESC_VALIDATION = new Regex("^([a-zA-Z0-9\\s\\x20-\\x7F]){0,499}$");

        /** UI created by script **/
        public Texture2D BackgroundImage { get; set; }
        public Texture2D BackgroundImageDialog { get; set; }
        public UIButton CancelButton { get; set; }
        public UIButton AcceptButton { get; set; }
        public UIButton SkinLightButton { get; set; }
        public UIButton SkinMediumButton { get; set; }
        public UIButton SkinDarkButton { get; set; }
        public UIButton FemaleButton { get; set; }
        public UIButton MaleButton { get; set; }

        public UITextEdit NameTextEdit { get; set; }
        public UITextEdit DescriptionTextEdit { get; set; }
        public UIButton DescriptionScrollUpButton { get; set; }
        public UIButton DescriptionScrollDownButton { get; set; }
        public UISlider DescriptionSlider { get; set; }
        private UIButton m_ExitButton;

        protected UICollectionViewer m_HeadSkinBrowser;
        protected UICollectionViewer m_BodySkinBrowser;
        
        /** Data **/
        private Collection MaleHeads;
        private Collection MaleOutfits;
        private Collection FemaleHeads;
        private Collection FemaleOutfits;

        /** State **/
        public AppearanceType AppearanceType { get; internal set; } = AppearanceType.Light;
        private UIButton SelectedAppearanceButton;
        public Gender Gender { get; internal set; } = Gender.Female;
        /// <summary>
        /// Set when this screen is editing an existing avatar rather than creating a new one.
        /// The name is locked in this mode — changing it requires contacting moderation.
        /// </summary>
        public bool EditMode { get; private set; }
        
        public UISim SimBox;

        /** Strings **/
        public string ProgressDialogTitle { get; set; }
        public string ProgressDialogMessage { get; set; }
        public string DefaultAvatarDescription { get; set; }

        /// <summary>Reduce a username to a valid default sim name: keep letters and spaces (the only
        /// characters sim names allow), collapse runs of whitespace, and give up (empty field) if
        /// fewer than 3 characters survive — the accept button then stays disabled until a name is
        /// typed, instead of offering a default the server would reject.</summary>
        private static string SanitizeDefaultName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') sb.Append(c);
                else if (c == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            var result = sb.ToString().TrimEnd(' ');
            if (result.Length > 24) result = result.Substring(0, 24).TrimEnd(' ');
            return NAME_VALIDATION.IsMatch(result) ? result : "";
        }

        /// <summary>The original TSO string table (UIText 170, entry 23) is sloppy: a stray leading
        /// space on its "Quote:" line, and trailing spaces after "Fav. music:" and "Quote:" that the
        /// other lines don't have. The table ships in the TSO assets every install downloads, so
        /// clean the template client-side: trim spaces from both ends of each line.</summary>
        private static string SanitizeDefaultDescription(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return desc ?? "";
            var lines = desc.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim(' ', '\r');
            return string.Join("\n", lines);
        }

        public PersonSelectionEdit() : base()
        {
            /**
            * Data
            */
            var content = Content.Content.Get();
            MaleHeads = content.AvatarCollections.Get("ea_male_heads.col");
            MaleOutfits = content.AvatarCollections.Get("ea_male.col");

            FemaleHeads = content.AvatarCollections.Get("ea_female_heads.col");
            FemaleOutfits = content.AvatarCollections.Get("ea_female.col");

            /**
             * UI
             */

            UIScript ui = this.RenderScript("personselectionedit1024.uis");

            Position = new Vector2((GlobalSettings.Default.GraphicsWidth-1024)/2, (GlobalSettings.Default.GraphicsHeight-768)/2) * FSOEnvironment.DPIScaleFactor;

            m_ExitButton = (UIButton)ui["ExitButton"];
            m_ExitButton.OnButtonClick += new ButtonClickDelegate(m_ExitButton_OnButtonClick);

            CancelButton = (UIButton)ui["CancelButton"];
            CancelButton.OnButtonClick += new ButtonClickDelegate(CancelButton_OnButtonClick);
            //CancelButton.Disabled = true;

            DescriptionTextEdit.CurrentText = SanitizeDefaultDescription(ui.GetString("DefaultAvatarDescription"));
            DescriptionSlider.AttachButtons(DescriptionScrollUpButton, DescriptionScrollDownButton, 1);
            DescriptionTextEdit.AttachSlider(DescriptionSlider);
            DescriptionTextEdit.CurrentText = SanitizeDefaultDescription(DefaultAvatarDescription);
            DescriptionTextEdit.OnChange += DescriptionTextEdit_OnChange;

            NameTextEdit.OnChange += new ChangeDelegate(NameTextEdit_OnChange);
            // Default the sim name to the username, sanitized to what sim names ALLOW (letters and
            // spaces only) — usernames may contain digits, and an unsubmittable default that LOOKS
            // fine just round-trips to a server error.
            NameTextEdit.CurrentText = SanitizeDefaultName(GlobalSettings.Default.LastUser);
            GameFacade.Screens.inputManager.SetFocus(NameTextEdit);

            // Gate on full validation from the start: the default value must pass the same rules as
            // typed input, or Accept submits a name the server is guaranteed to reject.
            UpdateAcceptButtonState();
            AcceptButton.OnButtonClick += new ButtonClickDelegate(AcceptButton_OnButtonClick);

            /** Appearance **/
            SkinLightButton.OnButtonClick += new ButtonClickDelegate(SkinButton_OnButtonClick);
            SkinMediumButton.OnButtonClick += new ButtonClickDelegate(SkinButton_OnButtonClick);
            SkinDarkButton.OnButtonClick += new ButtonClickDelegate(SkinButton_OnButtonClick);
            SelectedAppearanceButton = SkinLightButton;

            m_HeadSkinBrowser = ui.Create<UICollectionViewer>("HeadSkinBrowser");
            m_HeadSkinBrowser.OnChange += new ChangeDelegate(HeadSkinBrowser_OnChange);
            m_HeadSkinBrowser.Init();
            this.Add(m_HeadSkinBrowser);

            m_BodySkinBrowser = ui.Create<UICollectionViewer>("BodySkinBrowser");
            m_BodySkinBrowser.OnChange += new ChangeDelegate(BodySkinBrowser_OnChange);
            m_BodySkinBrowser.Init();
            this.Add(m_BodySkinBrowser);

            FemaleButton.OnButtonClick += new ButtonClickDelegate(GenderButton_OnButtonClick);
            MaleButton.OnButtonClick += new ButtonClickDelegate(GenderButton_OnButtonClick);

            /** Backgrounds **/
            var bg = new UIImage(BackgroundImage).With9Slice(128,128, 84, 84);
            this.AddAt(0, bg);
            bg.SetSize(GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight);
            bg.Position = new Vector2((GlobalSettings.Default.GraphicsWidth - 1024) / -2, (GlobalSettings.Default.GraphicsHeight - 768) / -2);
            Background = bg;

            var offset = new Vector2(0, 0);
            if (BackgroundImageDialog != null)
            {
                offset = new Vector2(112, 84);

                DialogBackground = new UIImage(BackgroundImageDialog)
                {
                    X = 112,
                    Y = 84
                };
                this.AddAt(1, DialogBackground);
            }

            /**
             * Music
             */
            HIT.HITVM.Get().PlaySoundEvent(UIMusic.CAS);

            SimBox = new UISim();
            SimBox.Position = new Vector2(offset.X + 70, offset.Y + 88);
            SimBox.Size = new Vector2(140,200);
            SimBox.AutoRotate = true;
            SimBox.Interactive = true; // drag to rotate, scroll to zoom
            this.Add(SimBox);

            /**
             * Init state
             */

            if (GlobalSettings.Default.DebugGender)
            {
                Gender = Gender.Male;
                MaleButton.Selected = true;
                FemaleButton.Selected = false;
            }
            else
            {
                Gender = Gender.Female;
                MaleButton.Selected = false;
                FemaleButton.Selected = true;
            }

            AppearanceType = (AppearanceType)GlobalSettings.Default.DebugSkin;

            SkinLightButton.Selected = false;
            SkinMediumButton.Selected = false;
            SkinDarkButton.Selected = false;

            switch (AppearanceType)
            {
                case AppearanceType.Light:
                    SkinLightButton.Selected = true; break;
                case AppearanceType.Medium:
                    SkinMediumButton.Selected = true; break;
                case AppearanceType.Dark:
                    SkinDarkButton.Selected = true; break;
            }

            RefreshCollections();

            SearchCollectionForInitID(GlobalSettings.Default.DebugHead, GlobalSettings.Default.DebugBody);

            FSO.UI.Model.DiscordRpcEngine.SendFSOPresence("In Create A Sim");

            GameThread.NextUpdate(x =>
            {
                FSOFacade.Hints.TriggerHint("screen:cas");
            });
        }

        /// <summary>
        /// Switches the screen to edit an existing avatar: preselects their current gender, skin tone,
        /// head and body, and locks the name field (name changes go through moderation).
        /// </summary>
        public void SetEditMode(Server.Protocol.CitySelector.AvatarData avatar)
        {
            EditMode = true;

            Gender = avatar.Gender == Server.Protocol.CitySelector.AvatarGender.Male ? Gender.Male : Gender.Female;
            MaleButton.Selected = Gender == Gender.Male;
            FemaleButton.Selected = Gender == Gender.Female;

            AppearanceType = (AppearanceType)Enum.Parse(typeof(AppearanceType), avatar.AppearanceType.ToString());
            SelectedAppearanceButton.Selected = false;
            switch (AppearanceType)
            {
                case AppearanceType.Light:
                    SelectedAppearanceButton = SkinLightButton; break;
                case AppearanceType.Medium:
                    SelectedAppearanceButton = SkinMediumButton; break;
                case AppearanceType.Dark:
                    SelectedAppearanceButton = SkinDarkButton; break;
            }
            SelectedAppearanceButton.Selected = true;

            NameTextEdit.CurrentText = avatar.Name;
            NameTextEdit.Mode = UITextEditMode.ReadOnly;
            NameTextEdit.Tooltip = "Name changes require contacting moderation.";
            DescriptionTextEdit.CurrentText = avatar.Description ?? "";

            RefreshCollections();
            SearchCollectionForInitID(avatar.HeadOutfitID, avatar.BodyOutfitID);
            UpdateAcceptButtonState();

            FSO.UI.Model.DiscordRpcEngine.SendFSOPresence("In Edit A Sim");
            OnEditModeApplied();
        }

        /// <summary>
        /// Hook for subclasses (CASScreenV2) to restyle themselves for edit mode.
        /// </summary>
        protected virtual void OnEditModeApplied()
        {
        }

        protected UIImage Background;
        protected UIImage DialogBackground;
        public override void GameResized()
        {
            base.GameResized();
            Position = new Vector2((GlobalSettings.Default.GraphicsWidth - 1024) / 2, (GlobalSettings.Default.GraphicsHeight - 768) / 2) * FSOEnvironment.DPIScaleFactor;
            Background.SetSize(GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight);
            Background.Position = new Vector2((GlobalSettings.Default.GraphicsWidth - 1024) / -2, (GlobalSettings.Default.GraphicsHeight - 768) / -2);
        }



        private UIAlert _ProgressAlert;

        public void ShowCreationProgressBar(bool show)
        {
            if (show)
            {
                if (_ProgressAlert == null)
                {
                    _ProgressAlert = GlobalShowAlert(new UIAlertOptions
                    {
                        Message = ProgressDialogMessage,
                        Title = ProgressDialogTitle,
                        ProgressBar = true,
                        Width = 300
                    }, true);
                }
            }
            else
            {
                if (_ProgressAlert != null)
                {
                    UIScreen.RemoveDialog(_ProgressAlert);
                    _ProgressAlert = null;
                }
            }
        }

        public override void DeviceReset(GraphicsDevice Device)
        {
            CalculateMatrix();
        }


        public string Name
        {
            get { return NameTextEdit.CurrentText; }
        }

        public string Description
        {
            get { return DescriptionTextEdit.CurrentText; }
        }

        public ulong HeadOutfitId
        {
            get
            {
                var selectedHead = (CollectionItem)((UIGridViewerItem)m_HeadSkinBrowser.SelectedItem).Data;
                var headPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedHead.PurchasableOutfitId);
                return headPurchasable.OutfitID;
            }
        }

        public ulong BodyOutfitId
        {
            get
            {
                var selectedBody = (CollectionItem)((UIGridViewerItem)m_BodySkinBrowser.SelectedItem).Data;
                var bodyPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedBody.PurchasableOutfitId);
                return bodyPurchasable.OutfitID;
            }
        }

        private void m_ExitButton_OnButtonClick(UIElement button)
        {
            if (FSOFacade.Controller.CloseAttempt())
            {
                GameFacade.Kill();
            }
        }

        private void CancelButton_OnButtonClick(UIElement button)
        {
            var control = FindController<PersonSelectionEditController>();
            if (control != null) control.Cancel();
            else
            {
                FSOFacade.Controller.ShowLogin();
            }
        }

        private void AcceptButton_OnButtonClick(UIElement button)
        {
            var control = FindController<PersonSelectionEditController>();
            if (control != null) control.Create();
            else
            {
                var selectedHead = (CollectionItem)((UIGridViewerItem)m_HeadSkinBrowser.SelectedItem).Data;
                var selectedBody = (CollectionItem)((UIGridViewerItem)m_BodySkinBrowser.SelectedItem).Data;
                var headPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedHead.PurchasableOutfitId);
                var bodyPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedBody.PurchasableOutfitId);

                GlobalSettings.Default.DebugBody = bodyPurchasable.OutfitID;
                GlobalSettings.Default.DebugHead = headPurchasable.OutfitID;
                GlobalSettings.Default.LastUser = NameTextEdit.CurrentText;
                GlobalSettings.Default.DebugGender = (Gender == Gender.Male);
                GlobalSettings.Default.DebugSkin = (int)AppearanceType;

                GlobalSettings.Default.Save();

                GlobalShowDialog(new UISandboxSelector(), true);
            }
        }

        private void HeadSkinBrowser_OnChange(UIElement element)
        {
            RefreshSim();
        }

        private void BodySkinBrowser_OnChange(UIElement element)
        {
            RefreshSim();
        }

        private void RefreshSim()
        {
            if(m_HeadSkinBrowser.SelectedItem == null || m_BodySkinBrowser.SelectedItem == null){
                return;
            }
            var selectedHead = (CollectionItem)((UIGridViewerItem)m_HeadSkinBrowser.SelectedItem).Data;
            var selectedBody = (CollectionItem)((UIGridViewerItem)m_BodySkinBrowser.SelectedItem).Data;

            var headPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedHead.PurchasableOutfitId);
            var bodyPurchasable = Content.Content.Get().AvatarPurchasables.Get(selectedBody.PurchasableOutfitId);
            
            var headOutfit = Content.Content.Get().AvatarOutfits.Get(headPurchasable.OutfitID);
            var bodyOutfit = Content.Content.Get().AvatarOutfits.Get(bodyPurchasable.OutfitID);

            SimBox.Avatar.Appearance = AppearanceType;
            SimBox.Avatar.Head = headOutfit;
            SimBox.Avatar.Body = bodyOutfit;
            SimBox.Avatar.Handgroup = bodyOutfit;
        }

        private void NameTextEdit_OnChange(UIElement element)
        {
            UpdateAcceptButtonState();
        }

        private void DescriptionTextEdit_OnChange(UIElement element)
        {
            UpdateAcceptButtonState();
        }

        private void UpdateAcceptButtonState()
        {
            var enabled = true;
            //in edit mode the name is server-provided and locked, so it must not gate the accept button
            if (!EditMode && !NAME_VALIDATION.IsMatch(NameTextEdit.CurrentText))
            {
                enabled = false;
            }

            if (!DESC_VALIDATION.IsMatch(DescriptionTextEdit.CurrentText))
            {
                enabled = false;
            }

            AcceptButton.Disabled = !enabled;
        }

        private void GenderButton_OnButtonClick(UIElement button)
        {
            if (button == MaleButton)
            {
                Gender = Gender.Male;
                MaleButton.Selected = true;
                FemaleButton.Selected = false;
            }
            else if (button == FemaleButton)
            {
                Gender = Gender.Female;
                MaleButton.Selected = false;
                FemaleButton.Selected = true;
            }
            RefreshCollections();
        }

        private void RefreshCollections()
        {
            var oldHeadIndex = m_HeadSkinBrowser.SelectedIndex;
            var oldBodyIndex = m_BodySkinBrowser.SelectedIndex;
            var oldHeadPage = m_HeadSkinBrowser.SelectedPage;
            var oldBodyPage = m_BodySkinBrowser.SelectedPage;

            if (Gender == Gender.Male)
            {
                m_HeadSkinBrowser.DataProvider = CollectionToDataProvider(MaleHeads);
                m_BodySkinBrowser.DataProvider = CollectionToDataProvider(MaleOutfits);
            }
            else
            {
                m_HeadSkinBrowser.DataProvider = CollectionToDataProvider(FemaleHeads);
                m_BodySkinBrowser.DataProvider = CollectionToDataProvider(FemaleOutfits);
            }
            // the choices for male heads and bodys are fewer than for females, so if changing to male, make sure the current page/index isn't out of bounds
            m_HeadSkinBrowser.SelectedIndex = Math.Min(oldHeadIndex, m_HeadSkinBrowser.DataProvider.Count - 1);
            m_BodySkinBrowser.SelectedIndex = Math.Min(oldBodyIndex, m_BodySkinBrowser.DataProvider.Count - 1);
            m_HeadSkinBrowser.SelectedPage = Math.Min(oldHeadPage, m_HeadSkinBrowser.DataProvider.Count / m_HeadSkinBrowser.ItemsPerPage);
            m_BodySkinBrowser.SelectedPage = Math.Min(oldBodyPage, m_BodySkinBrowser.DataProvider.Count / m_BodySkinBrowser.ItemsPerPage);

            RefreshSim();
        }

        private void SearchCollectionForInitID(ulong headID, ulong bodyID)
        {
            var purchs = Content.Content.Get().AvatarPurchasables;

            int index = m_BodySkinBrowser.DataProvider.FindIndex(x =>
                purchs.Get(
                ((CollectionItem)(((UIGridViewerItem)x).Data)).PurchasableOutfitId
                ).OutfitID == bodyID
            );

            if (index == -1) index = 0;
            m_BodySkinBrowser.SelectedIndex = index;

            index = m_HeadSkinBrowser.DataProvider.FindIndex(x =>
                purchs.Get(
                ((CollectionItem)(((UIGridViewerItem)x).Data)).PurchasableOutfitId
                ).OutfitID == headID
            );

            if (index == -1) index = 0;
            m_HeadSkinBrowser.SelectedIndex = index;

            RefreshSim();
        }
        
        private List<object> CollectionToDataProvider(Collection collection)
        {
            var dataProvider = new List<object>();
            foreach (var outfit in collection)
            {
                var purchasable = Content.Content.Get().AvatarPurchasables.Get(outfit.PurchasableOutfitId);
                Outfit TmpOutfit = Content.Content.Get().AvatarOutfits.Get(purchasable.OutfitID);
                Appearance TmpAppearance = Content.Content.Get().AvatarAppearances.Get(TmpOutfit.GetAppearance(AppearanceType));
                FSO.Common.Content.ContentID thumbID = TmpAppearance.ThumbnailID;

                dataProvider.Add(new UIGridViewerItem {
                    Data = outfit,
                    Thumb = new Promise<Texture2D>(x => Content.Content.Get().AvatarThumbnails.Get(thumbID).Get(GameFacade.GraphicsDevice))
                });
            }
            return dataProvider;
        }

        private void SkinButton_OnButtonClick(UIElement button)
        {
            SelectedAppearanceButton.Selected = false;
            SelectedAppearanceButton = (UIButton)button;
            SelectedAppearanceButton.Selected = true;

            var type = AppearanceType.Light;

            if (button == SkinMediumButton)
            {
                type = AppearanceType.Medium;
            }
            else if (button == SkinDarkButton)
            {
                type = AppearanceType.Dark;
            }

            this.AppearanceType = type;
            RefreshCollections();
        }
    }

    public enum Gender
    {
        Male,
        Female
    }
}
