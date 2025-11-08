using FSO.Common.Model;
using FSO.Common.Utils;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetSimJoinCmd : VMNetCommandBodyAbstract
    {
        public ushort Version = CurVer;

        public override bool AcceptFromClient { get { return false; } }

        public VMNetAvatarPersistState AvatarState;
        public LotTransitionInfo TransitionInfo;

        public static ushort CurVer = 0xFFEE;

        //variables used locally for deferred avatar loading

        public override bool Execute(VM vm)
        {
            if (vm.TS1)
            {
                if (vm.TS1State.CurrentFamily == null) return true;
                var gameState = Content.Content.Get().Neighborhood.GameState;
                var control = vm.Entities.FirstOrDefault(x => x is VMAvatar && !((VMAvatar)x).IsPet && ((VMAvatar)x).GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == vm.TS1State.CurrentFamily?.ChunkID);
                if (control == null)
                {
                    control = vm.Context.CreateObjectInstance((gameState.DowntownSimGUID == 0)?0x32AA2056:gameState.DowntownSimGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject;
                    control?.SetPosition(LotTilePos.FromBigTile(1, 1, 1), Direction.NORTH, vm.Context);
                }
                if (control != null)
                {
                    var ava = (VMAvatar)control;
                    ava.PersistID = ActorUID;
                    ava.AvatarState.Permissions = VMTSOAvatarPermissions.Admin;
                    vm.Context.ObjectQueries.RegisterAvatarPersist(ava, ava.PersistID);
                    vm.SetGlobalValue(3, control.ObjectID);
                }
                return true;
            }
            
            var name = AvatarState.Name.Substring(0, Math.Min(AvatarState.Name.Length, 64));
            var guid = (AvatarState.CustomGUID == 0) ? VMAvatar.TEMPLATE_PERSON : AvatarState.CustomGUID;

            var sim = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH).Objects[0];

            ValidateTransitionInfo(vm);

            bool toMailbox = true;

            if (TransitionInfo != null)
            {
                // The edge tiles on a lot are blank and overlap with the surrounding lot...
                // ...so when wrapping the position, they are subtracted.
                int wOffset = (vm.Context.Architecture.Width - 2) << 4;
                int hOffset = (vm.Context.Architecture.Height - 2) << 4;

                int x = TransitionInfo.AvatarLotTilePosX - TransitionInfo.RelativeChangeX * wOffset;
                int y = TransitionInfo.AvatarLotTilePosY - TransitionInfo.RelativeChangeY * hOffset;

                if (sim.SetPosition(new LotTilePos((short)x, (short)y, 1), Direction.NORTH, vm.Context).Status == VMPlacementError.Success)
                {
                    sim.RadianDirection = TransitionInfo.AvatarDirection;
                    toMailbox = false;
                }
            }

            if (VM.UseWorld) FSO.HIT.HITVM.Get().PlaySoundEvent("lot_enter");

            if (toMailbox)
            {
                var mailbox = vm.Entities.FirstOrDefault(x => (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0));
                if (mailbox != null) VMFindLocationFor.FindLocationFor(sim, mailbox, vm.Context, VMPlaceRequestFlags.Default);
                else sim.SetPosition(LotTilePos.FromBigTile(3, 3, 1), Direction.NORTH, vm.Context);
            }

            sim.PersistID = ActorUID;

            if (vm.Tuning?.GetTuning("aprilfools", 0, 2019) == 1f)
            {
                var sum = AvatarState.Name.Sum(x => x);
                if (sum % 4 == 0) ((VMAvatar)sim).SetPersonData(VMPersonDataVariable.JobPerformance, 50);
                if (sum % 128 == 127) ((VMAvatar)sim).SetPersonData(VMPersonDataVariable.JobPerformance, 2);
            }

            VMAvatar avatar = (VMAvatar)sim;

            if (vm.TSOState.CommunityLot && AvatarState.Permissions < VMTSOAvatarPermissions.Owner)
            {
                if (vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    if (vm.TSOState.BuildRoommates.Contains(AvatarState.PersistID))
                    {
                        AvatarState.Permissions = VMTSOAvatarPermissions.BuildBuyRoommate;
                    }
                    else
                    {
                        AvatarState.Permissions = VMTSOAvatarPermissions.Roommate;
                    }
                }
            }

            AvatarState.Apply(avatar);

            var oldRoomCount = vm.TSOState.Roommates.Count;
            //some off lot changes may have occurred. Keep things up to date if we're caught between database sync points (TODO: right now never, but should happen on every roomie change).
            if (AvatarState.Permissions > VMTSOAvatarPermissions.Visitor && AvatarState.Permissions < VMTSOAvatarPermissions.Admin)
            {
                if (!vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    vm.TSOState.Roommates.Add(AvatarState.PersistID);
                    if (AvatarState.Permissions > VMTSOAvatarPermissions.Roommate)
                        vm.TSOState.BuildRoommates.Add(AvatarState.PersistID);
                    else
                        vm.TSOState.BuildRoommates.Remove(AvatarState.PersistID);
                    VMBuildableAreaInfo.UpdateOverbudgetObjects(vm);
                }
            } else if (AvatarState.Permissions != VMTSOAvatarPermissions.Admin)
            {
                if (vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    vm.TSOState.Roommates.Remove(AvatarState.PersistID);
                    vm.TSOState.BuildRoommates.Remove(AvatarState.PersistID);
                    VMBuildableAreaInfo.UpdateOverbudgetObjects(vm);
                }
            }

            if (oldRoomCount != vm.TSOState.Roommates.Count)
            {
                //mark objects not owned by roommates for inventory transfer
                foreach (var ent in vm.Entities)
                {
                    if (ent is VMGameObject && ent.PersistID > 0 && ((VMTSOObjectState)ent.TSOState).OwnerID == avatar.PersistID)
                    {
                        var old = ((VMGameObject)ent).Disabled;
                        if (AvatarState.Permissions < VMTSOAvatarPermissions.Roommate) ((VMGameObject)ent).Disabled |= VMGameObjectDisableFlags.PendingRoommateDeletion;
                        else ((VMGameObject)ent).Disabled &= ~VMGameObjectDisableFlags.PendingRoommateDeletion;
                        if (old != ((VMGameObject)ent).Disabled) vm.Scheduler.RescheduleInterrupt(ent);
                        ((VMGameObject)ent).RefreshLight();
                    }
                }
            }

            vm.Context.ObjectQueries.RegisterAvatarPersist(avatar, avatar.PersistID);
            if (ActorUID == uint.MaxValue - 1)
            {
                // Old code for the invisible "server" sim from sandbox server days.
                avatar.SetValue(VMStackObjectVariable.Hidden, 1);
                avatar.SetPosition(LotTilePos.OUT_OF_WORLD, Direction.NORTH, vm.Context);
                avatar.SetFlag(VMEntityFlags.HasZeroExtent, true);
                avatar.SetPersonData(VMPersonDataVariable.IsGhost, 1); //oooooOOooooOo
            }

            vm.SignalChatEvent(new VMChatEvent(avatar, VMChatEventType.Join, avatar.Name));

            if (toMailbox)
            {
                var oow = vm.Context.ObjectQueries.GetObjectsAt(LotTilePos.OUT_OF_WORLD);
                if (oow != null)
                {
                    foreach (var obj in oow)
                    {
                        obj.ExecuteNamedEntryPoint("CT - FSO Player Joined", vm.Context, true, obj, new short[] { avatar.ObjectID, 0, 0, 0 });
                    }
                }
            }
            else
            {
                if (TransitionInfo.Type == LotTransitionType.DirectControl)
                {
                    avatar.SetPersonData(VMPersonDataVariable.UnusedAndDoNotUse2, 32767); // Enable direct control
                    avatar.Thread.EnsureDirectControlAction();
                }
                else if (TransitionInfo.Type == LotTransitionType.Routing)
                {
                    VMNetGotoCmd.QueueGoto(vm, avatar, new LotTilePos((short)TransitionInfo.RoutingLotTilePosX, (short)TransitionInfo.RoutingLotTilePosY, 1));
                }
            }

            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return !FromNet; //can only be sent out by server
        }

        private void ValidateTransitionInfo(VM vm)
        {
            if (TransitionInfo != null)
            {
                if (TransitionInfo.RelativeChangeX < -1 || TransitionInfo.RelativeChangeX > 1 ||
                    TransitionInfo.RelativeChangeY < -1 || TransitionInfo.RelativeChangeY > 1 ||
                    (TransitionInfo.RelativeChangeX == 0 && TransitionInfo.RelativeChangeY == 0))
                {
                    TransitionInfo = null;
                    return;
                }

                int w = vm.Context.Architecture.Width << 4;
                int h = vm.Context.Architecture.Height << 4;

                int acceptableMargin = 16;

                // Needs to be on the correct side for the relative change info

                TransitionInfo.AvatarDirection = (float)DirectionUtils.Normalize(TransitionInfo.AvatarDirection);

                if (float.IsNaN(TransitionInfo.AvatarDirection) || float.IsInfinity(TransitionInfo.AvatarDirection))
                {
                    TransitionInfo.AvatarDirection = 0;
                }

                if (TransitionInfo.Type == LotTransitionType.Routing)
                {
                    // TODO !!
                    // Check if target tile position is in bounds
                    // If the tile position is occupied, go to the mailbox.
                }
            }
        }

        public static void PutTransition(BinaryWriter output, LotTransitionInfo info)
        {
            output.Write(info.BeforeLocation);
            output.Write(info.RelativeChangeX);
            output.Write(info.RelativeChangeY);

            output.Write(info.AvatarLotTilePosX);
            output.Write(info.AvatarLotTilePosY);
            output.Write(info.AvatarDirection);

            output.Write((int)info.Type);
            output.Write(info.RoutingTargetLocation);
            output.Write(info.RoutingLotTilePosX);
            output.Write(info.RoutingLotTilePosY);
        }

        public static LotTransitionInfo GetTransition(BinaryReader input)
        {
            return new LotTransitionInfo()
            {
                BeforeLocation = input.ReadUInt32(),
                RelativeChangeX = input.ReadInt32(),
                RelativeChangeY = input.ReadInt32(),

                AvatarLotTilePosX = input.ReadInt32(),
                AvatarLotTilePosY = input.ReadInt32(),
                AvatarDirection = input.ReadSingle(),

                Type = (LotTransitionType)input.ReadInt32(),
                RoutingTargetLocation = input.ReadUInt32(),
                RoutingLotTilePosX = input.ReadInt32(),
                RoutingLotTilePosY = input.ReadInt32(),
            };
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Version);
            AvatarState.SerializeInto(writer);

            writer.Write(TransitionInfo != null);
            if (TransitionInfo != null)
            {
                PutTransition(writer, TransitionInfo);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Version = reader.ReadUInt16();
            AvatarState = new VMNetAvatarPersistState();
            AvatarState.Deserialize(reader);

            var hasTransition = reader.ReadBoolean();
            if (hasTransition)
            {
                TransitionInfo = GetTransition(reader);
            }
        }
        #endregion
    }
}
