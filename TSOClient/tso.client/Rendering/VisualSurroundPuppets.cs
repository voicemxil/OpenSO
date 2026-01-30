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
    class VisualSurroundPuppet : IDisposable
    {
        private readonly VisualSurroundPuppets Parent;
        private readonly uint LotLocation;

        private SurroundPuppet Puppet;
        private ulong StartTimestamp;

        private SimAvatar TargetAvatar;
        private AvatarComponent TargetAvatarComponent;
        private Dictionary<string, Animation> AnimByName = [];

        private Blueprint LastBp;
        private bool ReloadPuppet = false;
        private Vector3 LotOffset;

        public bool IsLeaving => Puppet.Delta.HasFlag(SurroundPuppetDelta.Leaving);

        public VisualSurroundPuppet(VisualSurroundPuppets parent, uint lotLocation)
        {
            Parent = parent;
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
                    TargetAvatar = new SimAvatar(Content.Content.Get().AvatarSkeletons.Get(Puppet.SkeletonName+".skel"));
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
                    TargetAvatarComponent.blueprint = bp;

                    bp.AddAvatar(TargetAvatarComponent);

                    LastBp = bp;

                    RecalculateOffset(parentLocation); // Can somehow end up wildly negative
                }

                LastBp = bp;
                ReloadPuppet = false;
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
                            anim = Content.Content.Get().AvatarAnimations.Get(state.Name + ".anim");
                        }

                        Animator.RenderFrame(TargetAvatar, anim, (int)visualFrame, visualFrame % 1, state.Weight / totalWeight);
                    }
                }

                var pos = Puppet.VisualPositionStart;
                var vel = Puppet.Velocity;
                bool visible = Puppet.Delta.HasFlag(SurroundPuppetDelta.Leaving) ? !Parent.IsPresentElsewhere(Puppet.PersistID) : true;

                TargetAvatar.ReloadSkeleton();
                TargetAvatarComponent.Position = new Vector3(pos.X, pos.Y, pos.Z) + fraction * new Vector3(vel.X, vel.Y, vel.Z) + LotOffset;
                TargetAvatarComponent.RadianDirection = (double)(pos.W - Puppet.Velocity.W * fraction);
                TargetAvatarComponent.Visible = visible;
            }
        }

        public void Dispose()
        {
            LastBp?.RemoveAvatar(TargetAvatarComponent);
        }
    }

    public class VisualSurroundPuppets
    {
        private CoreGameScreen Screen;
        private Dictionary<uint, Dictionary<uint, VisualSurroundPuppet>> LotIdToPuppet = [];
        private Blueprint LastBp;
        private readonly HashSet<uint> ExpectedAvatars = [];

        public VisualSurroundPuppets(CoreGameScreen screen)
        {
            Screen = screen;
        }

        private Point CalculateOffset(uint parentLocation, uint lotLocation)
        {
            var loc = MapCoordinates.Unpack(lotLocation).ToPoint();
            var parent = MapCoordinates.Unpack(parentLocation).ToPoint();

            return loc - parent;
        }

        public void PreDraw()
        {
            // Try update animation and position for any surround puppets

            var vm = Screen.VisualVM;

            if (vm == null || !vm.Ready || vm.FSOVAsyncLoading)
            {
                return;
            }

            uint parentLocation = vm.TSOState.LotID;
            Blueprint bp = vm.Context.Blueprint;
            bool bpChanged = bp != LastBp;

            List<uint> lotIdsToDelete = null;

            foreach (var lot in LotIdToPuppet)
            {
                if (bpChanged)
                {
                    var delta = CalculateOffset(parentLocation, lot.Key);

                    if ((delta.X == 0 && delta.Y == 0) || Math.Abs(delta.X) > 1 || Math.Abs(delta.Y) > 1)
                    {
                        lotIdsToDelete ??= [];

                        lotIdsToDelete.Add(lot.Key);

                        continue;
                    }
                }

                foreach (var puppet in lot.Value)
                {
                    puppet.Value.PreDraw(parentLocation, bp, 0);
                }
            }

            if (lotIdsToDelete != null)
            {
                foreach (var id in lotIdsToDelete)
                {
                    if (LotIdToPuppet.TryGetValue(id, out var puppets))
                    {
                        foreach (var puppet in puppets)
                        {
                            puppet.Value.Dispose();
                        }

                        LotIdToPuppet.Remove(id);
                    }
                }
            }

            LastBp = bp;
        }

        public void Process(FSOVMSurroundPuppets message)
        {
            uint myID = Screen.VisualVM?.MyUID ?? 0;

            foreach (var lotData in message.Lots)
            {
                if (!LotIdToPuppet.TryGetValue(lotData.LotLocation, out var puppets))
                {
                    puppets = new Dictionary<uint, VisualSurroundPuppet>();
                    LotIdToPuppet[lotData.LotLocation] = puppets;
                }

                foreach (var tick in lotData.Ticks)
                {
                    ExpectedAvatars.Clear();
                    ExpectedAvatars.UnionWith(puppets.Keys);

                    foreach (var puppet in tick.Puppets)
                    {
                        if (puppet.PersistID == myID)
                        {
                            // You can't see a puppet of yourself...
                            continue;
                        }

                        if (!puppets.TryGetValue(puppet.PersistID, out var visualPuppet))
                        {
                            visualPuppet = new VisualSurroundPuppet(this, lotData.LotLocation);
                            puppets[puppet.PersistID] = visualPuppet;
                        }

                        visualPuppet.SetPuppet(puppet, 0);

                        ExpectedAvatars.Remove(puppet.PersistID);
                    }

                    foreach (var toRemove in ExpectedAvatars)
                    {
                        if (puppets.TryGetValue(toRemove, out var visualPuppet))
                        {
                            visualPuppet.Dispose();
                            puppets.Remove(toRemove);
                        }
                    }
                }
            }
        }
        
        public bool IsPresentElsewhere(uint pid)
        {
            var vm = Screen.VisualVM;
            if (vm.Ready && !vm.FSOVAsyncLoading && vm.GetObjectByPersist(pid) != null)
            {
                return true;
            }

            foreach (var lot in LotIdToPuppet)
            {
                foreach (var puppet in lot.Value)
                {
                    if (!puppet.Value.IsLeaving)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
