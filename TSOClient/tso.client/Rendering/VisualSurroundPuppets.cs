using FSO.Client.UI.Screens;
using FSO.Common.Model;
using FSO.LotView.Components;
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

        private Avatar TargetAvatar;
        private AvatarComponent TargetAvatarComponent;
        private Dictionary<string, Animation> AnimByName;

        public void SetPuppet(SurroundPuppet puppet, ulong startTimestamp)
        {
            Puppet.ApplyDelta(puppet);

            if (puppet.Delta.HasFlag(SurroundPuppetDelta.BodyInfo))
            {
                // reload the puppet

                var ava = new SimAvatar(Content.Content.Get().AvatarSkeletons.Get(puppet.SkeletonName));
                ava.Appearance = (AppearanceType)puppet.SkinTone;
                ava.Head = FSO.Content.Content.Get().AvatarOutfits.Get(puppet.HeadOutfit);
                ava.Body = FSO.Content.Content.Get().AvatarOutfits.Get(puppet.BodyOutfit);
                ava.Handgroup = ava.Body;
            }

            TargetAvatarComponent = new()
            {
                Avatar = TargetAvatar
            };
        }

        public void Predraw(ulong renderTimestamp)
        {
            if (TargetAvatar != null)
            {
                // Update the avatar's position and based on the process.
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
                TargetAvatarComponent.Position = new Vector3(pos.X, pos.Y, pos.Z) + fraction * new Vector3(vel.X, vel.Y, vel.Z);
                TargetAvatarComponent.RadianDirection = (double)(pos.W - Puppet.Velocity.W * fraction);
            }
        }
    }

    internal class VisualSurroundPuppets
    {
        private CoreGameScreen Screen;
        private Dictionary<uint, Dictionary<uint, VisualSurroundPuppet>> LotIdToPuppet = [];

        public VisualSurroundPuppets(CoreGameScreen screen)
        {
            Screen = screen;
        }

        public void Predraw()
        {
            // Try update animation and position for any surround puppets

            foreach (var lot in LotIdToPuppet)
            {
                foreach (var puppet in lot.Value)
                {
                    puppet.Value.Predraw(0);
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
                            visualPuppet = new VisualSurroundPuppet();
                            puppets[puppet.PersistID] = visualPuppet;
                        }

                        visualPuppet.SetPuppet(puppet, 0);
                    }
                }
            }
        }
    }
}
