using FSO.Client.UI.Screens;
using FSO.Common.Domain.Realestate;
using FSO.Common.Model;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.Server.Protocol.Electron.Packets;
using FSO.Vitaboy;
using Microsoft.Xna.Framework;
using System.Diagnostics;

namespace FSO.Client.Rendering
{
    class VisualSurroundPuppet
    {
        private SurroundPuppet Puppet;
        private ulong StartTimestamp;

        private SimAvatar TargetAvatar;
        private AvatarComponent TargetAvatarComponent;
        private Dictionary<string, Animation> AnimByName;

        private Blueprint LastBp;
        private bool ReloadPuppet = false;
        private Vector3 LotOffset;

        private uint LotLocation;

        public VisualSurroundPuppet(uint lotLocation)
        {
            LotLocation = lotLocation;
        }

        private void RecalculateOffset(uint parentLocation)
        {
            var loc = MapCoordinates.Unpack(LotLocation).ToPoint();
            var parent = MapCoordinates.Unpack(parentLocation).ToPoint();

            var relative = LotTransitionInfo.RelativeChangeCityToLot(loc - parent);

            var width = LastBp.Width;
            var height = LastBp.Height;

            LotOffset = new Vector3(relative.X * (width - 2), relative.Y * (height - 2), 0);
        }

        public void SetPuppet(SurroundPuppet puppet, ulong startTimestamp)
        {
            Puppet.ApplyDelta(puppet);

            if (puppet.Delta.HasFlag(SurroundPuppetDelta.BodyInfo))
            {
                ReloadPuppet = true;
            }
        }

        public void PreDraw(uint parentLocation, Blueprint bp, ulong renderTimestamp)
        {
            if ((bp != LastBp || ReloadPuppet || TargetAvatar == null) && bp != null)
            {
                if (TargetAvatar == null)
                {
                    TargetAvatar = new SimAvatar(Content.Content.Get().AvatarSkeletons.Get(Puppet.SkeletonName));
                }

                TargetAvatar.Appearance = (AppearanceType)Puppet.SkinTone;
                TargetAvatar.Head = FSO.Content.Content.Get().AvatarOutfits.Get(Puppet.HeadOutfit);
                TargetAvatar.Body = FSO.Content.Content.Get().AvatarOutfits.Get(Puppet.BodyOutfit);
                TargetAvatar.Handgroup = TargetAvatar.Body;

                if (bp != LastBp || TargetAvatarComponent == null)
                {
                    LastBp?.RemoveAvatar(TargetAvatarComponent);

                    TargetAvatarComponent = new()
                    {
                        Avatar = TargetAvatar
                    };

                    bp.AddAvatar(TargetAvatarComponent);

                    LastBp = bp;

                    RecalculateOffset(parentLocation);
                }

                LastBp = bp;
            }

            if (TargetAvatar != null)
            {
                // Update the avatar's position and animation based on the frame timing.
                float fraction = (renderTimestamp - StartTimestamp) / ((float)Stopwatch.Frequency / 30f);

                float totalWeight = 0f;
                foreach (var state in Puppet.Animations)
                {
                    totalWeight += state.Weight;
                    if (!state.EndReached)
                    {
                        float visualFrame = state.CurrentFrame;
                        if (state.PlayingBackwards) visualFrame -= state.Speed * fraction;
                        else visualFrame += state.Speed * fraction;

                        if (!AnimByName.TryGetValue(state.Name, out var anim))
                        {
                            anim = Content.Content.Get().AvatarAnimations.Get(state.Name);
                        }

                        Animator.RenderFrame(TargetAvatar, anim, (int)visualFrame, visualFrame % 1, state.Weight / totalWeight);
                    }
                }

                var pos = Puppet.VisualPositionStart;
                var vel = Puppet.Velocity;
                TargetAvatar.ReloadSkeleton();
                TargetAvatarComponent.Position = new Vector3(pos.X, pos.Y, pos.Z) + fraction * new Vector3(vel.X, vel.Y, vel.Z) + LotOffset;
                TargetAvatarComponent.RadianDirection = (double)(pos.W - Puppet.Velocity.W * fraction);
            }
        }
    }

    public class VisualSurroundPuppets
    {
        private CoreGameScreen Screen;
        private Dictionary<uint, Dictionary<uint, VisualSurroundPuppet>> LotIdToPuppet = [];

        public VisualSurroundPuppets(CoreGameScreen screen)
        {
            Screen = screen;
        }

        public void PreDraw()
        {
            // Try update animation and position for any surround puppets

            uint parentLocation = Screen.VisualVM.TSOState.LotID;
            Blueprint bp = Screen.VisualVM.Context.Blueprint;

            foreach (var lot in LotIdToPuppet)
            {
                foreach (var puppet in lot.Value)
                {
                    puppet.Value.PreDraw(parentLocation, bp, 0);
                }
            }
        }

        public void Process(FSOVMSurroundPuppets message)
        {
            foreach (var lotData in message.Lots)
            {
                if (!LotIdToPuppet.TryGetValue(lotData.LotLocation, out var puppets))
                {
                    puppets = new Dictionary<uint, VisualSurroundPuppet>();
                    LotIdToPuppet[lotData.LotLocation] = puppets;
                }

                foreach (var tick in lotData.Ticks)
                {
                    foreach (var puppet in tick.Puppets)
                    {
                        if (!puppets.TryGetValue(puppet.PersistID, out var visualPuppet))
                        {
                            visualPuppet = new VisualSurroundPuppet(lotData.LotLocation);
                            puppets[puppet.PersistID] = visualPuppet;
                        }

                        visualPuppet.SetPuppet(puppet, 0);
                    }
                }
            }
        }
    }
}
