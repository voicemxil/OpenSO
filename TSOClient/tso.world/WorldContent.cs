using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common;
using FSO.LotView.Effects;

namespace FSO.LotView
{
    /// <summary>
    /// Handles XNA content for the world.
    /// </summary>
    public static class WorldContent
    {
        public static ContentManager ContentManager;
        private static BasicEffect _be;

        public static void Init(GameServiceContainer serviceContainer, string rootDir)
        {
            ContentManager = new ContentManager(serviceContainer);
            ContentManager.RootDirectory = rootDir;

            LoadEffects(false);
        }

        public static void LoadEffects(bool reload)
        {
            _2DWorldBatchEffect = new WorldBatchEffect(ContentManager.Load<Effect>("Effects/2DWorldBatch" + EffectSuffix));
            Grad2DEffect = new GradEffect(ContentManager.Load<Effect>("Effects/gradpoly2D"));
            Light2DEffect = new LightMap2DEffect(ContentManager.Load<Effect>("Effects/LightMap2D"));
            GrassEffect = new GrassEffect(ContentManager.Load<Effect>("Effects/GrassShader" + EffectSuffix));
            RCObject = new RCObjectEffect(ContentManager.Load<Effect>("Effects/RCObject" + EffectSuffix));
            SSAA = ContentManager.Load<Effect>("Effects/SSAA");
            // Post-process AA resolve. Optional: only present in content profiles where the shader was
            // built (Windows DX/OGL). Missing -> null, and the AA pipeline falls back to the plain blit.
            try { FXAA = ContentManager.Load<Effect>("Effects/FXAA"); }
            catch { FXAA = null; }
            // FSR (RCAS sharpening). Optional, same as FXAA.
            try { FSR = ContentManager.Load<Effect>("Effects/FSR"); }
            catch { FSR = null; }
            // SMAA (3-pass post-AA) and its precomputed AreaTex/SearchTex lookup textures. Optional, same as
            // FXAA/FSR — if any piece is missing the SMAA path stays off (resolve falls back to FXAA / blit).
            try { SMAA = ContentManager.Load<Effect>("Effects/SMAA"); }
            catch { SMAA = null; }
            SMAAAreaTex = TryLoadPNG("Textures/SMAA_AreaTex.png");
            SMAASearchTex = TryLoadPNG("Textures/SMAA_SearchTex.png");
            // Per-pixel motion blur (3D). Reads color + velocity buffer.
            try { MotionBlur = ContentManager.Load<Effect>("Effects/MotionBlur"); }
            catch { MotionBlur = null; }
            // Temporal AA (3D). Needs velocity buffer + history ping-pong.
            try { TAA = ContentManager.Load<Effect>("Effects/TAA"); }
            catch { TAA = null; }
            // Velocity diagnostic visualizer (3D). Decodes MRT1 to screen color.
            try { VelocityViz = ContentManager.Load<Effect>("Effects/VelocityViz"); }
            catch { VelocityViz = null; }
            // Sky dome shader with velocity output (replaces BasicEffect when velocity is active).
            try { SkyVelocity = ContentManager.Load<Effect>("Effects/SkyVelocity"); }
            catch { SkyVelocity = null; }
            // Bloom (threshold + Kawase dual-filter + composite).
            try { Bloom = ContentManager.Load<Effect>("Effects/Bloom"); }
            catch { Bloom = null; }
            // GTAO (slice-based ambient occlusion).
            try { GTAO = ContentManager.Load<Effect>("Effects/GTAO"); }
            catch { GTAO = null; }
            SpriteEffect = new Effects.SpriteEffect(ContentManager.Load<Effect>("Effects/SpriteEffects" + EffectSuffix));
            ParticleEffect = new LightMappedEffect(ContentManager.Load<Effect>("Effects/ParticleShader"));
            AvatarEffect = new LightMappedEffect(ContentManager.Load<Effect>("Effects/Vitaboy" + EffectSuffix));

            Files.RC.Utils.DepthTreatment.SpriteEffect = SpriteEffect;

            LightEffects = new List<LightMappedEffect>()
            {
                _2DWorldBatchEffect,
                GrassEffect,
                RCObject,
                ParticleEffect,
                AvatarEffect
            };
        }

        public static List<LightMappedEffect> LightEffects;

        public static string EffectSuffix
        {
            get { return ((FSOEnvironment.GLVer == 2) ?"iOS":""); }
        }

        public static WorldBatchEffect _2DWorldBatchEffect;

        public static GradEffect Grad2DEffect;

        public static LightMap2DEffect Light2DEffect;

        public static GrassEffect GrassEffect;

        public static RCObjectEffect RCObject;

        public static Effect SSAA;

        public static Effect FXAA;

        public static Effect FSR;

        public static Effect SMAA;
        public static Texture2D SMAAAreaTex;
        public static Texture2D SMAASearchTex;

        public static Effect MotionBlur;
        public static Effect TAA;
        public static Effect VelocityViz;
        public static Effect SkyVelocity;
        public static Effect Bloom;
        public static Effect GTAO;

        // Load a PNG from ContentDir as a Texture2D. Returns null if missing/unreadable so the caller can
        // disable the dependent feature (matches the ParticleComponent/AbstractSkyDome pattern).
        private static Texture2D TryLoadPNG(string relPath)
        {
            try
            {
                var gd = ((IGraphicsDeviceService)ContentManager.ServiceProvider.GetService(typeof(IGraphicsDeviceService))).GraphicsDevice;
                using (var fs = File.OpenRead(Path.Combine(FSOEnvironment.ContentDir, relPath)))
                {
                    return Texture2D.FromStream(gd, fs);
                }
            }
            catch { return null; }
        }

        public static Effects.SpriteEffect SpriteEffect;

        public static LightMappedEffect ParticleEffect;

        public static LightMappedEffect AvatarEffect;

        private static VertexBuffer _TextureVerts;
        public static VertexBuffer GetTextureVerts(GraphicsDevice gd) 
        {
            if (_TextureVerts == null)
            {
                var verts = new VertexPositionTexture[4];
                verts[0] = new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 0));
                verts[1] = new VertexPositionTexture(new Vector3(-1, 1, 0), new Vector2(0, 1));
                verts[2] = new VertexPositionTexture(new Vector3(1, -1, 0), new Vector2(1, 0));
                verts[3] = new VertexPositionTexture(new Vector3(1, 1, 0), new Vector2(1, 1));
                _TextureVerts = new VertexBuffer(gd, typeof(VertexPositionTexture), 4, BufferUsage.None);
                _TextureVerts.SetData(verts);
            }
            return _TextureVerts;
        }

        private static VertexBuffer _TextureVertsInv;
        public static VertexBuffer GetTextureVertsInv(GraphicsDevice gd)
        {
            if (_TextureVertsInv == null)
            {
                var verts = new VertexPositionTexture[4];
                verts[0] = new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 0));
                verts[2] = new VertexPositionTexture(new Vector3(-1, 1, 0), new Vector2(0, 1));
                verts[1] = new VertexPositionTexture(new Vector3(1, -1, 0), new Vector2(1, 0));
                verts[3] = new VertexPositionTexture(new Vector3(1, 1, 0), new Vector2(1, 1));
                _TextureVertsInv = new VertexBuffer(gd, typeof(VertexPositionTexture), 4, BufferUsage.None);
                _TextureVertsInv.SetData(verts);
            }
            return _TextureVertsInv;
        }

        public static BasicEffect GetBE(GraphicsDevice gd)
        {
            if (_be == null) _be = new BasicEffect(gd);
            return _be;
        }
    }
}
