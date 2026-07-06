using System.Collections.Generic;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Entities
{
    /// <summary>
    /// Deterministic per-tick system: spawns a private "Social Bunny" NPC next to a Sim with low
    /// Social and no other real player nearby. Runs inside VM.InternalTick on server + all clients.
    /// Stateless by design: unmarshalled fields desync late joiners / lot reloads, so every decision
    /// is recomputed from the shared entity list (PrivateToPersistID) + tickID. There is no respawn
    /// cooldown - the spawn (-50) / despawn (+20) hysteresis is the cooldown.
    /// </summary>
    public class VMSocialBunnySystem
    {
        public const string BUNNY_NAME = "Social Bunny";

        // Motive scale runs roughly -100 to 100.
        private const short SOCIAL_LOW_THRESHOLD = -50;
        private const short SOCIAL_SATISFIED_THRESHOLD = 20;
        private const int NEARBY_PLAYER_RADIUS_TILES = 20;

        // Above any real (DB-assigned) persist id, so bunny ids can't collide with real avatars.
        private const uint EPHEMERAL_PERSIST_BASE = 0xF0000000;

        // Stock interaction names to try, in order. NOT cached: resolving runs the pie menu check
        // trees, which consume the shared random stream - every VM must resolve at spawn time.
        private static readonly string[] TalkInteractionNames = { "Talk", "Chat", "Introduce" };

        public void Tick(VM vm, uint tickID)
        {
            var context = vm.Context;
            var ticksPerMinute = context.Clock.TicksPerMinute;
            if (ticksPerMinute <= 0 || tickID % (uint)ticksPerMinute != 0) return; // once per sim-minute, synced via tickID

            // Snapshot: spawning/despawning below mutates ObjectQueries.Avatars.
            var avatars = new List<VMEntity>(context.ObjectQueries.Avatars);

            // Pass 1 - rebuild the bunny map from shared state; delete orphans and duplicates,
            // re-dress bunnies from older saves.
            var bunniesByTarget = new Dictionary<uint, VMAvatar>();
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar bunny) || bunny.Dead || bunny.PrivateToPersistID == null) continue;

                var targetId = bunny.PrivateToPersistID.Value;
                var bunnyTarget = FindRealPlayer(avatars, targetId);
                if (bunniesByTarget.ContainsKey(targetId) || bunnyTarget == null)
                {
                    bunny.Delete(true, context);
                    continue;
                }

                bunniesByTarget[targetId] = bunny;
                if (bunny.Name != BUNNY_NAME)
                {
                    bunny.Name = BUNNY_NAME;
                    VMSocialBunnySuits.ApplyBunnyCostume(bunny);
                }
                // re-asserted every sim-minute: interactions drain the bunny's motives
                SetupBunnyPsyche(bunny, bunnyTarget);
            }

            // Pass 2 - per real player: despawn a no-longer-needed bunny, or spawn one.
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar avatar) || !IsRealPlayer(avatar)) continue;

                if (bunniesByTarget.TryGetValue(avatar.PersistID, out var bunny))
                {
                    if (ShouldDespawn(avatar, bunny, avatars)) bunny.Delete(true, context);
                    continue;
                }

                if (avatar.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (avatar.GetMotiveData(VMMotive.Social) > SOCIAL_LOW_THRESHOLD) continue;
                if (IsAnyOtherRealPlayerNearby(avatar, avatars)) continue;

                SpawnBunny(avatar, avatars, context);
            }
        }

        private static bool IsRealPlayer(VMAvatar avatar)
        {
            return !avatar.Dead && avatar.PersistID != 0 && !avatar.IsPet
                && avatar.PrivateToPersistID == null
                && avatar.GetPersonData(VMPersonDataVariable.PersonType) < 254;
        }

        private static VMAvatar FindRealPlayer(List<VMEntity> avatars, uint persistID)
        {
            foreach (var ent in avatars)
            {
                if (ent is VMAvatar avatar && avatar.PersistID == persistID && IsRealPlayer(avatar)) return avatar;
            }
            return null;
        }

        private static bool IsAnyOtherRealPlayerNearby(VMAvatar target, List<VMEntity> avatars)
        {
            if (target.Position == LotTilePos.OUT_OF_WORLD) return false;
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar other) || other == target || !IsRealPlayer(other)) continue;
                if (other.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (other.Position.Level != target.Position.Level) continue;

                var dx = other.Position.TileX - target.Position.TileX;
                var dy = other.Position.TileY - target.Position.TileY;
                if (dx * dx + dy * dy <= NEARBY_PLAYER_RADIUS_TILES * NEARBY_PLAYER_RADIUS_TILES) return true;
            }
            return false;
        }

        // True while either has the other queued/active - don't delete the bunny mid-social.
        private static bool BusyWithEachOther(VMAvatar target, VMAvatar bunny)
        {
            if (bunny.Thread != null)
            {
                foreach (var action in bunny.Thread.Queue)
                {
                    if (action.Callee == target || action.StackObject == target) return true;
                }
            }
            if (target.Thread != null)
            {
                foreach (var action in target.Thread.Queue)
                {
                    if (action.Callee == bunny || action.StackObject == bunny) return true;
                }
            }
            return false;
        }

        private static bool ShouldDespawn(VMAvatar target, VMAvatar bunny, List<VMEntity> avatars)
        {
            if (target.Dead || target.Position == LotTilePos.OUT_OF_WORLD) return true; // target left the lot
            if (bunny.Dead) return false; // already gone, nothing to do
            if (BusyWithEachOther(target, bunny)) return false;

            if (target.GetMotiveData(VMMotive.Social) >= SOCIAL_SATISFIED_THRESHOLD) return true;
            if (IsAnyOtherRealPlayerNearby(target, avatars)) return true;

            return false;
        }

        private static void SpawnBunny(VMAvatar target, List<VMEntity> avatars, VMContext context)
        {
            var group = context.CreateObjectInstance(VMAvatar.TEMPLATE_PERSON, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            if (group == null || group.Objects.Count == 0) return;

            var bunny = (VMAvatar)group.Objects[0];
            bunny.PersistID = AllocateEphemeralPersistID(avatars);
            bunny.PrivateToPersistID = target.PersistID;
            bunny.Name = BUNNY_NAME;
            VMSocialBunnySuits.ApplyBunnyCostume(bunny);
            SetupBunnyPsyche(bunny, target);

            if (!VMFindLocationFor.FindLocationFor(bunny, target, context, VMPlaceRequestFlags.Default))
            {
                // no room near the target this tick - clean up and retry later
                bunny.Delete(true, context);
                return;
            }

            // greet the target once; after this the bunny is a normal pie-menu target
            var talk = ResolveTalkInteraction(target, bunny, context);
            if (talk != null)
            {
                target.PushUserInteraction(talk.Value.index, bunny, context, talk.Value.global);
            }
        }

        // Make the bunny accept any social: acceptance checks weigh the receiver's relationship,
        // mood and personality. Cheats=1 also freezes motive decay (VMAvatarMotiveDecay.Tick).
        // Only the bunny's own relationship map is written - writing the player's would leak
        // ephemeral bunny ids into their DB relationship rows.
        private static void SetupBunnyPsyche(VMAvatar bunny, VMAvatar target)
        {
            if (!bunny.MeToPersist.TryGetValue(target.PersistID, out var rel))
            {
                rel = new List<short>();
                bunny.MeToPersist[target.PersistID] = rel;
            }
            while (rel.Count < 2) rel.Add(0);
            rel[0] = 100; // short-term relationship
            rel[1] = 100; // long-term relationship

            // NPC person type (>=254): excluded from visitor counting/greeting and the person grid;
            // socials use AvatarState.Permissions so they still work. GreetStatus 2 = greeted.
            bunny.SetPersonData(VMPersonDataVariable.PersonType, 254);
            bunny.SetPersonData(VMPersonDataVariable.GreetStatus, 2);

            bunny.SetPersonData(VMPersonDataVariable.Cheats, 1);
            bunny.SetPersonData(VMPersonDataVariable.Gender, 1); // matches the female costume set
            bunny.SetPersonData(VMPersonDataVariable.NicePersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.OutgoingPersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.PlayfulPersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.ActivePersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.GenerousPersonality, 1000);

            bunny.SetMotiveData(VMMotive.Mood, 100);
            bunny.SetMotiveData(VMMotive.Energy, 100);
            bunny.SetMotiveData(VMMotive.Comfort, 100);
            bunny.SetMotiveData(VMMotive.Hunger, 100);
            bunny.SetMotiveData(VMMotive.Hygiene, 100);
            bunny.SetMotiveData(VMMotive.Bladder, 100);
            bunny.SetMotiveData(VMMotive.Fun, 100);
            bunny.SetMotiveData(VMMotive.Social, 100);
        }

        private static uint AllocateEphemeralPersistID(List<VMEntity> avatars)
        {
            // derived from shared state - identical on every VM, never reuses a live id
            uint id = EPHEMERAL_PERSIST_BASE;
            foreach (var ent in avatars)
            {
                if (ent.PersistID >= id) id = ent.PersistID + 1;
            }
            return id;
        }

        private static (int index, bool global)? ResolveTalkInteraction(VMAvatar target, VMAvatar bunny, VMContext context)
        {
            var pie = target.GetPieMenu(context.VM, bunny, false, true);
            foreach (var name in TalkInteractionNames)
            {
                foreach (var entry in pie)
                {
                    if (entry.Name != null && entry.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return (entry.ID, entry.Global);
                    }
                }
            }
            return null;
        }
    }
}
