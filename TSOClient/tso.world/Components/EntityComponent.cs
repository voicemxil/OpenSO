using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using FSO.LotView.Model;

namespace FSO.LotView.Components
{
    public abstract class EntityComponent : WorldComponent
    {
        private Texture2D _Headline;
        public Texture2D Headline
        {
            get
            {
                return _Headline;
            }
            set
            {
                if (_Headline != value)
                {
                    _Headline = value;
                    if (value == null)
                    {
                        blueprint.HeadlineObjects.Remove(this);
                    }
                    else
                    {
                        blueprint.HeadlineObjects.Add(this);
                    }
                }
            }
        }
        public Blueprint blueprint;
        public Vector3 MTOffset;
        public Vector3 VisualNormal = Vector3.Up;
        public bool UseNormal;
        public Matrix? GroundAlign; //for realigning objects on sloped terrain (optional, for cars)

        protected short _ObjectID;
        public virtual short ObjectID
        {
            get
            {
                return _ObjectID;
            }
            set
            {
                _ObjectID = value;
            }
        } //set this any time it changes so that hit test works.

        private bool _AvatarSolid = true;
        public bool AvatarSolid
        {
            get
            {
                return _AvatarSolid;
            }
            set
            {
                bool oldValue = _AvatarSolid;
                _AvatarSolid = value;

                if (value != oldValue)
                {
                    blueprint?.SM64?.UpdateObject(this);
                }
            }
        }

        public abstract Vector2 GetScreenPos(WorldState world);

        public abstract ushort Room { get; set; }

        public virtual Vector3 GetSLOTPosition(int slot, bool avatar)
        {
            return new Vector3(0, 0, 0);
        }

        public EntityComponent Container;
        public int ContainerSlot;

        protected bool _Visible = true;
        public bool Visible { get { return _Visible; } set { _Visible = value; } }

        /// <summary>
        /// Position of the object in tile units
        /// </summary>
        public override Vector3 Position
        {
            get
            {
                if (Container == null)
                {
                    if (_IdleFramesPct <= 0)
                    {
                        return _Position;
                    }
                    else
                    {
                        return Vector3.Lerp(_Position, SnapSelfPrevious, _IdleFramesPct);
                    }
                }
                else
                {
                    if (_IdleFramesPct <= 0 || PreviousSlotOffset == null)
                    {
                        return Container.GetSLOTPosition(ContainerSlot, false);
                    } else
                    {
                        var oldP = Container.Position + PreviousSlotOffset.Value;
                        var newP = Container.GetSLOTPosition(ContainerSlot, false);
                        if (Vector3.Distance(oldP, newP) > 1.5) oldP = newP;
                        return Vector3.Lerp(newP, oldP, _IdleFramesPct);
                    }
                }
            }
            set
            {
                _UnmoddedPosition = value;
                _Position = value;
                if (blueprint != null) _Position.Z += blueprint.InterpAltitudeWithSubworlds(new Vector3(0.5f, 0.5f, 0) + _Position - MTOffset / 16) + MTOffset.Z / 16f;
                OnPositionChanged();
                _WorldDirty = true;

                blueprint?.SM64?.UpdateObject(this);
            }
        }

        protected int _IdleFrames;
        protected EntityComponent InterpolationOwner;
        protected float _IdleFramesPct;
        public int IdleFrames
        {
            set
            {
                if (value < 20)
                {
                    _IdleFrames = value;
                } else
                {
                    if (_IdleFramesPct < -3) _IdleFrames = 0;
                }
            }
            get {
                return _IdleFrames;
            }
        }
        public Vector3 SnapSelfPrevious;

        public void PrepareSnapInterpolation(EntityComponent ent)
        {
            if (_Position != SnapSelfPrevious)
            {
                InterpolationOwner = ent;
                _IdleFramesPct = 1f;
                SnapSelfPrevious = _Position;
                PreviousSlotOffset = null;
            }
        }

        public Vector3? PreviousSlotOffset;

        public void PrepareSlotInterpolation()
        {
            _IdleFramesPct = 1f;
            SnapSelfPrevious = _Position;
            if (Container != null)
            {
                PreviousSlotOffset = Container.GetSLOTPosition(ContainerSlot, false) - Container.Position;
            }
            else
            {
                PreviousSlotOffset = null;
            }
        }

        private Vector3 _UnmoddedPosition;
        public Vector3 UnmoddedPosition
        {
            get
            {
                return _UnmoddedPosition;
            }
            set
            {
                _UnmoddedPosition = value;
                _Position = value;
                OnPositionChanged();
                _WorldDirty = true;
            }
        }

        public override void Draw(GraphicsDevice device, WorldState world)
        {
            
        }

        public abstract void Preload(GraphicsDevice device, WorldState world);
        public abstract Vector3 GetHeadlinePos();
        public abstract Vector3 GetLookTarget();

        public virtual float GetHeadlineScale()
        {
            return 1f;
        }

        // Previous frame's billboard transform — velocity for the headline quad (see the velocity path
        // in DrawHeadline3D). Invalidated whenever the velocity path doesn't run (prev would be stale).
        private Matrix _PrevHeadlineWorld;
        private bool _PrevHeadlineWorldValid;

        public void DrawHeadline3D(GraphicsDevice device, WorldState world)
        {
            if (Headline == null || Headline.IsDisposed) return;
            var gd = world.Device;

            Vector3 scale;
            Quaternion rotation;
            Vector3 translation;
            world.View.Decompose(out scale, out rotation, out translation);
            var tHead1 = GetHeadlinePos();
            var hScale = GetHeadlineScale();
            var newWorld = Matrix.CreateScale(hScale*Headline.Width / 64f, hScale*Headline.Height / -64f, 1) * Matrix.Invert(Matrix.CreateFromQuaternion(rotation)) * Matrix.CreateTranslation(new Vector3(tHead1.X * 3, 1.6f + tHead1.Z * 3, tHead1.Y * 3)) * this.World;

            // Velocity path (TAA / motion blur active): draw with the BillboardVelocity shader so the
            // billboard writes true per-frame velocity (avatar tracking + bob) and depth to MRT1. Without
            // this the temporal resolve has NO motion evidence for the bubbles — they animate over a
            // zero-velocity signal and the accumulation smears them ("very ghosted" under TAAU).
            var velRT = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget();
            var bbVel = WorldContent.BillboardVelocity;
            if (velRT != null && bbVel != null)
            {
                var mvp = newWorld * world.View * world.Projection; // jittered (TAA sampling)
                // Previous transform x previous UN-jittered ViewProjection; subworld ModelTranslation
                // correction mirrors WorldEntities.DrawAvatars.
                var prevVP = world.PreviousViewProjection;
                if (world.Cameras.ModelTranslation.HasValue)
                    prevVP = Matrix.CreateTranslation(-world.Cameras.ModelTranslation.Value) * prevVP;
                var prevMVP = (_PrevHeadlineWorldValid ? _PrevHeadlineWorld : newWorld) * prevVP;

                var savedRTs = gd.GetRenderTargets();
                var savedBlend = gd.BlendState;
                FSO.Common.Utils.PPXDepthEngine.BindVelocityMRT(gd, velRT);
                // Color keeps its alpha blend; velocity (MRT1) must OVERWRITE (independent blend, with
                // plain-alpha fallback on old GPUs) — same treatment as the sky dome's velocity path.
                gd.BlendState = FSO.Common.Utils.PPXDepthEngine.VelocityColorBlend(gd, BlendState.NonPremultiplied);
                bbVel.Parameters["MVP"]?.SetValue(mvp);
                bbVel.Parameters["PrevMVP"]?.SetValue(prevMVP);
                bbVel.Parameters["JitterNDC"]?.SetValue(world.TAAJitter);
                bbVel.Parameters["BillboardTex"]?.SetValue(Headline);
                bbVel.CurrentTechnique = bbVel.Techniques["DrawBillboard"];
                bbVel.CurrentTechnique.Passes[0].Apply();
                gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
                gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
                gd.SetRenderTargets(savedRTs);
                gd.BlendState = savedBlend;
                _PrevHeadlineWorld = newWorld;
                _PrevHeadlineWorldValid = true;
                return;
            }
            _PrevHeadlineWorldValid = false;

            var effect = WorldContent.GetBE(gd);

            effect.TextureEnabled = true;
            effect.VertexColorEnabled = false;

            effect.DiffuseColor = Color.White.ToVector3();
            effect.World = newWorld;
            effect.Texture = Headline;
            effect.View = world.View;
            effect.Projection = world.Projection;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }

        private Vector3 Project(Vector3 u, Vector3 v)
        {
            return u * (Vector3.Dot(u, v) / Vector3.Dot(u, u));
        }

        private Matrix NormalToMatrix(Vector3 v1, Vector3 v2, Vector3 v3)
        {
            var u1 = v1;
            var u2 = v2 - Project(u1, v2);
            var u3 = (v3 - Project(u1, v3)) - Project(u2, v3);

            u1.Normalize();
            u2.Normalize();
            u3.Normalize();

            return new Matrix(
                new Vector4(u3, 0),
                new Vector4(u1, 0),
                new Vector4(u2, 0),
                new Vector4(0, 0, 0, 1));
        }

        private Matrix NormalToMatrix()
        {
            return NormalToMatrix(VisualNormal, Vector3.Backward, Vector3.Right);
        }

        public override Matrix World
        {
            get
            {
                if (_WorldDirty || (Container != null))
                {
                    var worldPosition = WorldSpace.GetWorldFromTile(Position);
                    _World = Matrix.CreateTranslation(worldPosition);
                    if (UseNormal)
                    {
                        _World = NormalToMatrix() * _World;
                    }
                    _WorldDirty = false;
                }
                return _World;
            }
        }
    }
}
