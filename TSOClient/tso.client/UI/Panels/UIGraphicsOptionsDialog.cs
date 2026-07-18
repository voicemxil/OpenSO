using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Screens;
using FSO.Common;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.LotView;

namespace FSO.Client.UI.Panels
{
    public class UIGraphicsOptionsDialog : UIDialog
    {

        public UIButton AntiAliasCheckButton { get; set; }
        public UIButton ShadowsCheckButton { get; set; }
        public UIButton LightingCheckButton { get; set; }
        public UIButton UIEffectsCheckButton { get; set; }
        public UIButton EdgeScrollingCheckButton { get; set; }
        public UIButton Wall3DButton { get; set; }

        // High-Medium-Low detail buttons:

        public UIButton TerrainDetailLowButton { get; set; }
        public UIButton TerrainDetailMedButton { get; set; }
        public UIButton TerrainDetailHighButton { get; set; }

        public UIButton CharacterDetailLowButton { get; set; }
        public UIButton CharacterDetailMedButton { get; set; }
        public UIButton CharacterDetailHighButton { get; set; }

        public UIButton AALowButton { get; set; }
        public UIButton AAMedButton { get; set; }
        public UIButton AAHighButton { get; set; }

        public UIButton SwitchModeButton { get; set; }

        public UILabel UIEffectsLabel { get; set; }
        public UILabel AntiAliasLabel { get; set; }
        public UILabel CharacterDetailLabel { get; set; }
        public UILabel ShadowsLabel { get; set; }
        public UILabel LightingLabel { get; set; }
        public UILabel EdgeScrollingLabel { get; set; }

        public UILabel LowLabel { get; set; }
        public UILabel MediumLabel { get; set; }
        public UILabel HighLabel { get; set; }

        public UILabel TerrainDetailLabel { get; set; }
        public UILabel Wall3DLabel { get; set; }
        
        public UIButton DirectionButton { get; set; }
        public UILabel DirectionLabel { get; set; }

        public UIButton CompressionButton { get; set; }
        public UILabel CompressionLabel { get; set; }

        public UIButton AdvancedButton { get; set; }
        public UILabel AdvancedLabel { get; set; }

        public UIButton DPIButton { get; set; }
        public UISlider LightingSlider;
        private bool InternalChange;

        // --- Anti-aliasing / resolution controls (merged in from the former separate dialog) ---
        private UICombobox AACombo, MotionBlurCombo, BloomCombo, AOCombo, UpscalerCombo, TAADebugCombo;
        private object[] _aaObjs, _mblurObjs, _bloomObjs, _aoObjs, _upscalerObjs, _taaDbgObjs;
        private UILabel TAADebugRowLabel; // row hides when Cosmic TAA isn't the AA mode
        private UISlider RenderScaleSlider, SharpenSlider, MotionBlurSlider, BloomThresholdSlider, BloomIntensitySlider, AORadiusSlider, AOIntensitySlider;
        private UILabel RenderScaleLabel, SharpenLabel, MotionBlurLabel, BloomThresholdLabel, BloomIntensityLabel, AORadiusLabel, AOIntensityLabel;
        // Min 1/3 = the DLSS/FSR2 "Ultra Performance" ratio (1080p output from 360p render at 0.33x).
        private const float RENDER_SCALE_MIN = 1f / 3f, RENDER_SCALE_MAX = 2f;
        private const int AAX = 460; // x origin of the right-hand AA column
        private const int MBLUR_DEBUG = 99;       // dropdown sentinel: velocity-buffer debug view
        private const int MBLUR_DEBUG_DEPTH = 98; // dropdown sentinel: velocity-buffer depth view

        // Unified anti-aliasing modes: mutually-exclusive (MSAA, PostAA) combinations — MSAA and SMAA are
        // never enabled together. The dropdown value is the index into this table. MSAA tiers above the GPU's
        // FSOEnvironment.MaxMSAA are filtered out when the dropdown is built (so 8× is hidden on Apple Silicon).
        // PostAA: 0=none, 1=FXAA, 3=SMAA (high). (PostAA 2 "SMAA Low" was dropped — it hit the same shader.)
        // taa: 0=off, 1=Cosmic TAA (our temporal AA; enables the Upscaler row's Cosmic TAAU), 2=Cosmic TAA
        // Lite (same pipeline — jitter/velocity/history/TAAU all identical — but the lighter TAALite resolve
        // technique: fewer fetches, no lock/evidence machinery; for weaker GPUs).
        // Mutually exclusive with the other modes here (the engine composes spatial+temporal internally, but
        // the menu presents one AA choice; TAA forces MSAA off anyway — multisampled velocity corrupts it).
        // NOTE (user decision 2026-07-05): the Cosmic TAA rows deliberately keep PostAA=0 — no FXAA
        // pre-pass before the temporal resolve, even though the chain would compose them.
        private static readonly (string label, int msaa, int postAA, int taa)[] AAModes =
        {
            ("Off",                 0, 0, 0),
            ("FXAA",                0, 1, 0),
            ("SMAA",                0, 3, 0),
            ("Cosmic TAA",          0, 0, 1),
            ("Cosmic TAA Lite",     0, 0, 2),
            ("MSAA 2×",             2, 0, 0),
            ("MSAA 4×",             4, 0, 0),
            ("MSAA 8×",             8, 0, 0),
        };

        public UIGraphicsOptionsDialog() : base(UIDialogStyle.OK, true)
        {
            // Heal a stale on-disk PostAA==2 ("SMAA Low", removed from the AAModes table) to 3 so the saved
            // config agrees with what the engine has always run for it (World.ChangeAAMode: PostAA >= 2 =>
            // SMAA) — not just what CurrentAAIndex resolves it to in memory.
            if (GlobalSettings.Default.PostAA == 2)
            {
                GlobalSettings.Default.PostAA = 3;
                GlobalSettings.Default.Save();
            }

            SetSize(920, 540); // widened for the anti-aliasing / resolution column on the right (compacted)
            var script = this.RenderScript("graphicspanel.uis");

            UIEffectsLabel.Caption = GameFacade.Strings.GetString("f103", "2");
            UIEffectsLabel.Alignment = TextAlignment.Middle;
            // "Character Detail" actually drives ShadowQuality (shadow-map resolution) - relabel it
            CharacterDetailLabel.Caption = "Shadow Detail";
            CharacterDetailLabel.Tooltip = "Shadow map resolution (Low 512 / Med 1024 / High 2048).";
            TerrainDetailLabel.Caption = GameFacade.Strings.GetString("f103", "1");
            TerrainDetailLabel.Tooltip = "How many surrounding lots are drawn around the current lot.";
            ShadowsLabel.Caption = GameFacade.Strings.GetString("f103", "6");
            LightingLabel.Caption = GameFacade.Strings.GetString("f103", "20");

            ShadowsCheckButton.Tooltip = ShadowsLabel.Caption;
            LightingCheckButton.Tooltip = LightingLabel.Caption;
            UIEffectsCheckButton.Tooltip = UIEffectsLabel.Caption;

            CharacterDetailLowButton.OnButtonClick += new ButtonClickDelegate(ChangeShadowDetail);
            CharacterDetailMedButton.OnButtonClick += new ButtonClickDelegate(ChangeShadowDetail);
            CharacterDetailHighButton.OnButtonClick += new ButtonClickDelegate(ChangeShadowDetail);

            TerrainDetailLowButton.OnButtonClick += new ButtonClickDelegate(ChangeSurroundingDetail);
            TerrainDetailMedButton.OnButtonClick += new ButtonClickDelegate(ChangeSurroundingDetail);
            TerrainDetailHighButton.OnButtonClick += new ButtonClickDelegate(ChangeSurroundingDetail);

            TerrainDetailLowButton.Tooltip = GameFacade.Strings.GetString("f103", "8");
            TerrainDetailMedButton.Tooltip = GameFacade.Strings.GetString("f103", "9");
            TerrainDetailHighButton.Tooltip = GameFacade.Strings.GetString("f103", "10");

            var moveItems = new UIElement[] {
                TerrainDetailLowButton,TerrainDetailMedButton,TerrainDetailHighButton,
                CharacterDetailLowButton,CharacterDetailMedButton,CharacterDetailHighButton,
                CharacterDetailLabel, TerrainDetailLabel,
                HighLabel, MediumLabel, LowLabel,
            };
            foreach (var item in moveItems) item.Position += new Vector2(57, 27);

            var aa = CloneDetail(new Vector2(0, 23*2));
            AALowButton = aa.Item1;
            AAMedButton = aa.Item2;
            AAHighButton = aa.Item3;
            aa.Item4.Caption = AntiAliasLabel.Caption;

            // the old AA preset buttons are replaced by the granular column on the right
            AALowButton.Visible = false;
            AAMedButton.Visible = false;
            AAHighButton.Visible = false;
            aa.Item4.Visible = false;
            BuildAAColumn();

            var clone = CloneCheckbox();
            Wall3DButton = clone.Item1; Wall3DLabel = clone.Item2;
            Wall3DLabel.Caption = GameFacade.Strings.GetString("f103", "12");

            clone = CloneCheckbox();
            DirectionButton = clone.Item1; DirectionLabel = clone.Item2;
            //DirectionButton.Visible = FSOEnvironment.Enable3D;
            //DirectionLabel.Visible = FSOEnvironment.Enable3D;
            DirectionLabel.Caption = GameFacade.Strings.GetString("f103", "18");

            clone = CloneCheckbox();
            AdvancedButton = clone.Item1; AdvancedLabel = clone.Item2;
            AdvancedLabel.Caption = GameFacade.Strings.GetString("f103", "26");
            AdvancedLabel.Tooltip = GameFacade.Strings.GetString("f103", "27");
            AdvancedButton.Tooltip = AdvancedLabel.Tooltip;

            clone = CloneCheckbox();
            CompressionButton = clone.Item1; CompressionLabel = clone.Item2;
            CompressionLabel.Caption = GameFacade.Strings.GetString("f103", "23");
            CompressionLabel.Tooltip = GameFacade.Strings.GetString("f103", "24");
            CompressionButton.Tooltip = CompressionLabel.Tooltip;
            CompressionButton.Disabled = !FSOEnvironment.TexCompressSupport;

            AntiAliasCheckButton.Disabled = !FSOEnvironment.MSAASupport;

            AntiAliasCheckButton.Visible = false;
            AntiAliasLabel.Visible = false;
            var toggles = new Dictionary<UIButton, UILabel>()
            {
                { ShadowsCheckButton, ShadowsLabel },
                { LightingCheckButton, LightingLabel },
                { UIEffectsCheckButton, UIEffectsLabel },
                { EdgeScrollingCheckButton, EdgeScrollingLabel },
                { CompressionButton, CompressionLabel },
                { Wall3DButton, Wall3DLabel },
                { DirectionButton, DirectionLabel },
                { AdvancedButton, AdvancedLabel },
            };

            int i = 0;
            foreach (var item in toggles)
            {
                item.Key.Position = new Vector2(23, 65 + 22 * (i++));
                item.Value.Alignment = TextAlignment.Left;
                item.Value.Position = item.Key.Position + new Vector2(24, 0);
                item.Key.OnButtonClick += new ButtonClickDelegate(FlipSetting);
            }

            //switch lighting and uieffects label. replace lighting check with a slider

            LightingSlider = new UISlider();
            LightingSlider.Orientation = 0;
            LightingSlider.Texture = GetTexture(0x42500000001);
            LightingSlider.MinValue = 0f;
            LightingSlider.MaxValue = 3f;
            LightingSlider.AllowDecimals = false;
            LightingSlider.Position = new Vector2(184, 167+10);
            LightingSlider.SetSize(240f, 0f);
            Add(LightingSlider);
            //LightingLabel.X -= 24;

            DPIButton = new UIButton();
            DPIButton.Size = new Vector2(150, 35);
            DPIButton.Caption = GameFacade.Strings.GetString("f103", "13");
            DPIButton.Position = new Vector2(40, 250);
            DPIButton.OnButtonClick += DPIButton_OnButtonClick;
            Add(DPIButton);

            SwitchModeButton = new UIButton();
            SwitchModeButton.Size = new Vector2(175, 35);
            SwitchModeButton.Caption = GameFacade.Strings.GetString("f103", "41");
            SwitchModeButton.Tooltip = GameFacade.Strings.GetString("f103", "40");
            SwitchModeButton.Position = new Vector2(210, 250);
            SwitchModeButton.OnButtonClick += SwitchModeButton_OnButtonClick;
            Add(SwitchModeButton);

            var style = TextStyle.DefaultTitle.Clone();
            style.Size = 12;

            var toggle = new UILabel();
            toggle.CaptionStyle = style;
            toggle.Caption = GameFacade.Strings.GetString("f103", "19");
            toggle.Position = new Vector2(23, 35);
            Add(toggle);

            var detail = new UILabel();
            detail.CaptionStyle = style;
            detail.Caption = GameFacade.Strings.GetString("f103", "22");
            detail.Position = new Vector2(180, 35);
            Add(detail);

            var adv = new UILabel();
            adv.CaptionStyle = style;
            adv.Caption = GameFacade.Strings.GetString("f103", "16");
            adv.Position = new Vector2(180, 117+10);
            Add(adv);

            var types = new UILabel();
            types.Caption = GameFacade.Strings.GetString("f103", "17");
            types.Position = new Vector2(180, 145+10);
            types.Size = new Vector2(240, 0);
            types.Alignment = TextAlignment.Center;
            Add(types);

            Caption = GameFacade.Strings.GetString("f103", "21");

            SettingsChanged();

            LightingSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                var settings = GlobalSettings.Default;
                settings.LightingMode = (int)LightingSlider.Value;
                GlobalSettings.Default.Save();
                SettingsChanged();
            };

            OKButton.OnButtonClick += (btn) =>
            {
                UIScreen.RemoveDialog(this);
            };

            GraphicsModeControl.ModeChanged += UpdateModeText;
        }

        private void SwitchModeButton_OnButtonClick(UIElement button)
        {
            if (GraphicsModeControl.Mode == LotView.Model.GlobalGraphicsMode.Hybrid2D)
            {
                GraphicsModeControl.ChangeMode(LotView.Model.GlobalGraphicsMode.Full3D);
            }
            else if (GraphicsModeControl.Mode == LotView.Model.GlobalGraphicsMode.Full3D)
            {
                GraphicsModeControl.ChangeMode(LotView.Model.GlobalGraphicsMode.Hybrid2D);
            }
        }

        private void DPIButton_OnButtonClick(UIElement button)
        {
            UIScreen.GlobalShowDialog(new UIDPIScaleDialog(), true);
        }

        public Tuple<UIButton, UIButton, UIButton, UILabel> CloneDetail(Vector2 posOffset)
        {
            var check = new UIButton(TerrainDetailLowButton.Texture) { Position = TerrainDetailLowButton.Position + posOffset };
            Add(check);
            var check2 = new UIButton(TerrainDetailLowButton.Texture) { Position = TerrainDetailMedButton.Position + posOffset };
            Add(check2);
            var check3 = new UIButton(TerrainDetailLowButton.Texture) { Position = TerrainDetailHighButton.Position + posOffset };
            Add(check3);
            var label = new UILabel();
            label.CaptionStyle = TerrainDetailLabel.CaptionStyle;
            label.Position = TerrainDetailLabel.Position + posOffset;
            label.Size = TerrainDetailLabel.Size;
            label.Alignment = TerrainDetailLabel.Alignment;
            Add(label);
            return new Tuple<UIButton, UIButton, UIButton, UILabel>(check, check2, check3, label);
        }

        public Tuple<UIButton, UILabel> CloneCheckbox()
        {
            var check = new UIButton(AntiAliasCheckButton.Texture);
            Add(check);
            var label = new UILabel();
            label.CaptionStyle = UIEffectsLabel.CaptionStyle;
            label.Position = check.Position + new Microsoft.Xna.Framework.Vector2(34, 0);
            Add(label);
            return new Tuple<UIButton, UILabel>(check, label);
        }

        private void ShowRestartWarning()
        {
            UIAlert alert = null;
            alert = UIScreen.GlobalShowAlert(new UIAlertOptions()
            {
                Message = GameFacade.Strings.GetString("f103", "25"),
                Buttons = UIAlertButton.Ok(x => {
                    UIScreen.RemoveDialog(alert);
                })
            }, true);
        }

        private void FlipSetting(UIElement button)
        {
            var settings = GlobalSettings.Default;
            if (button == AntiAliasCheckButton) settings.AntiAlias = settings.AntiAlias ^ 1;
            else if (button == ShadowsCheckButton) settings.EnableTransitions = !(settings.EnableTransitions);
            else if (button == LightingCheckButton) settings.Weather = !(settings.Weather);
            else if (button == UIEffectsCheckButton) settings.CityShadows = !(settings.CityShadows);
            else if (button == EdgeScrollingCheckButton) settings.EdgeScroll = !(settings.EdgeScroll);
            else if (button == DirectionButton) settings.DirectionalLight3D = !(settings.DirectionalLight3D);
            else if (button == AdvancedButton) settings.ComplexShaders = !(settings.ComplexShaders);
            else if (button == CompressionButton)
            {
                settings.TexCompression = (((settings.TexCompression) & 1) ^ 1) | 2;
                ShowRestartWarning();
            }
            else if (button == Wall3DButton)
            {
                settings.CitySkybox = !settings.CitySkybox;
            }
            GlobalSettings.Default.Save();
            SettingsChanged();
        }

        private void ChangeShadowDetail(UIElement button)
        {
            var settings = GlobalSettings.Default;
            if (button == CharacterDetailLowButton) settings.ShadowQuality = 512;
            else if (button == CharacterDetailMedButton) settings.ShadowQuality = 1024;
            else if (button == CharacterDetailHighButton) settings.ShadowQuality = 2048;
            GlobalSettings.Default.Save();
            SettingsChanged();
        }

        private void ChangeSurroundingDetail(UIElement button)
        {
            var settings = GlobalSettings.Default;
            if (button == TerrainDetailLowButton) settings.SurroundingLotMode = 0;
            else if (button == TerrainDetailMedButton) settings.SurroundingLotMode = 1;
            else if (button == TerrainDetailHighButton) settings.SurroundingLotMode = 2;
            GlobalSettings.Default.Save();
            SettingsChanged();
        }

        private void ChangeAA(UIElement button)
        {
            var settings = GlobalSettings.Default;
            // Quality presets over the decoupled AA pipeline. The legacy AntiAlias summary is kept
            // in sync for UI/icon render targets that still read it.
            if (button == AALowButton) // Off
            {
                settings.MSAALevel = 0; settings.SuperSampling = 1;
                settings.PostAA = 0; settings.Sharpen = 0; settings.AntiAlias = 0;
            }
            else if (button == AAMedButton) // MSAA 4x
            {
                settings.MSAALevel = 4; settings.SuperSampling = 1;
                settings.PostAA = 0; settings.Sharpen = 0; settings.AntiAlias = 1;
            }
            else if (button == AAHighButton) // MSAA 4x + Supersample 2x
            {
                settings.MSAALevel = 4; settings.SuperSampling = 2;
                settings.PostAA = 0; settings.Sharpen = 0; settings.AntiAlias = 2;
            }
            GlobalSettings.Default.Save();
            SettingsChanged();
        }

        private void UpdateModeText(LotView.Model.GlobalGraphicsMode mode)
        {
            switch (mode)
            {
                case LotView.Model.GlobalGraphicsMode.Full2D:
                    SwitchModeButton.Visible = false;
                    break;
                case LotView.Model.GlobalGraphicsMode.Full3D:
                    SwitchModeButton.Visible = true;
                    SwitchModeButton.Caption = GameFacade.Strings.GetString("f103", "42");
                    break;
                case LotView.Model.GlobalGraphicsMode.Hybrid2D:
                    SwitchModeButton.Visible = true;
                    SwitchModeButton.Caption = GameFacade.Strings.GetString("f103", "41");
                    break;
            }
            Invalidate();
        }

        private void SettingsChanged()
        {
            var settings = GlobalSettings.Default;
            AntiAliasCheckButton.Selected = settings.AntiAlias > 0; //antialias for render targets
            ShadowsCheckButton.Selected = settings.EnableTransitions;
            LightingCheckButton.Selected = settings.Weather;
            UIEffectsCheckButton.Selected = settings.CityShadows; //instead of being able to disable UI transparency, you can toggle City Shadows.
            EdgeScrollingCheckButton.Selected = settings.EdgeScroll;
            DirectionButton.Selected = settings.DirectionalLight3D;
            AdvancedButton.Selected = settings.ComplexShaders;

            // Character detail changed for city shadow detail.
            CharacterDetailLowButton.Selected = (settings.ShadowQuality <= 512);
            CharacterDetailMedButton.Selected = (settings.ShadowQuality > 512 && settings.ShadowQuality <= 1024);
            CharacterDetailHighButton.Selected = (settings.ShadowQuality > 1024);

            //not used right now! We need to determine if this should be ingame or not... It affects the density of grass blades on the simulation terrain.
            TerrainDetailLowButton.Selected = (settings.SurroundingLotMode == 0);
            TerrainDetailMedButton.Selected = (settings.SurroundingLotMode == 1);
            TerrainDetailHighButton.Selected = (settings.SurroundingLotMode == 2);

            AALowButton.Selected = (settings.MSAALevel == 0 && settings.SuperSampling <= 1);
            AAMedButton.Selected = (settings.MSAALevel > 0 && settings.SuperSampling <= 1);
            AAHighButton.Selected = (settings.SuperSampling > 1);

            InternalChange = true;
            LightingSlider.Value = settings.LightingMode;
            InternalChange = false;

            Wall3DButton.Selected = settings.CitySkybox;
            FSOEnvironment.TexCompress = (settings.TexCompression & 1) > 0;
            CompressionButton.Selected = FSOEnvironment.TexCompress;

            UpdateModeText(GraphicsModeControl.Mode);

            var oldSurrounding = LotView.WorldConfig.Current.SurroundingLots;
            LotView.WorldConfig.Current = new LotView.WorldConfig()
            {
                LightingMode = settings.LightingMode,
                SmoothZoom = settings.SmoothZoom,
                SurroundingLots = settings.SurroundingLotMode,
                AA = settings.AntiAlias,
                MSAA = settings.MSAALevel,
                SuperSampling = settings.SuperSampling,
                RenderScale = settings.RenderScale,
                PostAA = settings.PostAA,
                TAA = settings.TAA,
                TAALite = settings.TAALite,
                MotionBlur = settings.MotionBlur,
                MotionBlurAmount = settings.MotionBlurAmount,
                Bloom = settings.Bloom,
                BloomThreshold = settings.BloomThreshold,
                BloomIntensity = settings.BloomIntensity,
                AO = settings.AO,
                AORadius = settings.AORadius,
                AOIntensity = settings.AOIntensity,
                VelocityDebug = settings.VelocityDebug,
                VelocityDebugDepth = settings.VelocityDebugDepth,
                TAADebug = settings.TAADebug,
                Upscaler = settings.Upscaler,
                Sharpen = settings.Sharpen,
                SharpenAmount = settings.SharpenAmount,
                Weather = settings.Weather,
                Directional = settings.DirectionalLight3D,
                Complex = settings.ComplexShaders,
                EnableTransitions = settings.EnableTransitions
            };

            var vm = ((IGameScreen)GameFacade.Screens.CurrentUIScreen)?.vm;
            if (vm != null)
            {
                vm.Context.World.ChangedWorldConfig(GameFacade.GraphicsDevice);
                if (oldSurrounding != settings.SurroundingLotMode)
                {
                    SimAntics.Utils.VMLotTerrainRestoreTools.RestoreSurroundings(vm, vm.HollowAdj, true);
                }
            }
        }

        // ---- Anti-aliasing / resolution column -----------------------------------------------------------

        private void BuildAAColumn()
        {
            var msaa = FSOEnvironment.MSAASupport;

            // Rows are added BOTTOM-to-TOP so each combo's drop-down renders over the rows beneath it.
            // Velocity debug lives in the Motion-blur dropdown.

            // --- Effects ---
            AddAOIntensityRow("AO intensity", 486);
            AddAORadiusRow("AO radius", 454);
            AOCombo = AddRow("Ambient occlusion (3D)", 422,
                new[] { "Off", "On" }, new[] { 0, 1 }, out _aoObjs,
                v => { GlobalSettings.Default.AO = v == 1; ApplyAndRefresh(true); });
            AddMotionBlurRow("Motion blur strength", 390);
            MotionBlurCombo = AddRow("Motion blur (3D)", 358,
                new[] { "Off", "On", "Debug (velocity)", "Debug (depth)" }, new[] { 0, 2, MBLUR_DEBUG, MBLUR_DEBUG_DEPTH }, out _mblurObjs,  // 2 = per-pixel 3D
                v =>
                {
                    var s = GlobalSettings.Default;
                    s.VelocityDebug = (v == MBLUR_DEBUG || v == MBLUR_DEBUG_DEPTH);
                    s.VelocityDebugDepth = (v == MBLUR_DEBUG_DEPTH);
                    s.MotionBlur = (v == 2) ? 2 : 0;
                    ApplyAndRefresh(true);
                });
            AddBloomIntensityRow("Bloom intensity", 324);
            AddBloomThresholdRow("Bloom threshold", 292);
            BloomCombo = AddRow("Bloom", 258,
                new[] { "Off", "On" }, new[] { 0, 1 }, out _bloomObjs,
                v => { GlobalSettings.Default.Bloom = v == 1; ApplyAndRefresh(true); });
            AddGroupHeader("Effects", 232);

            // --- Resolution ---
            // Upscaler for render scale < 1: FSR 1 (spatial) vs Cosmic TAAU. TAAU needs TAA's
            // history/velocity, so its entry is only selectable while TAA is on.
            UpscalerCombo = AddRow("Upscaler", 200,
                new[] { "FSR 1", "Sharp Bilinear", "Cosmic TAAU" }, new[] { 0, 2, 1 }, out _upscalerObjs,
                v =>
                {
                    // TAAU is unselectable while TAA is off; coerce if it slips through (e.g. keys)
                    if (v == 1 && !GlobalSettings.Default.TAA) v = 0;
                    GlobalSettings.Default.Upscaler = v;
                    ApplyAndRefresh();
                });
            AddSharpenRow("Sharpening", 168); // FSR RCAS post-pass; applies at any render scale
            SharpenLabel.Tooltip = "FSR RCAS sharpening — applies at any render scale.";
            AddRenderScaleRow("Render scale", 136);
            AddGroupHeader("Resolution", 110);

            // --- Anti-aliasing (added last so its drop-down overlays the rows below) ---
            // Cosmic TAA debug row: visible only while Cosmic TAA is the AA mode.
            TAADebugCombo = AddRow("Cosmic TAA debug", 78,
                new[] { "Off", "On" }, new[] { 0, 1 }, out _taaDbgObjs,
                v => { GlobalSettings.Default.TAADebug = v == 1; ApplyAndRefresh(); });
            TAADebugRowLabel = _LastRowLabel;

            // Unified AA selector; MSAA tiers capped to the GPU max. Value = index into AAModes.
            var aaNames = new System.Collections.Generic.List<string>();
            var aaValues = new System.Collections.Generic.List<int>();
            for (int i = 0; i < AAModes.Length; i++)
                if (AAModes[i].msaa == 0 || (msaa && AAModes[i].msaa <= FSOEnvironment.MaxMSAA))
                { aaNames.Add(AAModes[i].label); aaValues.Add(i); }
            AACombo = AddRow("Anti-aliasing", 46, aaNames.ToArray(), aaValues.ToArray(), out _aaObjs, OnAAMode);
            AddGroupHeader("Anti-aliasing", 20);

            RefreshSelections();
        }

        private UILabel _LastRowLabel; // label created by the most recent AddRow (for rows that hide/show)

        private UICombobox AddRow(string label, int y, string[] names, int[] values, out object[] valueObjs, Action<int> onPick)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);
            _LastRowLabel = lbl;

            var objs = new object[values.Length];
            var items = new List<UIComboboxItem>();
            for (int i = 0; i < values.Length; i++)
            {
                objs[i] = values[i];
                items.Add(new UIComboboxItem() { Name = names[i], Value = objs[i] });
            }
            valueObjs = objs;

            var combo = new UICombobox() { Width = 230, Position = new Vector2(AAX + 175, y) };
            combo.Items = items;
            combo.OnSelect += (o) => { if (!InternalChange && o != null) onPick((int)o); };
            DynamicOverlay.Add(combo);
            return combo;
        }

        // Render scale slider (<1 upscales, >1 supersamples).
        private void AddRenderScaleRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);

            RenderScaleSlider = new UISlider()
            {
                Orientation = 0,
                Texture = GetTexture(0x42500000001),
                MinValue = RENDER_SCALE_MIN,
                MaxValue = RENDER_SCALE_MAX,
                AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            RenderScaleSlider.SetSize(150f, 0f);
            DynamicOverlay.Add(RenderScaleSlider);

            RenderScaleLabel = new UILabel() { Caption = "1.0×", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(RenderScaleLabel);

            RenderScaleSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                var s = GlobalSettings.Default;
                float v = (float)(System.Math.Round(RenderScaleSlider.Value * 20.0) / 20.0); // soft 0.05 grid
                // the 0.05 grid can't express the 1/3 floor - snap the bottom of the slider to it
                if (RenderScaleSlider.Value <= RENDER_SCALE_MIN + 0.01f) v = RENDER_SCALE_MIN;
                s.RenderScale = v;
                s.SuperSampling = (s.RenderScale > 1f) ? 2 : 1;
                ApplyAndRefresh(true);
            };
        }

        private void SetRenderScaleSlider(float scale)
        {
            if (RenderScaleSlider == null) return;
            RenderScaleSlider.Value = scale;
            if (RenderScaleLabel == null) return;
            string res = "";
            var gd = GameFacade.GraphicsDevice;
            if (gd != null)
            {
                int w = System.Math.Max(1, (int)System.Math.Round(gd.Viewport.Width * scale));
                int h = System.Math.Max(1, (int)System.Math.Round(gd.Viewport.Height * scale));
                res = "  " + w + "×" + h;
            }
            RenderScaleLabel.Caption = scale.ToString("0.0#") + "×" + res;
        }

        private void AddSharpenRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);

            SharpenSlider = new UISlider()
            {
                Orientation = 0,
                Texture = GetTexture(0x42500000001),
                MinValue = 0f,
                MaxValue = 1f,
                AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            SharpenSlider.SetSize(150f, 0f);
            DynamicOverlay.Add(SharpenSlider);

            SharpenLabel = new UILabel() { Caption = "Off", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(SharpenLabel);

            SharpenSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                var s = GlobalSettings.Default;
                s.SharpenAmount = (float)(System.Math.Round(SharpenSlider.Value * 20.0) / 20.0);
                s.Sharpen = (s.SharpenAmount > 0f) ? 1 : 0;
                ApplyAndRefresh(true);
            };
        }

        private void SetSharpenSlider(float amt)
        {
            if (SharpenSlider == null) return;
            SharpenSlider.Value = amt;
            if (SharpenLabel != null) SharpenLabel.Caption = (amt > 0f) ? amt.ToString("0.0#") : "Off";
        }

        private void AddMotionBlurRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);

            MotionBlurSlider = new UISlider()
            {
                Orientation = 0,
                Texture = GetTexture(0x42500000001),
                MinValue = 0f,
                // capped at 0.5 = the film-standard 180 degree shutter; higher is far too strong
                MaxValue = 0.5f,
                AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            MotionBlurSlider.SetSize(150f, 0f);
            DynamicOverlay.Add(MotionBlurSlider);

            MotionBlurLabel = new UILabel() { Caption = "0.5", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(MotionBlurLabel);

            MotionBlurSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                var s = GlobalSettings.Default;
                s.MotionBlurAmount = (float)(System.Math.Round(MotionBlurSlider.Value * 20.0) / 20.0); // 0.05 grid
                ApplyAndRefresh(true);
            };
        }

        private void SetMotionBlurSlider(float amt)
        {
            if (MotionBlurSlider == null) return;
            MotionBlurSlider.Value = System.Math.Min(amt, 0.5f); // legacy configs may hold up to 1.0
            if (MotionBlurLabel != null) MotionBlurLabel.Caption = System.Math.Min(amt, 0.5f).ToString("0.0#");
        }

        private void AddBloomThresholdRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);
            BloomThresholdSlider = new UISlider()
            {
                Orientation = 0, Texture = GetTexture(0x42500000001),
                MinValue = 0f, MaxValue = 2f, AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            BloomThresholdSlider.SetSize(150f, 0f);
            DynamicOverlay.Add(BloomThresholdSlider);
            BloomThresholdLabel = new UILabel() { Caption = "0.8", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(BloomThresholdLabel);
            BloomThresholdSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                GlobalSettings.Default.BloomThreshold = (float)(System.Math.Round(BloomThresholdSlider.Value * 20.0) / 20.0);
                ApplyAndRefresh(true);
            };
        }

        private void AddBloomIntensityRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);
            BloomIntensitySlider = new UISlider()
            {
                Orientation = 0, Texture = GetTexture(0x42500000001),
                MinValue = 0f, MaxValue = 1f, AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            BloomIntensitySlider.SetSize(150f, 0f);
            DynamicOverlay.Add(BloomIntensitySlider);
            BloomIntensityLabel = new UILabel() { Caption = "0.6", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(BloomIntensityLabel);
            BloomIntensitySlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                GlobalSettings.Default.BloomIntensity = (float)(System.Math.Round(BloomIntensitySlider.Value * 20.0) / 20.0);
                ApplyAndRefresh(true);
            };
        }

        private void SetBloomSliders(float threshold, float intensity)
        {
            if (BloomThresholdSlider != null) { BloomThresholdSlider.Value = threshold; if (BloomThresholdLabel != null) BloomThresholdLabel.Caption = threshold.ToString("0.0#"); }
            if (BloomIntensitySlider != null) { BloomIntensitySlider.Value = intensity; if (BloomIntensityLabel != null) BloomIntensityLabel.Caption = intensity.ToString("0.0#"); }
        }

        private void AddAORadiusRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);
            AORadiusSlider = new UISlider()
            {
                Orientation = 0, Texture = GetTexture(0x42500000001),
                MinValue = 0.1f, MaxValue = 2f, AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            AORadiusSlider.SetSize(150f, 0f);
            DynamicOverlay.Add(AORadiusSlider);
            AORadiusLabel = new UILabel() { Caption = "0.5", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(AORadiusLabel);
            AORadiusSlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                GlobalSettings.Default.AORadius = (float)(System.Math.Round(AORadiusSlider.Value * 20.0) / 20.0);
                ApplyAndRefresh(true);
            };
        }

        private void AddAOIntensityRow(string label, int y)
        {
            var lbl = new UILabel() { Caption = label, Position = new Vector2(AAX + 25, y + 2) };
            DynamicOverlay.Add(lbl);
            AOIntensitySlider = new UISlider()
            {
                Orientation = 0, Texture = GetTexture(0x42500000001),
                MinValue = 0f, MaxValue = 2f, AllowDecimals = true,
                Position = new Vector2(AAX + 175, y + 8)
            };
            AOIntensitySlider.SetSize(150f, 0f);
            DynamicOverlay.Add(AOIntensitySlider);
            AOIntensityLabel = new UILabel() { Caption = "1.0", Position = new Vector2(AAX + 335, y + 2) };
            DynamicOverlay.Add(AOIntensityLabel);
            AOIntensitySlider.OnChange += (elem) =>
            {
                if (InternalChange) return;
                GlobalSettings.Default.AOIntensity = (float)(System.Math.Round(AOIntensitySlider.Value * 20.0) / 20.0);
                ApplyAndRefresh(true);
            };
        }

        private void SetAOSliders(float radius, float intensity)
        {
            if (AORadiusSlider != null) { AORadiusSlider.Value = radius; if (AORadiusLabel != null) AORadiusLabel.Caption = radius.ToString("0.0#"); }
            if (AOIntensitySlider != null) { AOIntensitySlider.Value = intensity; if (AOIntensityLabel != null) AOIntensityLabel.Caption = intensity.ToString("0.0#"); }
        }

        private void SelectValue(UICombobox combo, object[] objs, int value)
        {
            if (combo == null || objs == null) return;
            for (int i = 0; i < objs.Length; i++)
                if ((int)objs[i] == value) { combo.SelectedItem = objs[i]; return; }
            if (objs.Length > 0) combo.SelectedItem = objs[0];
        }

        private void OnAAMode(int index)
        {
            if (index < 0 || index >= AAModes.Length) return;
            var s = GlobalSettings.Default;
            bool taaWasOff = !s.TAA;
            s.MSAALevel = AAModes[index].msaa;
            s.PostAA = AAModes[index].postAA;
            s.TAA = AAModes[index].taa >= 1;
            s.TAALite = AAModes[index].taa == 2; // lighter resolve technique; false for all other AA modes
            if (!s.TAA) s.TAADebug = false; // debug rides on TAA; its own row (visible while TAA on) sets it
            // Enabling Cosmic TAA auto-selects Cosmic TAAU as the upscaler (the Upscaler row's Cosmic entry
            // un-grays); disabling leaves the stored preference (engine falls back to FSR 1 without TAA).
            if (taaWasOff && s.TAA) s.Upscaler = 1;
            ApplyAndRefresh();
        }

        // World.ChangeAAMode picks the SMAA pass with "cfg.PostAA >= 2" — so a legacy stored PostAA==2
        // ("SMAA Low", dropped from the AAModes table above) still runs SMAA at the engine, but has no exact
        // match in the table (which only has 0/1/3). Collapse it to the SMAA entry (3) so the dropdown shows
        // what's actually running instead of falling through to the FXAA fallback below.
        private static int NormalizedPostAA(int postAA) => (postAA >= 2) ? 3 : postAA;

        // Map the current (MSAALevel, PostAA, TAA) settings back to an AAModes index for the dropdown. TAA
        // wins (it's mutually exclusive in the menu); then exact match; otherwise prefer the hardware MSAA
        // tier if one is set, else the post-AA method, else Off.
        private int CurrentAAIndex()
        {
            var s = GlobalSettings.Default;
            if (s.TAA)
                for (int i = 0; i < AAModes.Length; i++)
                    if (AAModes[i].taa == (s.TAALite ? 2 : 1)) return i;
            int postAA = NormalizedPostAA(s.PostAA);
            for (int i = 0; i < AAModes.Length; i++)
                if (AAModes[i].msaa == s.MSAALevel && AAModes[i].postAA == postAA && AAModes[i].taa == 0) return i;
            if (s.MSAALevel > 0)
                for (int i = 0; i < AAModes.Length; i++)
                    if (AAModes[i].msaa == s.MSAALevel && AAModes[i].postAA == 0 && AAModes[i].taa == 0) return i;
            if (postAA > 0)
                for (int i = 0; i < AAModes.Length; i++)
                    if (AAModes[i].msaa == 0 && AAModes[i].postAA != 0) return i;
            return 0; // Off
        }

        private void AddGroupHeader(string caption, int y)
        {
            var style = TextStyle.DefaultTitle.Clone();
            style.Size = 11;
            DynamicOverlay.Add(new UILabel() { CaptionStyle = style, Caption = caption, Position = new Vector2(AAX + 25, y) });
        }

        private void ApplyAndRefresh(bool light = false)
        {
            var s = GlobalSettings.Default;
            if (!FSOEnvironment.MSAASupport) s.MSAALevel = 0;
            s.AntiAlias = (s.RenderScale > 1f) ? 2 : ((s.MSAALevel > 0 || s.PostAA > 0) ? 1 : 0);
            s.Save();

            LotView.WorldConfig.Current = new LotView.WorldConfig()
            {
                LightingMode = s.LightingMode,
                SmoothZoom = s.SmoothZoom,
                SurroundingLots = s.SurroundingLotMode,
                AA = s.AntiAlias,
                MSAA = s.MSAALevel,
                SuperSampling = s.SuperSampling,
                RenderScale = s.RenderScale,
                PostAA = s.PostAA,
                TAA = s.TAA,
                TAALite = s.TAALite,
                MotionBlur = s.MotionBlur,
                MotionBlurAmount = s.MotionBlurAmount,
                Bloom = s.Bloom,
                BloomThreshold = s.BloomThreshold,
                BloomIntensity = s.BloomIntensity,
                AO = s.AO,
                AORadius = s.AORadius,
                AOIntensity = s.AOIntensity,
                VelocityDebug = s.VelocityDebug,
                VelocityDebugDepth = s.VelocityDebugDepth,
                TAADebug = s.TAADebug,
                Upscaler = s.Upscaler,
                Sharpen = s.Sharpen,
                SharpenAmount = s.SharpenAmount,
                Weather = s.Weather,
                Directional = s.DirectionalLight3D,
                Complex = s.ComplexShaders,
                EnableTransitions = s.EnableTransitions
            };

            var vm = (GameFacade.Screens.CurrentUIScreen as IGameScreen)?.vm;
            if (vm != null)
            {
                if (light) vm.Context.World.ChangeAAMode(GameFacade.GraphicsDevice);
                else vm.Context.World.ChangedWorldConfig(GameFacade.GraphicsDevice);
            }

            RefreshSelections();
        }

        private void RefreshSelections()
        {
            var s = GlobalSettings.Default;
            InternalChange = true;
            SelectValue(AACombo, _aaObjs, CurrentAAIndex());
            SetRenderScaleSlider(s.RenderScale);
            SelectValue(MotionBlurCombo, _mblurObjs, s.VelocityDebug ? (s.VelocityDebugDepth ? MBLUR_DEBUG_DEPTH : MBLUR_DEBUG) : ((s.MotionBlur == 2) ? 2 : 0));
            // the Cosmic TAAU entry is only selectable while TAA is on (grayed out otherwise)
            UpscalerCombo.Items = new List<UIComboboxItem>
            {
                new UIComboboxItem { Name = "FSR 1", Value = _upscalerObjs[0] },
                new UIComboboxItem { Name = "Sharp Bilinear", Value = _upscalerObjs[1] },
                new UIComboboxItem { Name = "Cosmic TAAU", Value = _upscalerObjs[2], Disabled = !s.TAA },
            };
            // SelectValue takes the stored VALUE (not a list index); TAAU displays as FSR 1 while TAA
            // is off (the render path falls back to EASU in that state too)
            SelectValue(UpscalerCombo, _upscalerObjs, (s.Upscaler == 1 && !s.TAA) ? 0 : s.Upscaler);
            SelectValue(TAADebugCombo, _taaDbgObjs, s.TAADebug ? 1 : 0);
            TAADebugCombo.Visible = s.TAA;
            if (TAADebugRowLabel != null) TAADebugRowLabel.Visible = s.TAA;
            SetMotionBlurSlider(s.MotionBlurAmount);
            SelectValue(BloomCombo, _bloomObjs, s.Bloom ? 1 : 0);
            SetBloomSliders(s.BloomThreshold, s.BloomIntensity);
            SelectValue(AOCombo, _aoObjs, s.AO ? 1 : 0);
            SetAOSliders(s.AORadius, s.AOIntensity);
            SetSharpenSlider(s.SharpenAmount);
            InternalChange = false;
        }

        public override void Removed()
        {
            base.Removed();
            GraphicsModeControl.ModeChanged -= UpdateModeText;
        }
    }

    public class UIDPIScaleDialog : UIDialog
    {
        public UILabel DPILabel;
        public UISlider DPISlider;
        public UIButton AutoButton;
        private bool InternalChange;

        public UIDPIScaleDialog() : base(UIDialogStyle.OK, true) {

            DPILabel = new UILabel();
            DPILabel.Position = new Vector2(25, 50);
            DPILabel.Size = new Vector2(350f, 0f);
            DPILabel.Alignment = TextAlignment.Center;
            DynamicOverlay.Add(DPILabel);

            DPISlider = new UISlider();
            DPISlider.Orientation = 0;
            DPISlider.Texture = GetTexture(0x42500000001);
            DPISlider.MinValue = 4f;
            DPISlider.MaxValue = 12f;
            DPISlider.AllowDecimals = false;
            DPISlider.Position = new Vector2(25, 80);
            DPISlider.SetSize(350f, 0f);

            DPISlider.Value = FSOEnvironment.DPIScaleFactor * 4;
            DynamicOverlay.Add(DPISlider);

            DPISlider.OnChange += DPISlider_OnChange;

            AutoButton = new UIButton();
            AutoButton.Size = new Vector2(100, 35);
            AutoButton.Caption = "Auto";
            AutoButton.Tooltip = "Match your operating system's display scale";
            AutoButton.Position = new Vector2(25, 105);
            AutoButton.OnButtonClick += AutoButton_OnButtonClick;
            DynamicOverlay.Add(AutoButton);

            SetSize(400, 150);

            OKButton.OnButtonClick += (btn) =>
            {
                UIScreen.RemoveDialog(this);
            };
        }

        private void AutoButton_OnButtonClick(UIElement button)
        {
            GlobalSettings.Default.AutoDPI = 1;
            InternalChange = true;
            DPISlider.Value = Utils.DPIScaleDetect.GetSnappedScale() * 4;
            InternalChange = false;
            GlobalSettings.Default.Save(); //persist the auto flag even when the scale didn't change
        }

        private void DPISlider_OnChange(UIElement element)
        {
            var manual = !InternalChange;
            GameThread.NextUpdate((cb) =>
            {
                if (manual) GlobalSettings.Default.AutoDPI = 0;
                FSOEnvironment.DPIScaleFactor = DPISlider.Value / 4f;
                GlobalSettings.Default.DPIScaleFactor = FSOEnvironment.DPIScaleFactor;

                var width = Math.Max(1, GameFacade.Game.Window.ClientBounds.Width);
                var height = Math.Max(1, GameFacade.Game.Window.ClientBounds.Height);

                UIScreen.Current.ScaleX = UIScreen.Current.ScaleY = FSOEnvironment.DPIScaleFactor;

                GlobalSettings.Default.GraphicsWidth = (int)(width / FSOEnvironment.DPIScaleFactor);
                GlobalSettings.Default.GraphicsHeight = (int)(height / FSOEnvironment.DPIScaleFactor);

                UIScreen.Current.GameResized();
                GlobalSettings.Default.Save();
            });
        }

        public override void Update(UpdateState state)
        {
            ScaleX = 1f / FSOEnvironment.DPIScaleFactor;
            ScaleY = 1f / FSOEnvironment.DPIScaleFactor;
            DPILabel.Caption = (FSOEnvironment.DPIScaleFactor * 100).ToString() + "%"
                + ((GlobalSettings.Default.AutoDPI == 1) ? " (Auto)" : "");
            base.Update(state);
            Position = Vector2.Zero;
        }
    }

}
