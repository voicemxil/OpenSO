using FSO.Common;
using System;
using System.Collections.Generic;
using System.IO;

namespace FSO.Client
{
    public class GlobalSettings : IniConfig
    {
        public override string HeadingComment => "OpenSO Settings File. Properties are self explanatory.";
        private static GlobalSettings defaultInstance;

        public static GlobalSettings Default
        {
            get
            {
                if (defaultInstance == null)
                {
                    defaultInstance = new GlobalSettings(Path.Combine(FSOEnvironment.UserDir, "config.ini"));
                    if (defaultInstance.DPIScaleFactor > 4 || defaultInstance.DPIScaleFactor == 0)
                        defaultInstance.DPIScaleFactor = 1; //sanity check
                    if (defaultInstance.ChatWindowsOpacity == 0 || defaultInstance.ChatWindowsOpacity > 1)
                        defaultInstance.ChatWindowsOpacity = 1; //sanity check
                    if (defaultInstance.GameEntryUrl == "http://api.freeso.org")
                    {
                        defaultInstance.GameEntryUrl = "https://api.freeso.org";
                        defaultInstance.CitySelectorUrl = "https://api.freeso.org";
                    }

                    if (defaultInstance.ArchiveClientGUID == "")
                    {
                        defaultInstance.ArchiveClientGUID = GenerateGUID();
                    }

                    // Migrate the legacy mutually-exclusive AntiAlias preset (0/1/2) into the decoupled
                    // MSAA + supersampling fields the first time we see a config without them.
                    if (defaultInstance.MSAALevel < 0)
                    {
                        switch (defaultInstance.AntiAlias)
                        {
                            case 1: defaultInstance.MSAALevel = 4; defaultInstance.SuperSampling = 1; break; //was MSAA4x
                            case 2: defaultInstance.MSAALevel = 0; defaultInstance.SuperSampling = 2; break; //was SSAA2x
                            default: defaultInstance.MSAALevel = 0; defaultInstance.SuperSampling = 1; break; //off
                        }
                        defaultInstance.Save();
                    }
                }
                return defaultInstance;
            }
        }

        private static string GenerateGUID()
        {
            return Guid.NewGuid().ToString();
        }


        public GlobalSettings(string path) : base(path) { }

        private Dictionary<string, string> _DefaultValues = new Dictionary<string, string>()
        {
            { "ShowHints", "true"},
            { "CurrentLang", "english" },
            { "ClientVersion", "0"},
            { "DebugEnabled", "false"},
            { "ScaleUI", "false"},
            { "CityShadows", "false"},
            { "ShadowQuality", "2048"},
            { "SmoothZoom", "true"},
            { "AntiAlias", "0"}, //legacy AA preset (0/1/2). Kept in sync as a summary for UI/icon render targets.
            // Decoupled AA pipeline. MSAALevel (-1 = "unset", migrated from AntiAlias on first load).
            { "MSAALevel", "-1"},        //hardware MSAA samples: 0/2/4/8
            { "SuperSampling", "1"},     //legacy supersample factor: 1 (off) or 2; superseded by RenderScale, kept in sync
            { "RenderScale", "1"},       //render-scale slider: <1 upscales (FSR/EASU), >1 supersamples (downsample resolve)
            { "PostAA", "0"},            //post-process AA: 0=Off, 1=FXAA, 2=SMAA-Low, 3=SMAA-High (shader pass; built on Windows)
            { "Sharpen", "0"},           //resolve sharpening: 0=Bilinear, 1=FSR (EASU+RCAS) (shader pass; built on Windows)
            { "SharpenAmount", "0.25"},  //RCAS sharpening strength, 0..1
            { "TAA", "false"},           //temporal AA (3D mode only; needs velocity buffer)
            { "MotionBlur", "0"},        //0=Off, 1=Camera (2D zoom/pan), 2=PerPixel (3D, needs velocity)
            { "MotionBlurAmount", "0.5"},//motion blur strength 0..1
            { "Bloom", "false"},         //threshold bright-pass bloom (post-process)
            { "BloomThreshold", "1.0"},  //bloom luminance threshold (0..2)
            { "BloomIntensity", "0.5"},  //bloom composite strength (0..1, shader scales internally)
            { "AO", "false"},            //GTAO ambient occlusion (3D only)
            { "AORadius", "0.5"},        //world-space AO sample radius
            { "AOIntensity", "1.0"},     //AO composite strength (0..2)
            { "VelocityDebug", "false"}, //diagnostic: render the MRT1 velocity buffer to screen (3D only)
            { "EdgeScroll", "true"},
            { "Lighting", "true"},
            { "FXVolume", "10"},
            { "MusicVolume", "10"},
            { "VoxVolume", "10"},
            { "AmbienceVolume", "8"},
            { "StartupPath", ""},
            { "DocumentsPath", ""},
            { "Windowed", "true"},
            { "GraphicsWidth", "1024"},
            { "GraphicsHeight", "768"},
            { "LastUser", ""},
            { "SkipIntro", "true"},
            { "DebugHead", "0"},
            { "DebugBody", "0"},
            { "DebugGender", "true"},
            { "DebugSkin", "0"},
            { "LanguageCode", "1"},
            { "SurroundingLotMode", "2" },

            { "UseCustomServer", "true" },
            { "ModernCAS", "true" },
            { "GameEntryUrl", "http://api.freeso.org" },
            { "CitySelectorUrl", "http://api.freeso.org" },

            { "TargetRefreshRate", "60" },

            { "TTSMode", "1" }, //disable/allow/force
            { "CompatState", "-1" },

            { "TS1HybridPath", "D:/Games/The Sims/" },
            { "TS1HybridEnable", "false" },
            { "TS1IsSteamInstall", "false" },
            { "TS1InstallationConfigured", "false" },

            { "Shadows3D", "false" },
            { "CitySkybox", "true" },

            { "LightingMode", "-1" },
            { "Weather", "true" },
            { "DirectionalLight3D", "true" },
            { "DPIScaleFactor", "1" },
            { "TexCompression", "0" },

            { "ChatColor", "0" }, //uint packed color. 0 means choose random
            { "ChatTTSPitch", "0" }, //-100 to 100
            { "ChatOnlyEmoji", "false" },
            { "ChatShowTimestamp", "false" },
            { "ChatSizeX", "400" },
            { "ChatSizeY", "255" },
            {"ChatLocationX", "20" },
            {"ChatLocationY", "20" },
            {"ChatDeltaScale", "8" },
            { "ChatWindowsOpacity", "0.8" },

            { "ComplexShaders", "false" },
            { "GlobalGraphicsMode", "0" }, //2d, 2d hybrid, 3d
            { "EnableTransitions", "true" },

            { "ArchiveServerGUID", "" },
            { "ArchiveClientGUID", "" },
        };

        public override Dictionary<string, string> DefaultValues
        {
            get { return _DefaultValues; }
            set { _DefaultValues = value; }
        }

        public string CurrentLang { get; set; }
        public string ClientVersion { get; set; }
        public bool CityShadows { get; set; }
        public int ShadowQuality { get; set; }
        public bool SmoothZoom { get; set; }
        public int AntiAlias { get; set; } //legacy AA preset summary (0/1/2), kept in sync for UI/icon render targets
        public int MSAALevel { get; set; } //hardware MSAA samples: 0/2/4/8
        public int SuperSampling { get; set; } //legacy supersample factor: 1 (off) or 2; kept in sync with RenderScale
        public float RenderScale { get; set; } //render scale: <1 upscale (FSR/EASU), 1 native, >1 supersample
        public int PostAA { get; set; } //0=Off, 1=FXAA, 2=SMAA-Low, 3=SMAA-High
        public int Sharpen { get; set; } //0=Bilinear, 1=FSR (EASU+RCAS)
        public float SharpenAmount { get; set; } //RCAS strength 0..1
        public bool TAA { get; set; } //temporal anti-aliasing (3D, needs velocity buffer)
        public int MotionBlur { get; set; } //0=Off, 1=Camera (2D), 2=PerPixel (3D)
        public float MotionBlurAmount { get; set; } //motion blur strength 0..1
        public bool Bloom { get; set; } //threshold bright-pass bloom
        public float BloomThreshold { get; set; } //bloom luminance threshold
        public float BloomIntensity { get; set; } //bloom composite strength
        public bool AO { get; set; } //GTAO ambient occlusion (3D only)
        public float AORadius { get; set; } //world-space AO sample radius
        public float AOIntensity { get; set; } //AO composite strength
        public bool VelocityDebug { get; set; } //diagnostic: visualize MRT1 velocity buffer to screen
        public bool EdgeScroll { get; set; }
        public bool Lighting { get; set; }
        public byte FXVolume { get; set; }
        public byte MusicVolume { get; set; }
        public byte VoxVolume { get; set; }
        public byte AmbienceVolume { get; set; }
        public string StartupPath { get; set; }
        public string DocumentsPath { get; set; }
        public bool Windowed { get; set; }
        public int GraphicsWidth { get; set; }
        public int GraphicsHeight { get; set; }
        public string LastUser { get; set; }
        public bool SkipIntro { get; set; }
        public ulong DebugHead { get; set; }
        public ulong DebugBody { get; set; }
        public bool DebugGender { get; set; }
        public int DebugSkin { get; set; }
        public byte LanguageCode { get; set; }

        public bool UseCustomServer { get; set; }
        public bool ModernCAS { get; set; }
        public string GameEntryUrl { get; set; }
        public string CitySelectorUrl { get; set; }

        public int TargetRefreshRate { get; set; }
        public int TTSMode { get; set; } //disable/allow/force
        public int SurroundingLotMode { get; set; }
        public int CompatState { get; set; }

        public string TS1HybridPath { get; set; }
        public bool TS1HybridEnable { get; set; }
        public bool TS1IsSteamInstall { get; set; }
        public bool TS1InstallationConfigured { get; set; }

        public bool Shadows3D { get; set; }
        public bool CitySkybox { get; set; }

        public int LightingMode { get; set; }

        public bool Weather { get; set; }
        public bool DirectionalLight3D { get; set; }
        public float DPIScaleFactor { get; set; }
        public int TexCompression { get; set; } //first bit on/off, second bit is user defined or auto.

        public uint ChatColor { get; set; }
        public int ChatTTSPitch { get; set; }
        public int ChatOnlyEmoji { get; set; }
        public bool ChatShowTimestamp { get; set; }
        public float ChatSizeX { get; set; }
        public float ChatSizeY { get; set; }
        public float ChatLocationX { get; set; }
        public float ChatLocationY { get; set; }
        public int ChatDeltaScale { get; set; }
        public float ChatWindowsOpacity { get; set; }

        public bool ComplexShaders { get; set; }
        public int GlobalGraphicsMode { get; set; }
        public bool EnableTransitions { get; set; }


        public string ArchiveClientGUID { get; set; }

        public static int TARGET_COMPAT_STATE = 2;
    }
}
